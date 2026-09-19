using System.Collections.ObjectModel;
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

    public Step3ProcessViewModel(Session session, IInferenceSidecar sidecar, Step1DatasourceViewModel step1)
    {
        _session = session;
        _sidecar = sidecar;
        _step1 = step1;
        _orchestrator = new ProcessingOrchestrator(sidecar, maxDegreeOfParallelism: 4);
    }

    public ObservableCollection<string> LogLines { get; } = new();

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

    public bool IsIdle => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        StartCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken ct)
    {
        IsRunning = true;
        _session.ResetStats();
        _cts = new CancellationTokenSource();

        // Reuse the Step 1 authenticated client — creating a fresh, unauthenticated
        // connection would fail every Daminion fetch and metadata write.
        var ds = _step1.ToSelectionForProcessing(_step1.ConnectedClient);
        var request = BuildTagRequest();

        var progress = new Progress<ProcessProgress>(p =>
        {
            ProgressPercent = p.Percent;
            CurrentFile = p.CurrentFile;
            ProgressText = $"{p.Processed}/{p.Total} ({p.Failed} failed)";
            EtaText = p.Eta is { } eta ? $"ETA {eta.Minutes}m {eta.Seconds}s" : "";
            // Session stats feed Step 4's summary.
            _session.TotalItems = p.Total;
            _session.ProcessedItems = p.Processed;
            _session.FailedItems = p.Failed;
        });

        try
        {
            await Task.Run(() => _orchestrator.RunAsync(
                ds,
                request,
                progress,
                async line => AppendLog(line),
                _cts.Token,
                _session.Results));
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
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private void Abort()
    {
        _cts?.Cancel();
        AppendLog("Aborting…");
    }

    private bool CanAbort() => IsRunning;

    /// <summary>
    /// Build the /tag request from the engine state (local vs cloud routing
    /// happens in the orchestrator; local uses the sidecar contract).
    /// </summary>
    private TagRequest BuildTagRequest()
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

    private void AppendLog(string line)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (LogLines.Count > 2000) LogLines.RemoveAt(0);
    }
}
