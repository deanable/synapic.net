using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Indexer.Contract;
using Synapic.Indexer.Contract.Configuration;
using Synapic.Main.Services.Indexer;

namespace Synapic.Main.ViewModels.Settings;

/// <summary>
/// The Service tab's view model: install/lifecycle for the background indexer
/// service, the watch folders that feed it, and a live view of its durable
/// queue. Every elevated action is one launch (UAC) — the indexer's own verbs
/// do the work — and everything the tab shows is read unelevated through the
/// shared contract, so the GUI and the service can never disagree about what
/// the queue holds or what the schedule says.
/// </summary>
public partial class IndexerServiceViewModel : ObservableObject
{
    /// <summary>The detection modes the service understands, as the UI names them.</summary>
    public static readonly IReadOnlyList<string> ModeOptions = new[] { "Interval", "Watch", "IntervalAndWatch" };

    private readonly IIndexerServiceMonitor _monitor;
    private readonly IIndexerProcessLauncher _launcher;
    private bool _loading;

    public IndexerServiceViewModel(IIndexerServiceMonitor monitor, IIndexerProcessLauncher launcher)
    {
        _monitor = monitor;
        _launcher = launcher;
        Refresh();
    }

    // ------------------------------------------------------------- lifecycle

    [ObservableProperty] private bool _isSupported = true;
    [ObservableProperty] private string _serviceStatusText = "";
    [ObservableProperty] private string _startTypeText = "";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _actionNote = "";

    partial void OnIsSupportedChanged(bool value) => OnCommandStatesChanged();
    partial void OnIsInstalledChanged(bool value) => OnCommandStatesChanged();
    partial void OnIsRunningChanged(bool value)
    {
        OnCommandStatesChanged();
        OnPropertyChanged(nameof(StatusBrush));
    }

    /// <summary>The status dot's colour, the same idiom the sidecar status uses.</summary>
    public Avalonia.Media.IBrush StatusBrush =>
        IsRunning ? Avalonia.Media.Brushes.SeaGreen : Avalonia.Media.Brushes.Gray;

    private bool CanInstall => IsSupported && _launcher.IndexerExecutablePresent && !IsInstalled;
    private bool CanUninstall => IsSupported && IsInstalled;
    private bool CanStart => IsSupported && IsInstalled && !IsRunning;
    private bool CanStop => IsSupported && IsInstalled && IsRunning;

    private void OnCommandStatesChanged()
    {
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install()
    {
        ActionNote = _launcher.LaunchElevated("--install")
            ? "Install requested — Windows will ask for permission; the status follows on the next refresh."
            : "The indexer executable could not be started.";
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private void Uninstall()
    {
        ActionNote = _launcher.LaunchElevated("--uninstall")
            ? "Uninstall requested — Windows will ask for permission; logs and data are kept."
            : "The indexer executable could not be started.";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        ActionNote = _launcher.LaunchServiceControl("start")
            ? "Start requested; the status follows on the next refresh."
            : "sc.exe could not be started.";
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        ActionNote = _launcher.LaunchServiceControl("stop")
            ? "Stop requested; the status follows on the next refresh."
            : "sc.exe could not be started.";
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = IndexerPaths.Default.LogsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActionNote = $"The log folder could not be opened ({e.GetType().Name}).";
        }
    }

    // -------------------------------------------------------- watch folders

    [ObservableProperty] private bool _watchRecursive = true;
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private int _intervalMinutes = 15;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _watchNote = "";
    [ObservableProperty] private string? _selectedWatchPath;

    public ObservableCollection<string> WatchPaths { get; } = new();

    partial void OnWatchRecursiveChanged(bool value) => MarkDirty();
    partial void OnModeIndexChanged(int value) => MarkDirty();
    partial void OnIntervalMinutesChanged(int value) => MarkDirty();

    private void MarkDirty()
    {
        if (_loading) return;
        IsDirty = true;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave => IsSupported && IsDirty && _launcher.IndexerExecutablePresent;

    /// <summary>Called by the view's folder picker; a duplicate is refused with a note.</summary>
    [RelayCommand]
    private void AddWatchFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (WatchPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            WatchNote = $"'{path}' is already watched.";
            return;
        }
        WatchPaths.Add(path);
        WatchNote = "";
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveWatchFolder()
    {
        if (SelectedWatchPath is null) return;
        WatchPaths.Remove(SelectedWatchPath);
        SelectedWatchPath = null;
        MarkDirty();
    }

    /// <summary>
    /// One elevated round trip: the schedule patch is written to the user's temp
    /// folder and applied by the indexer's <c>--apply-config</c>, which validates
    /// before saving and restarts the service if it was running.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var patch = new JsonObject
        {
            ["schedule"] = new JsonObject
            {
                ["watchPaths"] = new JsonArray(
                    WatchPaths.Select(path => (JsonNode?)path).ToArray()),
                ["watchRecursive"] = WatchRecursive,
                ["mode"] = ModeOptions[Math.Clamp(ModeIndex, 0, ModeOptions.Count - 1)],
                ["intervalMinutes"] = IntervalMinutes,
            },
        };

        var patchPath = _launcher.WriteConfigPatch(patch);
        if (patchPath is null)
        {
            WatchNote = "The configuration patch could not be written to the temp folder.";
            return;
        }

        if (!_launcher.LaunchElevated("--apply-config", patchPath))
        {
            WatchNote = "The indexer executable could not be started; nothing was applied.";
            return;
        }

        WatchNote = "Save requested — Windows will ask for permission; the command validates, applies, and restarts the service if it was running.";
        IsDirty = false;
        SaveCommand.NotifyCanExecuteChanged();
    }

    // --------------------------------------------------------------- queue

    [ObservableProperty] private string _queueNote = "";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _inFlightCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _transientFailureCount;
    [ObservableProperty] private int _failedCount;

    public ObservableCollection<string> RecentFailures { get; } = new();
    public ObservableCollection<string> RecentEvents { get; } = new();

    // ------------------------------------------------------------- refresh

    /// <summary>
    /// Re-reads everything the tab shows. The view calls this on activation and
    /// on a five-second timer while the tab is visible; each read is cheap and
    /// never elevated.
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        var status = _monitor.QueryStatus();
        if (status is null)
        {
            IsSupported = false;
            ServiceStatusText = "Windows only";
            StartTypeText = "";
            QueueNote = "The background service runs on Windows.";
            return;
        }

        IsSupported = true;
        IsInstalled = status.Exists;
        IsRunning = status.IsRunning;
        ServiceStatusText = status.Exists ? status.StateText : "not installed";
        StartTypeText = status.Exists ? status.StartTypeText : "";

        var queue = _monitor.ReadQueue();
        if (queue.Summary is null)
        {
            QueueNote = queue.Note ?? "the queue could not be read";
            PendingCount = InFlightCount = CompletedCount = TransientFailureCount = FailedCount = 0;
            RecentFailures.Clear();
            RecentEvents.Clear();
        }
        else
        {
            QueueNote = "";
            PendingCount = queue.Summary.Pending;
            InFlightCount = queue.Summary.InFlight;
            CompletedCount = queue.Summary.Completed;
            TransientFailureCount = queue.Summary.TransientFailure;
            FailedCount = queue.Summary.Failed;
            RecentFailures.Clear();
            foreach (var failure in queue.Summary.RecentFailures)
                RecentFailures.Add($"{failure.Key}  attempts={failure.Attempts}  {OneLine(failure.LastError)}");
            RecentEvents.Clear();
            foreach (var e in queue.Events)
            {
                var arrow = e.FromState is null ? e.ToState : $"{e.FromState} to {e.ToState}";
                RecentEvents.Add($"{TimeOf(e.At)}  {e.FileName}  {arrow}  {OneLine(e.Detail)}");
            }
        }

        if (!IsDirty)
        {
            _loading = true;
            var schedule = _monitor.ReadSchedule();
            if (schedule is not null)
            {
                WatchPaths.Clear();
                foreach (var path in schedule.WatchPaths) WatchPaths.Add(path);
                WatchRecursive = schedule.WatchRecursive;
                IntervalMinutes = schedule.IntervalMinutes;
                ModeIndex = Math.Max(0, ModeOptions.ToList().FindIndex(m =>
                    string.Equals(m, schedule.Mode.Trim(), StringComparison.OrdinalIgnoreCase)));
                WatchNote = "";
            }
            _loading = false;
        }
    }

    private static string TimeOf(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp)) return "";
        // The events carry round-trip UTC stamps; the tab shows the clock time.
        return DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time)
            ? time.ToLocalTime().ToString("HH:mm:ss")
            : timestamp;
    }

    private static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 120 ? single : single[..120];
    }
}
