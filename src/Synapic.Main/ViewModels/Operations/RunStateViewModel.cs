using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Services.Processing;

namespace Synapic.Main.ViewModels.Operations;

/// <summary>
/// Shared run machinery for every operation route (tagging, dedup scan,
/// upscaling): the progress/ETA surface, cancellation and pause lifecycle, and
/// the UI-marshalled capped log. Extracted from the near-verbatim copies in the
/// step view models (docs/ui-refactor-plan.md §4 Phase 1) so one RunStateBar can
/// bind one state shape.
/// </summary>
public abstract partial class RunStateViewModel : ViewModelBase
{
    /// <summary>The active run's cancellation source; null while idle.</summary>
    protected CancellationTokenSource? Cts;

    /// <summary>Pause gate for operations with pause semantics (tagging); null otherwise.</summary>
    protected PauseTokenSource? PauseSource;

    /// <summary>The run log every run view binds; appended through <see cref="RunLog"/>.</summary>
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

    [ObservableProperty]
    private bool _isPaused;

    /// <summary>True when no run is active — the Start button's enable state.</summary>
    public bool IsIdle => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        NotifyRunCommandsCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value) => NotifyRunCommandsCanExecuteChanged();

    /// <summary>
    /// Per-operation hook: notify the run commands this operation exposes
    /// (tagging: Start/Abort/Pause/Resume; upscaling: Start/Stop) that their
    /// CanExecute may have changed.
    /// </summary>
    protected abstract void NotifyRunCommandsCanExecuteChanged();

    /// <summary>Each operation's own ready-to-run predicate.</summary>
    protected abstract bool CanStart();

    /// <summary>Each operation's run body, executed by the shared StartCommand.</summary>
    protected abstract Task StartCoreAsync(CancellationToken ct);

    /// <summary>The shared start command, gated by the operation's own predicate.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync(CancellationToken ct) => StartCoreAsync(ct);

    /// <summary>Enter a run: flip state, clear pause, create the cancellation source.</summary>
    protected void BeginRun()
    {
        IsRunning = true;
        IsPaused = false;
        Cts = new CancellationTokenSource();
    }

    /// <summary>Leave a run (success, cancel or failure): tear down CTS and pause gate.</summary>
    protected void EndRun()
    {
        PauseSource = null;
        IsPaused = false;
        IsRunning = false;
        Cts?.Dispose();
        Cts = null;
    }

    /// <summary>Request cancellation of the active run (Abort/Stop commands).</summary>
    protected void CancelRun() => Cts?.Cancel();

    /// <summary>Pause the active run at the next item boundary (no-op without a gate).</summary>
    protected void PauseRun()
    {
        PauseSource?.Pause();
        IsPaused = true;
    }

    /// <summary>Resume a paused run (no-op without a gate).</summary>
    protected void ResumeRun()
    {
        PauseSource?.Resume();
        IsPaused = false;
    }

    /// <summary>Append a timestamped line through the shared capped log helper.</summary>
    protected void AppendLog(string line) => RunLog.Append(LogLines, line);

    /// <summary>
    /// Surface one <see cref="ProcessProgress"/> report on the observables —
    /// the strings and ETA format shared by every batch-style operation
    /// (port of step3_process.py's progress callback text).
    /// </summary>
    protected void ApplyProgress(ProcessProgress p)
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
            EtaText = $"ETA {ProcessProgress.FormatDuration(eta)} remaining — {ProcessProgress.FormatDuration(p.PerItem)} per image";
        else if (p.Total > 0 && p.Processed >= p.Total)
            EtaText = "";
    }
}
