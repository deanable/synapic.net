using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// Step 3: Process (port of step3_process.py) — progress bar with ETA, live
/// log, abort control; builds the TagRequest from Step 2's engine state and
/// runs the batch through ProcessingOrchestrator.
/// </summary>
public partial class Step3ProcessViewModel : ViewModelBase
{
    private readonly Session _session;
    private readonly IInferenceSidecar _sidecar;
    private readonly Step1DatasourceViewModel _step1;
    private readonly ProcessingOrchestrator _orchestrator;
    private CancellationTokenSource? _cts;
    private PauseTokenSource? _pauseSource;

    public Step3ProcessViewModel(Session session, IInferenceSidecar sidecar, Step1DatasourceViewModel step1)
    {
        _session = session;
        _sidecar = sidecar;
        _step1 = step1;
        _orchestrator = new ProcessingOrchestrator(sidecar, maxDegreeOfParallelism: 4);
    }

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>The sidecar instance the orchestrator uses (Step 4 retries need it).</summary>
    public IInferenceSidecar Sidecar => _sidecar;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = "Idle";

    [ObservableProperty]
    private string _etaText = "";

    [ObservableProperty]
    private string _currentFile = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    public bool IsIdle => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        StartCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken ct)
    {
        IsRunning = true;
        IsPaused = false;
        _session.ResetStats();
        _cts = new CancellationTokenSource();
        _pauseSource = new PauseTokenSource();

        // Reuse the Step 1 authenticated client — creating a fresh, unauthenticated
        // connection would fail every Daminion fetch and metadata write.
        var ds = _step1.ToSelectionForProcessing(_step1.ConnectedClient);
        var request = BuildTagRequest();

        // Fetch phase feedback: Daminion pagination can take seconds, and a
        // cold model adds minutes before the first completion report.
        ProgressPercent = 0;
        EtaText = "";
        CurrentFile = "";
        ProgressText = "Fetching items…";

        var progress = new Progress<ProcessProgress>(p =>
        {
            ProgressPercent = p.Percent;
            CurrentFile = p.CurrentFile;
            ProgressText = p.Total == 0 && p.Processed == 0
                ? "No items matched the current filters"
                : $"{p.Processed}/{p.Total} ({p.Failed} failed)";
            // Python parity (step3_process.py): show "ETA … remaining - … per
            // image" as soon as the first item completes; clear it only when the
            // batch is done. A null ETA (nothing finished yet) leaves the last
            // value in place instead of flickering to empty on every start report.
            if (p.Eta is { } eta && eta > TimeSpan.Zero)
                EtaText = $"ETA {FormatDuration(eta)} remaining — {FormatDuration(p.PerItem)} per image";
            else if (p.Total > 0 && p.Processed >= p.Total)
                EtaText = "";
            // Session stats feed Step 4's summary.
            _session.TotalItems = p.Total;
            _session.ProcessedItems = p.Processed;
            _session.FailedItems = p.Failed;
        });

        var batchStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Run(() => _orchestrator.RunAsync(
                ds,
                request,
                progress,
                async line => AppendLog(line),
                _cts.Token,
                _session.Results,
                _pauseSource.Token,
                _session.Engine.ToTagFieldSelection()));

            // Local usage counters (opt-in): one line per finished batch.
            TelemetryService.Shared.RecordBatch(
                itemsProcessed: _session.ProcessedItems - _session.FailedItems,
                itemsFailed: _session.FailedItems,
                modelId: request.ModelId,
                durationSeconds: batchStopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Batch aborted by user");
        }
        catch (Exception e)
        {
            AppendLog($"Batch failed: {e.Message}");
            SynapicLog.Error(nameof(Step3ProcessViewModel), $"Batch failed: {e}");
        }
        finally
        {
            _pauseSource = null;
            IsPaused = false;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    // Start stays disabled until at least one tag field is selected (Step 2).
    private bool CanStart() => !IsRunning && _session.Engine.HasSelectedTagField;

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private void Abort()
    {
        _cts?.Cancel();
        AppendLog("Aborting…");
    }

    private bool CanAbort() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _pauseSource?.Pause();
        IsPaused = true;
        AppendLog("Paused — running items finishing, no new items start");
    }

    private bool CanPause() => IsRunning && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _pauseSource?.Resume();
        IsPaused = false;
        AppendLog("Resumed");
    }

    private bool CanResume() => IsRunning && IsPaused;

    /// <summary>
    /// Build the /tag request from the engine state (local vs cloud routing
    /// happens in the orchestrator; local uses the sidecar contract).
    /// </summary>
    public TagRequest BuildTagRequest()
    {
        var engine = _session.Engine;
        return new TagRequest
        {
            ModelId = engine.ModelId,
            Task = engine.Task,
            Options = new TagOptions
            {
                ConfidenceThreshold = engine.ConfidenceThreshold,
                ProbabilityMode = engine.ProbabilityMode,
                ProbabilityThreshold = engine.ProbabilityThreshold,
                CandidateLabels = engine.ProbabilityCandidates.Length > 0 ? engine.ProbabilityCandidates : null,
                SystemPrompt = string.IsNullOrEmpty(engine.SystemPrompt) ? null : engine.SystemPrompt,
                MaxNewTokens = 512,
            },
        };
    }

    /// <summary>Human-readable duration (port of step3_process.py._format_duration).</summary>
    private static string FormatDuration(TimeSpan? value)
    {
        var seconds = value is { } v ? Math.Max((long)v.TotalSeconds, 0) : 0;
        var days = seconds / 86400;
        var hours = seconds % 86400 / 3600;
        var minutes = seconds % 3600 / 60;
        var secs = seconds % 60;
        if (days > 0) return $"~{days}d {hours}h";
        if (hours > 0) return $"~{hours}h {minutes}m";
        return $"~{minutes}m {secs}s";
    }

    private void AppendLog(string line)
    {
        // The orchestrator invokes this callback from thread-pool threads
        // (ConfigureAwait(false)); LogLines is bound to the UI, so marshal.
        // Without this, the first log line of a batch deadlocks or throws
        // cross-thread on the bound ListBox and kills the batch silently.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(line));
            return;
        }
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (LogLines.Count > 2000) LogLines.RemoveAt(0);
    }
}
