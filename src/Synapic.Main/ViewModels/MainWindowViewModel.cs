using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views;
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
        IHelpService? help = null)
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

        Wizard = new WizardViewModel(_session, _sidecar, connectionStore, engineStore, presetStore);

        // The start screen's three route cards are gated on the source panel
        // having something usable to work on; the panel lives on Step 1, so its
        // readiness has to travel up to the shell's binding.
        Wizard.Step1.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Step1DatasourceViewModel.HasUsableSource))
                OnPropertyChanged(nameof(CanStartRoute));
        };
    }

    public WizardViewModel Wizard { get; }

    // ── Routes: the app opens on a start screen with three entry points ──

    public const string HomeRoute = "home";
    public const string TaggingRoute = "tagging";
    public const string DedupRoute = "dedup";
    public const string UpscaleRoute = "upscale";

    /// <summary>Which entry point is active: home (chooser), tagging, dedup or upscale.</summary>
    [ObservableProperty]
    private string _route = HomeRoute;

    public bool IsHomeVisible => Route == HomeRoute;

    /// <summary>
    /// The start screen's gate: the three workflow cards stay disabled until the
    /// source panel has a usable source — an existing local folder, or a live
    /// Daminion session — so a route is never entered without knowing what it
    /// would work on.
    /// </summary>
    public bool CanStartRoute => Wizard.Step1.HasUsableSource;

    public bool IsWizardVisible => Route != HomeRoute;

    public bool IsTaggingRoute => Route == TaggingRoute;

    public bool IsDedupRoute => Route == DedupRoute;

    public bool IsUpscaleRoute => Route == UpscaleRoute;

    /// <summary>Route name shown in the nav bar so you always know which workflow you're in.</summary>
    public string RouteTitle => Route switch
    {
        TaggingRoute => "Tagging",
        DedupRoute => "Deduplication",
        UpscaleRoute => "Upscaling",
        _ => "",
    };

    partial void OnRouteChanged(string value)
    {
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsWizardVisible));
        OnPropertyChanged(nameof(IsTaggingRoute));
        OnPropertyChanged(nameof(IsDedupRoute));
        OnPropertyChanged(nameof(IsUpscaleRoute));
        OnPropertyChanged(nameof(RouteTitle));
        OnPropertyChanged(nameof(IsNavHomeActive));
    }

    /// <summary>Which sidebar entry reads as the current one on the start screen.</summary>
    public bool IsNavHomeActive => Route == HomeRoute;

    // ── Engine settings as a modal (proposal §2.2.3) ───────────────────

    /// <summary>
    /// Opens the engine settings dialog over whatever is on screen. It hosts a
    /// second view of <see cref="WizardViewModel.Step2"/> — one view model, two
    /// views — so an edit in the dialog is the same edit the wizard step shows,
    /// and every help anchor inside Step2Engine keeps working untouched.
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        try
        {
            var dialog = new Views.Settings.EngineSettingsDialog
            {
                DataContext = Wizard.Step2,
            };

            // Prefer the running window; fall back to DI (both are unavailable
            // in headless tests, where a non-modal show is the right outcome).
            var owner = (Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow as MainWindow;

            if (owner is null)
            {
                try { owner = App.Services.GetService(typeof(MainWindow)) as MainWindow; }
                catch { /* App.Services is not built yet */ }
            }

            if (owner is not null)
                _ = dialog.ShowDialog(owner);
            else
                dialog.Show();
        }
        catch (Exception e)
        {
            // Never let a missing application lifetime break the shell.
            SynapicLog.Warning(nameof(MainWindowViewModel), $"Could not open settings: {e.Message}");
        }
    }

    /// <summary>Start screen → the four-step tagging wizard.</summary>
    [RelayCommand]
    private void StartTaggingRoute()
    {
        Route = TaggingRoute;
        Wizard.EnterTaggingRoute();
        SynapicLog.Info(nameof(MainWindowViewModel), "Route selected: Tagging");
    }

    /// <summary>Start screen → Datasource, then the deduplication step.</summary>
    [RelayCommand]
    private void StartDedupRoute()
    {
        Route = DedupRoute;
        Wizard.EnterDedupRoute();
        SynapicLog.Info(nameof(MainWindowViewModel), "Route selected: Deduplication");
    }

    /// <summary>Start screen → Datasource, then the upscaling step (Feature enhancement).</summary>
    [RelayCommand]
    private void StartUpscaleRoute()
    {
        Route = UpscaleRoute;
        Wizard.EnterUpscaleRoute();
        SynapicLog.Info(nameof(MainWindowViewModel), "Route selected: Upscaling");
    }

    /// <summary>Back to the start screen (route state is kept, so returning resumes).</summary>
    [RelayCommand]
    private void GoHome()
    {
        Route = HomeRoute;
        SynapicLog.Info(nameof(MainWindowViewModel), "Returned to the start screen");
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
    /// that panel is what is gating them, otherwise the wizard step on screen.
    /// The window's key handler asks <see cref="Synapic.Main.Services.HelpScope"/>
    /// first, so focus inside an annotated scope (a section, or one setting)
    /// opens that scope's topic instead; this is what applies when focus sits
    /// in nothing annotated.
    /// </summary>
    [RelayCommand]
    private void OpenContextHelp() => OpenTopic(ContextHelpTopic);

    /// <summary>The topic <see cref="OpenContextHelpCommand"/> resolves to for the current state.</summary>
    public string ContextHelpTopic => IsSidecarRequired
        ? HelpTopics.FirstRunSidecar
        : HelpTopics.ForStepIndex(Wizard.CurrentStepIndex);

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
        while (LogEntries.Count > 2000) LogEntries.RemoveAt(0);
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
        while (LogEntries.Count > 2000) LogEntries.RemoveAt(0);
    }
}
