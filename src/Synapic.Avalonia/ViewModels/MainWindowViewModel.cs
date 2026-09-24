using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.ViewModels;

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
    private readonly Session _session;
    private readonly Func<string?> _findSidecarExecutable;
    private readonly Func<string, string?> _findSidecarVariant;
    private string? _detectedExe;
    private CancellationTokenSource? _healthPollCts;
    private string _lastDownloadStatus = "";

    public MainWindowViewModel(
        IInferenceSidecar sidecar,
        ISidecarBuildService build,
        Session session,
        Func<string?>? sidecarExecutableLocator = null,
        DaminionConnectionStore? connectionStore = null,
        EngineSettingsStore? engineStore = null,
        Func<string, string?>? sidecarVariantLocator = null,
        SystemPromptPresetStore? presetStore = null)
    {
        _sidecar = sidecar;
        _build = build;
        _session = session;
        _findSidecarExecutable = sidecarExecutableLocator ?? InferenceSidecarService.FindExecutable;
        _findSidecarVariant = sidecarVariantLocator ?? InferenceSidecarService.FindExecutableForRid;

        _sidecar.StatusChanged += OnSidecarStatusChanged;

        // Surface Serilog events (server status transitions, sidecar lifecycle)
        // in the UI log panel as well as the file log.
        SynapicLog.UiSink.Emitted -= OnUiLogEmitted;
        SynapicLog.UiSink.Emitted += OnUiLogEmitted;

        Wizard = new WizardViewModel(_session, _sidecar, connectionStore, engineStore, presetStore);
    }

    public WizardViewModel Wizard { get; }

    /// <summary>Snapshot the current wizard+engine state to config.json (Session is the source of truth).</summary>
    public void PersistConfig()
    {
        try
        {
            var config = App.Services.GetService(typeof(Synapic.Avalonia.Services.ConfigService)) as Synapic.Avalonia.Services.ConfigService;
            if (config is null) return;

            var s = _session;
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
                    Theme = config.Load().Ui.Theme,
                    LogLevel = config.Load().Ui.LogLevel,
                    AutoLaunchSidecar = config.Load().Ui.AutoLaunchSidecar,
                    TelemetryEnabled = config.Load().Ui.TelemetryEnabled,
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

    /// <summary>Shows the setup panel while any buildable variant is still missing.</summary>
    public bool IsSidecarPanelVisible => _build.CanBuild && SidecarVariants.Any(v => !v.IsBuilt);

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
        ServerUiState.Detecting => Brushes.Gray,
        _ => Brushes.Black,
    };

    /// <summary>Status-bar hint when the running exe predates the sidecar source.</summary>
    private string StaleServerSuffix => _sidecar.StaleBuildNotice is null
        ? ""
        : " \u2014 stale build, rebuild recommended";

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
            ServerUiState.Stopped => "Server stopped",
            ServerUiState.Starting => "Server starting\u2026 (first launch can take up to 2 minutes)",
            ServerUiState.Running => $"Server running (port {_sidecar.SidecarPort}){StaleServerSuffix}",
            ServerUiState.Error => "Server error \u2014 see log",
            _ => value.ToString(),
        };
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
                SidecarVariants.Add(new SidecarVariantViewModel(rid, DescribeVariant(rid), _build.CanBuild, BuildVariantAsync));
        }

        foreach (var variant in SidecarVariants)
        {
            variant.CanBuild = _build.CanBuild;
            variant.ApplyBuildState(_findSidecarVariant(variant.Rid));
        }

        NotifySidecarSetupChanged();
    }

    /// <summary>
    /// Builds one variant, then re-detects so the workspace unlocks as soon as
    /// the first build lands. Only one build runs at a time: the pipeline
    /// shares a work directory and the build service rejects concurrent runs.
    /// </summary>
    private async Task BuildVariantAsync(SidecarVariantViewModel variant, CancellationToken ct)
    {
        if (!variant.CanBuild || variant.IsBuilding) return;

        foreach (var other in SidecarVariants) other.CanBuild = false;
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
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Server busy/restarting — keep polling.
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
