using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Operations;
using Synapic.Shared.Contracts;

namespace Synapic.Main.ViewModels.Steps;

/// <summary>
/// Step 3: Process (port of step3_process.py) — progress bar with ETA, live
/// log, abort control; builds the TagRequest from Step 2's engine state and
/// runs the batch through ProcessingOrchestrator. The run surface
/// (progress/ETA/log/cancellation) is the shared
/// <see cref="RunStateViewModel"/>.
/// </summary>
public partial class Step3ProcessViewModel : RunStateViewModel
{
    private readonly Session _session;
    private readonly IInferenceSidecar _sidecar;
    private readonly Step1DatasourceViewModel _step1;
    private readonly ProcessingOrchestrator _orchestrator;

    public Step3ProcessViewModel(Session session, IInferenceSidecar sidecar, Step1DatasourceViewModel step1)
    {
        _session = session;
        _sidecar = sidecar;
        _step1 = step1;
        _orchestrator = new ProcessingOrchestrator(sidecar, maxDegreeOfParallelism: 4);
    }

    /// <summary>The sidecar instance the orchestrator uses (Step 4 retries need it).</summary>
    public IInferenceSidecar Sidecar => _sidecar;

    protected override void NotifyRunCommandsCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    // Start stays disabled until at least one tag field is selected (Step 2).
    protected override bool CanStart() => !IsRunning && _session.Engine.HasSelectedTagField;

    protected override async Task StartCoreAsync(CancellationToken ct)
    {
        BeginRun();
        PauseSource = new PauseTokenSource();
        _session.ResetStats();

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
            ApplyProgress(p);
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
                Cts!.Token,
                _session.Results,
                PauseSource!.Token,
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
            EndRun();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private void Abort()
    {
        CancelRun();
        AppendLog("Aborting…");
    }

    private bool CanAbort() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        PauseRun();
        AppendLog("Paused — running items finishing, no new items start");
    }

    private bool CanPause() => IsRunning && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        ResumeRun();
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
                // Blank => the sidecar's built-in tag instruction.
                UserPrompt = string.IsNullOrWhiteSpace(engine.UserPrompt) ? null : engine.UserPrompt,
                MaxNewTokens = 512,
            },
        };
    }
}
