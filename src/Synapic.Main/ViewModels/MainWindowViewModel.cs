using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.ViewModels.Steps;
using Synapic.Shared.Contracts;

namespace Synapic.Main.ViewModels;

/// <summary>
/// User-facing state of the inference server, shown by the always-visible
/// toolbar indicator: black = not detected (no sidecar executable), red =
/// stopped, orange = starting, green = running, orange-red = error,
/// blue = building.
/// </summary>
public enum ServerUiState
{
    Detecting,
    NotDetected,
    Building,
    Downloading,
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>
/// Main window shell: hosts the wizard steps, the server status indicator,
/// Start/Stop/Build Server controls, background model download progress,
/// and the live log view.
/// On launch the server is detected (executable + any already-running
/// instance) and the Start/Stop buttons are enabled accordingly; when no
/// executable exists a one-off Build Server button appears instead.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IInferenceSidecar _sidecar;
    private readonly ISidecarBuildService _build;
    private readonly ISidecarDownloadService _download;
    private readonly IHelpService _help;
    private readonly Session _session;
    private readonly Func<string?> _findSidecarExecutable;
    private readonly Func<string, string?> _findSidecarVariant;
    private string? _detectedExe;
    private CancellationTokenSource? _healthPollCts;
    private string _lastDownloadStatus = "";
    private string _lastReportedDevice = "";

    /// <param name="computeAvailabilityProbe">
    /// Overridable so a test can state what the machine can run instead of
    /// depending on the GPU under the test runner. It reaches the engine form,
    /// which offers only the devices this answers with — so a fact about the
    /// device the shell compares against can name one this machine would not
    /// offer. Null = probe the machine.
    /// </param>
    public MainWindowViewModel(
        IInferenceSidecar sidecar,
        ISidecarBuildService build,
        Session session,
        Func<string?>? sidecarExecutableLocator = null,
        DaminionConnectionStore? connectionStore = null,
        EngineSettingsStore? engineStore = null,
        Func<string, string?>? sidecarVariantLocator = null,
        SystemPromptPresetStore? presetStore = null,
        ISidecarDownloadService? download = null,
        IHelpService? help = null,
        ConfigService? configService = null,
        Func<ComputeAvailability>? computeAvailabilityProbe = null)
    {
        _sidecar = sidecar;
        _build = build;
        _download = download ?? new SidecarDownloadService();
        _help = help ?? new HelpService();
        _session = session;
        _findSidecarExecutable = sidecarExecutableLocator ?? InferenceSidecarService.FindExecutable;
        _findSidecarVariant = sidecarVariantLocator ?? InferenceSidecarService.FindExecutableForRid;

        _sidecar.StatusChanged += OnSidecarStatusChanged;

        // Surface Serilog events (server status transitions, sidecar lifecycle)
        // in the UI log panel as well as the file log.
        SynapicLog.UiSink.Emitted -= OnUiLogEmitted;
        SynapicLog.UiSink.Emitted += OnUiLogEmitted;

        // The app-wide settings view (ui-design §5), built *before* the wizard:
        // its Defaults section seeds a session that has no saved engine state,
        // and the step view models read the session as they are constructed.
        // Everything else in it is a live view over config.json, so a change
        // takes effect on the run in progress (theme, log level, diagnostics).
        AppSettings = new SettingsViewModel(
            _session,
            configProvider: () => configService ?? ResolveConfigService());
        AppSettings.Shell = this;
        AppSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.ShowDiagnostics))
                OnPropertyChanged(nameof(IsDiagnosticsVisible));
        };
        if (engineStore?.Load() is null) AppSettings.ApplyDefaultsToNewSession();

        Operations = new OperationShellViewModel(
            _session, _sidecar, connectionStore, engineStore, presetStore, computeAvailabilityProbe);
        Operations.NavigationLockChanged += (_, _) => OnPropertyChanged(nameof(IsNavigationLocked));

        // The shared source is the dashboard panels' status line and the header
        // profile: the record count redraws wherever it moves, never a stale
        // number on a panel.
        Operations.Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Step1DatasourceViewModel.CountText))
                OnPropertyChanged(nameof(SourceCountText));
        };

        // The three operation adapters (phase 1) and the shell's one navigation
        // state (D-03/D5): the dashboard panels and the mode commands all funnel
        // through Shell.Open, so "which operation is open" has exactly one
        // answer. There is no second route machine any more — the delegate below
        // *is* the entry point, and Current is the state it writes.
        TagOperation = new TagOperationViewModel(Operations.TagRun, Operations.TagReport);
        DedupOperation = new DedupOperationViewModel(Operations.Dedup);
        UpscaleOperation = new UpscaleOperationViewModel(Operations.Upscale);
        Shell = new ShellViewModel(
            new IOperationViewModel[] { TagOperation, DedupOperation, UpscaleOperation },
            openRoute: OpenOperation,
            goHome: GoHomeCore);
    }

    /// <summary>
    /// The one entry point for opening a mode (ui-design §6.2): the dashboard
    /// panels, the mode commands and <see cref="ShellViewModel.Open"/> all land
    /// here, so entering a mode has a single implementation and a single piece of
    /// state. Throws away nothing — the configured source and the mode's own
    /// parameters survive the trip (the template always shows them).
    /// </summary>
    private void OpenOperation(string key)
    {
        Operations.EnterMode(key, Shell.Current?.Key);
        Shell.SetCurrent(key);
        NotifyNavigationChanged();
    }

    /// <summary>Back to the dashboard: commit the mode being left, then clear Current.</summary>
    private void GoHomeCore()
    {
        Operations.LeaveMode(Shell.Current?.Key);
        Shell.SetCurrent(null);
        NotifyNavigationChanged();
    }

    /// <summary>
    /// The app-wide settings view (ui-design §5): the dashboard's Settings panel
    /// renders this, and it is the only home for app-level settings (D3). Its
    /// Inference server section reaches the server through
    /// <see cref="SettingsViewModel.Shell"/>, which this shell sets.
    /// (Named AppSettings because the shell already has a Settings *command* —
    /// the shortcut that opens this view.)
    /// </summary>
    public SettingsViewModel AppSettings { get; }

    /// <summary>
    /// Diagnostics drawer (ui-design D7): the in-app log view is hidden unless
    /// Settings → Logging turns it on, because no always-visible log strip is
    /// part of the design — an operation's run log lives in its Output region.
    /// </summary>
    public bool IsDiagnosticsVisible => AppSettings.ShowDiagnostics;

    /// <summary>
    /// The app's ConfigService from the DI container, or null when there is none
    /// (tests build the shell directly). Resolved defensively and lazily: the
    /// settings view must not need the container to exist.
    /// </summary>
    private static ConfigService? ResolveConfigService()
    {
        try
        {
            return App.Services?.GetService(typeof(ConfigService)) as ConfigService;
        }
        catch (Exception e)
        {
            SynapicLog.Debug(nameof(MainWindowViewModel), $"No ConfigService in the container: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The operation host: it owns the view models the shared template renders
    /// and hands each mode its Parameters/Run/Report content. Navigation lives
    /// on <see cref="Shell"/>, not here (D5 — there is no step chain).
    /// </summary>
    public OperationShellViewModel Operations { get; }

    /// <summary>The tagging operation as the shared template sees it (phase 1 adapter).</summary>
    public TagOperationViewModel TagOperation { get; }

    /// <summary>The deduplication operation as the shared template sees it (phase 1 adapter).</summary>
    public DedupOperationViewModel DedupOperation { get; }

    /// <summary>The upscaling operation as the shared template sees it (phase 1 adapter).</summary>
    public UpscaleOperationViewModel UpscaleOperation { get; }

    /// <summary>
    /// Dashboard ⇄ operation navigation (D-03/D5): <see cref="ShellViewModel.Current"/>
    /// null means the dashboard, non-null names the open operation. It is the
    /// only navigation state in the app — the route strings, the Is*Route
    /// booleans and the wizard's current step are all gone.
    /// </summary>
    public ShellViewModel Shell { get; }

    // ── Dashboard ⇄ operation, derived from Shell.Current ─────────────────

    /// <summary>The dashboard is what is on screen (nothing is open).</summary>
    public bool IsDashboardVisible => Shell.Current is null;

    /// <summary>An operation is open, so the template is what is on screen.</summary>
    public bool IsOperationVisible => Shell.Current is not null;

    /// <summary>
    /// The open operation's name, or an empty string on the dashboard
    /// (ui-design §6.1 breadcrumb: app title · Dashboard / Tag).
    /// </summary>
    public string OperationTitle => Shell.Current?.Title ?? "";

    /// <summary>The shell chrome's breadcrumb text (§6.1).</summary>
    public string Breadcrumb => Shell.Current is null ? "Dashboard" : $"Dashboard / {Shell.Current.Title}";

    /// <summary>
    /// Run lock (ui-design §6.3): while a batch runs, leaving the mode is
    /// locked — the dashboard entry is disabled and says why. Entering the mode
    /// is free (D6); only the exit is held until the run finishes or is stopped.
    /// </summary>
    public bool IsNavigationLocked => Operations.IsRunning;

    /// <summary>
    /// The dashboard panels' status line (ui-design §3): the shared source's
    /// record count, or the neutral hint until a source is configured — never a
    /// made-up number.
    /// </summary>
    public string SourceCountText => string.IsNullOrWhiteSpace(Operations.Source.CountText)
        ? "no source configured yet"
        : Operations.Source.CountText!;

    /// <summary>
    /// Everything that used to hang off <c>Route</c> now hangs off
    /// <see cref="ShellViewModel.Current"/>: the chrome (dashboard vs template),
    /// the title and the help topic are all derived, so a mode change is one
    /// write (<see cref="ShellViewModel.SetCurrent"/>) and every surface follows.
    /// </summary>
    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(IsDashboardVisible));
        OnPropertyChanged(nameof(IsOperationVisible));
        OnPropertyChanged(nameof(OperationTitle));
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(ContextHelpTopic));
    }

    // ── Settings (ui-design §5/§6.1, D3) ─────────────────────────────────

    /// <summary>
    /// The shell's Settings shortcut (ui-design §6.1): app-wide settings live on
    /// the dashboard's Settings panel, so the shortcut goes there. Every
    /// per-operation parameter is inline in the operation template's Parameters
    /// region instead — the three modal settings dialogs are retired (D3/D-02),
    /// so nothing here builds a window any more.
    /// </summary>
    [RelayCommand]
    private void Settings() => Shell.Home();

    /// <summary>Dashboard → the tagging operation (one template, no step chain).</summary>
    [RelayCommand]
    private void StartTaggingRoute()
    {
        Shell.Open(OperationShellViewModel.TagKey);
        SynapicLog.Info(nameof(MainWindowViewModel), "Mode opened: Tagging");
    }

    /// <summary>Dashboard → the deduplication operation.</summary>
    [RelayCommand]
    private void StartDedupRoute()
    {
        Shell.Open(OperationShellViewModel.DedupKey);
        SynapicLog.Info(nameof(MainWindowViewModel), "Mode opened: Deduplication");
    }

    /// <summary>Dashboard → the upscaling operation (Feature enhancement).</summary>
    [RelayCommand]
    private void StartUpscaleRoute()
    {
        Shell.Open(OperationShellViewModel.UpscaleKey);
        SynapicLog.Info(nameof(MainWindowViewModel), "Mode opened: Upscaling");
    }

    /// <summary>
    /// Back to the dashboard (ui-design §6.2: always available, and it resumes
    /// where you left off — the configured source, the mode's own parameters and
    /// its last output all survive the trip).
    /// </summary>
    [RelayCommand]
    private void GoHome()
    {
        Shell.Home();
        SynapicLog.Info(nameof(MainWindowViewModel), "Returned to the dashboard");
    }

    // ── Help (docs/help; see HelpService) ──────────────────────────────────

    /// <summary>The toolbar Help button: the help home page.</summary>
    [RelayCommand]
    private void OpenHelp() => OpenTopic(HelpTopics.Home);

    /// <summary>Opens one already-resolved topic; the Help button and F1 both funnel through here.</summary>
    [RelayCommand]
    private void OpenTopic(string? topic) => _help.Open(topic);

    /// <summary>
    /// F1 fallback: the topic for the state of the app - the sidecar topic while
    /// that panel is what is gating them, otherwise the open operation's own
    /// topic from the operation contract (<see cref="IOperationViewModel.HelpTopic"/>),
    /// and the help home on the dashboard. The window's key handler asks
    /// <see cref="Synapic.Main.Services.HelpScope"/> first, so focus inside an
    /// annotated scope (a section, or one setting) opens that scope's topic
    /// instead; this is what applies when focus sits in nothing annotated.
    /// </summary>
    [RelayCommand]
    private void OpenContextHelp() => OpenTopic(ContextHelpTopic);

    /// <summary>The topic <see cref="OpenContextHelpCommand"/> resolves to for the current state.</summary>
    public string ContextHelpTopic => IsSidecarRequired
        ? HelpTopics.FirstRunSidecar
        : Shell.Current?.HelpTopic ?? HelpTopics.Home;

    /// <summary>
    /// Snapshot the current wizard+engine state to config.json (Session is the
    /// source of truth). Resolves the app's ConfigService; a DI override keeps
    /// the behaviour directly testable.
    /// </summary>
    public void PersistConfig() => PersistConfig(
        App.Services.GetService(typeof(Synapic.Main.Services.ConfigService))
            as Synapic.Main.Services.ConfigService);

    /// <summary>Core persistence; the DI path delegates here.</summary>
    public void PersistConfig(ConfigService? config)
    {
        if (config is null) return;
        try
        {

            var s = _session;
            var persistedUi = config.Load().Ui;
            config.Save(new AppConfig
            {
                Version = 2,
                Datasource = new DatasourceSettings
                {
                    Type = s.Datasource.Type,
                    LocalPath = s.Datasource.LocalPath,
                    LocalRecursive = s.Datasource.LocalRecursive,
                    Daminion = new DaminionSettings
                    {
                        ServerUrl = s.Datasource.DaminionUrl,
                        Username = s.Datasource.DaminionUser,
                        Scope = s.Datasource.DaminionScope,
                        SearchTerm = s.Datasource.SearchTerm,
                        SavedSearchId = s.Datasource.SavedSearchId,
                        CollectionId = s.Datasource.CollectionId,
                        UntaggedKeywords = s.Datasource.UntaggedKeywords,
                        UntaggedCategories = s.Datasource.UntaggedCategories,
                        UntaggedDescription = s.Datasource.UntaggedDescription,
                        StatusFilter = s.Datasource.StatusFilter,
                        MaxItems = s.Datasource.MaxItems,
                    },
                },
                Engine = new EngineSettings
                {
                    ModelId = s.Engine.ModelId,
                    Task = s.Engine.Task,
                    Device = s.Engine.Device,
                    ConfidenceThreshold = s.Engine.ConfidenceThreshold,
                    ProbabilityMode = s.Engine.ProbabilityMode,
                    ProbabilityThreshold = s.Engine.ProbabilityThreshold,
                    ProbabilityCandidates = s.Engine.ProbabilityCandidates,
                    SystemPrompt = s.Engine.SystemPrompt,
                    UserPrompt = s.Engine.UserPrompt,
                    EmbeddingRescueEnabled = s.Engine.EmbeddingRescueEnabled,
                },
                Processing = new ProcessingSettings
                {
                    MaxItems = s.Datasource.MaxItems,
                    AutoPaginate = true,
                    ResizeScale = s.Datasource.ResizeScale,
                    UseThumbnailOverride = s.Datasource.UseThumbnailOverride,
                },
                Ui = new UiSettings
                {
                    // The Ui block survives saves untouched (ConfigService merges
                    // around it), so one Load carries the user's theme/log prefs.
                    Theme = persistedUi.Theme,
                    LogLevel = persistedUi.LogLevel,
                    AutoLaunchSidecar = persistedUi.AutoLaunchSidecar,
                    TelemetryEnabled = persistedUi.TelemetryEnabled,
                },
            });
            SynapicLog.Info(nameof(MainWindowViewModel), "Persisted wizard+engine config to config.json");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(MainWindowViewModel), $"Failed to persist config: {e.Message}");
        }
    }

    public ObservableCollection<UiLogEvent> LogEntries { get; } = new();

    [ObservableProperty]
    private string _statusText = "Detecting server\u2026";

    [ObservableProperty]
    private ServerUiState _serverState = ServerUiState.Detecting;

    [ObservableProperty]
    private bool _isBusy;

    // ── Background model download panel (shown next to the status) ─────────

    [ObservableProperty]
    private bool _isDownloadVisible;

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    private string _downloadText = "";

    public bool IsServerRunning => ServerState is ServerUiState.Starting or ServerUiState.Running;

    /// <summary>True when no sidecar executable exists and building is possible.</summary>
    public bool IsBuildButtonVisible => ServerState is ServerUiState.NotDetected && _build.CanBuild;

    // ── Per-platform sidecar setup (the sidecar gates the whole workspace) ──

    /// <summary>One row per buildable variant (CPU / CUDA) for the setup panel.</summary>
    public ObservableCollection<SidecarVariantViewModel> SidecarVariants { get; } = new();

    /// <summary>True once a sidecar executable exists, so the server can be started.</summary>
    public bool IsSidecarReady => _detectedExe is not null;

    /// <summary>True when nothing has been built yet - the blocking setup state.</summary>
    public bool IsSidecarRequired => !IsSidecarReady;

    /// <summary>
    /// Shows the setup panel while any variant is still missing, or while a built
    /// one is outdated - a stale executable, or a newer prebuilt on GitHub, is
    /// exactly when the Build/Download pair must be reachable, so the panel must
    /// not hide itself then. Deliberately not gated on CanBuild: a prebuilt
    /// executable can be fetched from the GitHub release, so an installed app
    /// with no build scripts can still pick up the variant it did not ship with
    /// (CUDA beside a CPU-only install).
    /// </summary>
    public bool IsSidecarPanelVisible => SidecarVariants.Any(v => !v.IsBuilt || v.IsStale || v.IsGitHubUpdate);

    /// <summary>
    /// The wizard (datasource, engine, processing, results) is inert without a
    /// sidecar: nothing can be tagged, so the rest of the UI stays disabled
    /// until at least one variant is built.
    /// </summary>
    public bool IsWorkspaceEnabled => IsSidecarReady;

    /// <summary>Status dot color for the always-visible server indicator.</summary>
    public IBrush ServerBrush => ServerState switch
    {
        ServerUiState.Running => Brushes.ForestGreen,
        ServerUiState.Starting => Brushes.Orange,
        ServerUiState.Error => Brushes.OrangeRed,
        ServerUiState.Stopped => Brushes.Red,
        ServerUiState.Building => Brushes.RoyalBlue,
        ServerUiState.Downloading => Brushes.RoyalBlue,
        ServerUiState.Detecting => Brushes.Gray,
        _ => Brushes.Black,
    };

    /// <summary>Status-bar hint when the running exe predates the sidecar source.</summary>
    private string StaleServerSuffix => _sidecar.StaleBuildNotice is null
        ? ""
        : " \u2014 stale build, rebuild recommended";

    // ── Which device the server is really using ────────────────────────────

    /// <summary>
    /// The device <c>/health</c> reports for the loaded model ("CPU", "CUDA",
    /// "MPS"). Empty until the server has loaded a model. Shown beside the
    /// status text: a device the server fell back from is otherwise invisible,
    /// which is how a run could report success while every image was tagged on
    /// the CPU.
    /// </summary>
    [ObservableProperty]
    private string _serverDeviceText = "";

    /// <summary>Set when the server reports a device other than the selected one.</summary>
    [ObservableProperty]
    private string? _deviceNotice;

    public bool IsServerDeviceVisible => ServerDeviceText.Length > 0;

    public bool IsDeviceNoticeVisible => DeviceNotice is not null;

    partial void OnServerDeviceTextChanged(string value) => OnPropertyChanged(nameof(IsServerDeviceVisible));

    partial void OnDeviceNoticeChanged(string? value) => OnPropertyChanged(nameof(IsDeviceNoticeVisible));

    /// <summary>Applies the device a /health payload reports (public for tests).</summary>
    public void ApplyServerDevice(string? reported)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyServerDeviceCore(reported);
        else
            Dispatcher.UIThread.Post(() => ApplyServerDeviceCore(reported));
    }

    private void ApplyServerDeviceCore(string? reported)
    {
        var device = (reported ?? "").Trim();
        ServerDeviceText = device.Length == 0 ? "" : device.ToUpperInvariant();

        var expected = InferenceSidecarService.NormalizeDevice(_session.Engine.Device);
        DeviceNotice = device.Length == 0 || string.Equals(device, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : string.Equals(device, "cpu", StringComparison.OrdinalIgnoreCase) && expected == "cuda"
                // The exact case this notice exists for: the run is on the CPU
                // while the GPU was asked for. The server says why in its own
                // log ("Requested device 'cuda' not available ... falling back
                // to CPU"), and the fix is a sidecar build that has CUDA.
                ? "Inference is running on the CPU, not the GPU: this server has no usable CUDA. "
                    + "Build or download the CUDA variant in the setup panel, then start the server again."
                : $"Inference is running on {device.ToUpperInvariant()} while {expected.ToUpperInvariant()} is selected.";

        // Log once per change, so the file log records which device the batch ran on.
        if (_lastReportedDevice == device.ToLowerInvariant()) return;
        _lastReportedDevice = device.ToLowerInvariant();
        if (device.Length == 0) return;

        if (DeviceNotice is null)
            SynapicLog.Info(nameof(MainWindowViewModel), $"Inference server device: {ServerDeviceText}");
        else
            SynapicLog.Warning(nameof(MainWindowViewModel), $"Inference device mismatch - {DeviceNotice}");
    }

    partial void OnServerStateChanged(ServerUiState value)
    {
        OnPropertyChanged(nameof(IsServerRunning));
        OnPropertyChanged(nameof(IsBuildButtonVisible));
        OnPropertyChanged(nameof(ServerBrush));
        StatusText = value switch
        {
            ServerUiState.Detecting => "Detecting server\u2026",
            ServerUiState.NotDetected => "Server not detected",
            ServerUiState.Building => "Building server\u2026 (first build downloads Python + packages)",
            ServerUiState.Downloading => "Downloading server\u2026",
            ServerUiState.Stopped => "Server stopped",
            ServerUiState.Starting => "Server starting\u2026 (first launch can take up to 2 minutes)",
            ServerUiState.Running => $"Server running (port {_sidecar.SidecarPort}){StaleServerSuffix}",
            ServerUiState.Error => "Server error \u2014 see log",
            _ => value.ToString(),
        };
        // The device shown belongs to the server that just went away.
        if (value is not (ServerUiState.Starting or ServerUiState.Running))
        {
            ServerDeviceText = "";
            DeviceNotice = null;
            _lastReportedDevice = "";
        }
        EnsureHealthPolling(value is ServerUiState.Starting or ServerUiState.Running);
        NotifyCommands();

        // The model list lives in Tagging's Parameters region, so it is fetched
        // as soon as there is a server to ask. Failure is the form's own message;
        // nothing else cares.
        if (value is ServerUiState.Running)
            _ = Operations.TagParameters.EnsureModelsLoadedAsync();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommands();
        foreach (var variant in SidecarVariants) variant.NotifyCommands();
    }

    private void NotifyCommands()
    {
        StartServerCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
        BuildServerCommand.NotifyCanExecuteChanged();
    }

    private void NotifySidecarSetupChanged()
    {
        OnPropertyChanged(nameof(IsSidecarReady));
        OnPropertyChanged(nameof(IsSidecarRequired));
        OnPropertyChanged(nameof(IsSidecarPanelVisible));
        OnPropertyChanged(nameof(IsWorkspaceEnabled));
    }

    private static string DescribeVariant(string rid) =>
        rid.EndsWith("-cuda", StringComparison.OrdinalIgnoreCase)
            ? "Fastest on an NVIDIA GPU; bundles the CUDA 12.6 torch build (~2.5 GB download on first build)."
            : "Runs anywhere (CPU only); the safe default.";

    /// <summary>
    /// Rebuilds the variant list from the buildable RIDs for this machine and
    /// refreshes each row's on-disk state. Cheap enough to call on every
    /// detection (metadata only).
    /// </summary>
    private void RefreshSidecarVariants()
    {
        if (SidecarVariants.Count == 0)
        {
            foreach (var rid in InferenceSidecarService.BuildableRids())
                SidecarVariants.Add(new SidecarVariantViewModel(
                    rid, DescribeVariant(rid), _build.CanBuild, BuildVariantAsync, DownloadVariantAsync));
        }

        var repoRoot = InferenceSidecarService.FindRepoRoot();
        foreach (var variant in SidecarVariants)
        {
            variant.CanBuild = _build.CanBuild;
            // A finished (or never-started) refresh re-arms every way of getting a variant.
            variant.CanDownload = true;
            var exe = _findSidecarVariant(variant.Rid);
            variant.ApplyBuildState(exe, exe is null ? null : InferenceSidecarService.DescribeStaleness(exe, repoRoot));
        }

        NotifySidecarSetupChanged();
    }

    /// <summary>
    /// Builds one variant, then re-detects so the workspace unlocks as soon as
    /// the first build lands. Only one build runs at a time: the pipeline
    /// shares a work directory and the build service rejects concurrent runs.
    /// A running server is stopped first: PyInstaller cannot replace an
    /// executable the OS still holds open (the build would die with
    /// PermissionError only after minutes of packaging).
    /// </summary>
    private async Task BuildVariantAsync(SidecarVariantViewModel variant, CancellationToken ct)
    {
        if (!variant.CanBuild || variant.IsBuilding) return;
        await WithServerStoppedForReplacementAsync(
            () => BuildVariantCoreAsync(variant, ct), ct, "build");
    }

    /// <summary>
    /// Stop the managed server around a replacement of its executable, then
    /// put it back the way the user left it (started again on the new bytes).
    /// Shared by the Build and Download commands, so both paths get the
    /// same lock-avoidance; orphans that outlived a previous run are swept by
    /// the build service's own pre-flight.
    /// </summary>
    private async Task WithServerStoppedForReplacementAsync(Func<Task> replace, CancellationToken ct, string initiator)
    {
        var restart = ServerState is ServerUiState.Running or ServerUiState.Starting;
        if (restart)
        {
            AppendLog($"[{initiator}] Stopping the running server before replacing its executable.");
            await _sidecar.StopAsync();
        }

        try
        {
            await replace();
        }
        finally
        {
            // Whether the replacement landed or failed, put the server back the
            // way the user left it: on the new build, or on the one that works.
            if (restart && IsSidecarReady)
            {
                AppendLog($"[{initiator}] Restarting the server on the replaced sidecar.");
                await StartServerAsync(ct);
            }
        }
    }

    private async Task BuildVariantCoreAsync(SidecarVariantViewModel variant, CancellationToken ct)
    {
        if (!variant.CanBuild || variant.IsBuilding) return;

        foreach (var other in SidecarVariants)
        {
            other.CanBuild = false;
            other.CanDownload = false;
        }
        variant.IsBuilding = true;
        variant.ResetBuildProgress();
        IsBusy = true;
        SetState(ServerUiState.Building);
        try
        {
            // Progress<T> marshals back to this (UI) thread as it is raised.
            var progress = new Progress<SidecarBuildProgress>(variant.ApplyProgress);
            await _build.BuildAsync(variant.Rid, AppendLog, progress, ct);
            AppendLog($"[build] {variant.DisplayName} sidecar built.");
            variant.GitHubUpdateText = null;   // fresh bytes beat whatever GitHub offers
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[build] {variant.DisplayName} build cancelled.");
        }
        catch (Exception e)
        {
            AppendLog($"[build] {variant.DisplayName} build failed: {e.Message}");
        }
        finally
        {
            variant.IsBuilding = false;
            IsBusy = false;
            // Re-detect: restores per-variant availability and, on success,
            // flips the server state out of Building and unlocks the wizard.
            await DetectServerAsync();
        }
    }

    /// <summary>
    /// Fetches one variant from the GitHub release instead of compiling it -
    /// the way out for a machine with no Python/PyInstaller toolchain, and for
    /// an installed app that wants a variant it did not ship with. The row only
    /// offers Download while the variant is missing or outdated, so on an
    /// outdated row this replaces the executable that is already there - which
    /// needs the running server stopped first, exactly like a build (Windows
    /// holds a running exe open, so the file move would fail otherwise).
    /// Mirrors <see cref="BuildVariantAsync"/>: one operation at a time, then
    /// re-detect so the workspace unlocks the moment the file lands.
    /// </summary>
    private async Task DownloadVariantAsync(SidecarVariantViewModel variant, CancellationToken ct)
    {
        if (variant.IsBuilding || variant.IsDownloading) return;
        await WithServerStoppedForReplacementAsync(
            () => DownloadVariantCoreAsync(variant, ct), ct, "download");
    }

    private async Task DownloadVariantCoreAsync(SidecarVariantViewModel variant, CancellationToken ct)
    {
        if (variant.IsBuilding || variant.IsDownloading) return;

        foreach (var other in SidecarVariants)
        {
            other.CanBuild = false;
            other.CanDownload = false;
        }
        variant.IsDownloading = true;
        variant.ResetBuildProgress();
        IsBusy = true;
        SetState(ServerUiState.Downloading);
        try
        {
            var destination = InferenceSidecarService.SidecarInstallPath(variant.Rid);
            var progress = new Progress<SidecarDownloadProgress>(
                p => variant.ApplyProgress(new SidecarBuildProgress(p.Percent, p.Stage)));
            await _download.DownloadAsync(variant.Rid, destination, progress, ct);
            AppendLog($"[download] {variant.DisplayName} sidecar downloaded to {destination}.");
            variant.GitHubUpdateText = null;   // just fetched what GitHub offered
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[download] {variant.DisplayName} download cancelled.");
        }
        catch (Exception e)
        {
            AppendLog($"[download] {variant.DisplayName} download failed: {e.Message}");
        }
        finally
        {
            variant.IsDownloading = false;
            IsBusy = false;
            // Re-detect: picks the new executable up and unlocks the wizard.
            await DetectServerAsync();
        }
    }

    /// <summary>
    /// The always-on GitHub check (run once at startup, after detection has
    /// created the variant rows): for every buildable variant, is there a
    /// prebuilt file on GitHub newer than the executable on disk - or no
    /// executable at all? When yes, the row says so, so the user downloads
    /// instead of building. Advisory by design: any failure logs and returns,
    /// because an unreachable GitHub must never affect startup.
    /// </summary>
    public async Task CheckForSidecarUpdatesAsync(CancellationToken ct = default)
    {
        foreach (var variant in SidecarVariants)
        {
            try
            {
                var info = await _download.CheckForUpdateAsync(
                    variant.Rid,
                    string.IsNullOrEmpty(variant.ExePath) ? null : variant.ExePath,
                    ct);

                if (info is null)
                {
                    variant.GitHubUpdateText = null;
                    continue;
                }

                var published = DateTime.TryParse(
                    info.PublishedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
                    ? $", published {at.ToLocalTime():yyyy-MM-dd}"
                    : "";
                variant.GitHubUpdateText = info.LocalMissing
                    ? $"Prebuilt sidecar available on GitHub ({info.TagName}{published}) \u2014 use Download instead of building."
                    : $"Newer prebuilt sidecar on GitHub ({info.TagName}{published}) \u2014 the row offers Build and Download: Download fetches this prebuilt, Build recompiles from source.";
                AppendLog($"[update] GitHub offers a newer prebuilt {variant.DisplayName} sidecar ({info.TagName}{published}); " +
                          "use Download instead of building.");
                SynapicLog.Info(nameof(MainWindowViewModel),
                    $"Sidecar update available for {variant.Rid}: {info.TagName}, " +
                    $"published {info.PublishedAt ?? "unknown"}, {info.TotalBytes:N0} bytes, localMissing={info.LocalMissing}");
            }
            catch (Exception e)
            {
                SynapicLog.Warning(nameof(MainWindowViewModel),
                    $"Sidecar update check failed for {variant.Rid}: {e.Message}");
            }
        }

        // A GitHub-newer row reopens the setup panel: the note lives inside it,
        // and so do the Build/Download buttons it points at.
        NotifySidecarSetupChanged();
    }

    private void OnSidecarStatusChanged(object? sender, SidecarStatusChangedEventArgs e)
    {
        SetState(e.Status switch
        {
            SidecarStatus.Stopped => ServerUiState.Stopped,
            SidecarStatus.Starting => ServerUiState.Starting,
            SidecarStatus.Ready => ServerUiState.Running,
            SidecarStatus.Error => ServerUiState.Error,
            _ => ServerUiState.Error,
        });
        if (e.Status == SidecarStatus.Error && e.Message is not null)
            AppendLog($"[server] {e.Message}");
    }

    private void SetState(ServerUiState state)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetState(state));
            return;
        }
        if (ServerState != state)
            SynapicLog.Debug(nameof(MainWindowViewModel), $"Server indicator: {ServerState} -> {state}");
        ServerState = state;
    }

    /// <summary>
    /// Startup detection: if the sidecar executable exists, enable Start (or
    /// Stop when an instance is already running); otherwise offer Build Server.
    /// </summary>
    public async Task DetectServerAsync(CancellationToken ct = default)
    {
        string? exe;
        try
        {
            exe = _findSidecarExecutable();
        }
        catch (Exception e)
        {
            AppendLog($"[server] Server detection failed: {e.Message}");
            _detectedExe = null;
            RefreshSidecarVariants();
            SetState(ServerUiState.NotDetected);
            return;
        }

        _detectedExe = exe;
        RefreshSidecarVariants();

        if (exe is null)
        {
            SetState(ServerUiState.NotDetected);
            var missing = string.Join(", ", SidecarVariants.Where(v => !v.IsBuilt).Select(v => v.DisplayName));
            if (_build.CanBuild)
                AppendLog(missing.Length > 0
                    ? $"[server] No inference server built. Build one of: {missing}."
                    : "[server] Inference server not found. Use \"Build Server\" to build it from source (one-time).");
            else
                AppendLog("[server] Inference server not found in this installation.");
            return;
        }

        AppendLog($"[server] Inference server executable: {exe}");
        // Say which device the launch will use, and which variant that picks:
        // the machine's own log is the first place a "selected CUDA, ran on the
        // CPU" surprise used to show up.
        var device = InferenceSidecarService.NormalizeDevice(_session.Engine.Device);
        var launchExe = InferenceSidecarService.FindExecutableForDevice(device);
        AppendLog(launchExe is not null && !string.Equals(launchExe, exe, StringComparison.OrdinalIgnoreCase)
            ? $"[server] Device {device} - this launch runs {launchExe}"
            : $"[server] Device {device}");
        // Strict lifecycle: a fresh launch means the server is NOT running
        // (leftover servers cannot exist - the sidecar runs in a kill-on-close
        // job object). Only our own Start/auto-launch can make it running.
        if (ServerState is not ServerUiState.Starting and not ServerUiState.Running)
            SetState(ServerUiState.Stopped);
    }

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private async Task StartServerAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await _sidecar.StartAsync(ct);
        }
        catch (Exception e)
        {
            AppendLog($"[server] Failed to start: {e.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStartServer() => ServerState is ServerUiState.Stopped or ServerUiState.Error && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopServerAsync()
    {
        IsBusy = true;
        try
        {
            await _sidecar.StopAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStopServer() => IsServerRunning && !IsBusy;

    /// <summary>
    /// Toolbar shortcut: builds the recommended variant (the preferred RID for
    /// this machine). The setup panel offers the explicit per-platform choice.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBuildServer))]
    private async Task BuildServerAsync(CancellationToken ct)
    {
        if (SidecarVariants.Count == 0) RefreshSidecarVariants();

        var target = SidecarVariants.FirstOrDefault(v => !v.IsBuilt) ?? SidecarVariants.FirstOrDefault();
        if (target is null)
        {
            AppendLog("[build] No buildable sidecar variant on this platform.");
            SetState(ServerUiState.NotDetected);
            return;
        }

        await BuildVariantAsync(target, ct);
    }

    private bool CanBuildServer() => ServerState is ServerUiState.NotDetected && !IsBusy && _build.CanBuild;

    // ── Background model download progress (polled from /health) ───────────

    private void EnsureHealthPolling(bool enabled)
    {
        if (enabled)
        {
            if (_healthPollCts is null)
            {
                _healthPollCts = new CancellationTokenSource();
                _ = PollHealthAsync(_healthPollCts.Token);
            }
            return;
        }
        _healthPollCts?.Cancel();
        _healthPollCts = null;
    }

    private async Task PollHealthAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_sidecar.SidecarPort > 0) // idle while the port is still unknown
                {
                    var health = await _sidecar.GetHealthAsync(ct).ConfigureAwait(false);
                    ApplyDownloadStatus(health);
                    ApplyServerDevice(health.Device);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // Server busy/restarting - keep polling, but record why.
                SynapicLog.Debug(nameof(MainWindowViewModel), $"Health poll skipped: {e.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Applies a /health payload to the download panel (public for tests).</summary>
    public void ApplyDownloadStatus(HealthResponse health)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyDownloadStatusCore(health);
        else
            Dispatcher.UIThread.Post(() => ApplyDownloadStatusCore(health));
    }

    private void ApplyDownloadStatusCore(HealthResponse health)
    {
        var d = health.Download;
        if (d is null)
        {
            IsDownloadVisible = false;
            _lastDownloadStatus = "";
            return;
        }

        var status = d.Status switch
        {
            "complete" => "complete",
            "failed" => "failed",
            _ => "downloading",
        };

        DownloadPercent = status == "complete"
            ? 100
            : d.TotalBytes > 0 ? Math.Clamp(100.0 * d.DoneBytes / d.TotalBytes, 0, 100) : 0;

        DownloadText = status switch
        {
            "complete" => $"Model ready: {d.ModelId}",
            "failed" => "Model download failed \u2014 see log",
            _ => d.TotalBytes > 0
                ? $"Downloading model: {Mb(d.DoneBytes)} / {Mb(d.TotalBytes)} MB ({DownloadPercent:F0}%)"
                : $"Downloading model: {Mb(d.DoneBytes)} MB",
        };

        if (_lastDownloadStatus != status)
        {
            switch (status)
            {
                case "complete":
                    AppendLog($"[server] Model downloaded: {d.ModelId}");
                    break;
                case "failed":
                    AppendLog($"[server] Model download failed for {d.ModelId}: {d.Error}");
                    break;
                case "downloading" when _lastDownloadStatus == "":
                    AppendLog($"[server] Downloading model {d.ModelId}\u2026 (progress shown in the toolbar)");
                    break;
            }
        }
        _lastDownloadStatus = status;

        IsDownloadVisible = true;
    }

    private static long Mb(long bytes) => (long)Math.Round(bytes / (1024.0 * 1024.0));

    public void AppendLog(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(line));
            return;
        }
        LogEntries.Add(new UiLogEvent(line, Serilog.Events.LogEventLevel.Information));
        RunLog.Trim(LogEntries);
    }

    /// <summary>Attach the sidecar stdout/stderr stream to the UI log.</summary>
    public void AttachSidecarLog()
    {
        _sidecar.LogReceived -= OnSidecarLog;
        _sidecar.LogReceived += OnSidecarLog;
    }

    /// <summary>
    /// Stop feeding the UI log; called by the shell once its window is gone.
    /// The UI sink is process-wide and Serilog keeps writing during shutdown,
    /// so leaving this attached appends log lines to a collection whose control
    /// no longer exists (the teardown-time layout crash).
    /// </summary>
    public void DetachUiLog()
    {
        SynapicLog.UiSink.Emitted -= OnUiLogEmitted;
        _sidecar.LogReceived -= OnSidecarLog;
    }

    private void OnSidecarLog(string line)
    {
        AppendLog(line);
    }

    private void OnUiLogEmitted(UiLogEvent evt)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnUiLogEmitted(evt));
            return;
        }
        LogEntries.Add(evt);
        RunLog.Trim(LogEntries);
    }
}
