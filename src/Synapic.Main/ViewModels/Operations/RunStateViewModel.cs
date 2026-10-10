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
        NotifyRunVisibilityChanged();
        NotifyRunCommandsCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        NotifyRunVisibilityChanged();
        NotifyRunCommandsCanExecuteChanged();
    }

    // ── The mode's own run actions, for the shared run bar ────────────────

    /// <summary>
    /// The mode's own words for the run's primary action — the bar's one accent
    /// button (“Start tagging”, “Scan”, “Run upscale”). The bar itself
    /// stays mode-agnostic: it asks the operation it binds what to say.
    /// </summary>
    public virtual string RunActionLabel => "Start";

    /// <summary>
    /// The mode's Stop-family action while a run is in flight — tagging's Abort,
    /// dedup's scan Stop, upscaling's Stop — or null for a mode with none. The
    /// bar renders it from here rather than from the mode's own page, so a run
    /// has exactly one Stop.
    /// </summary>
    public virtual IRelayCommand? StopAction => null;

    /// <summary>What this mode calls its Stop action (tagging aborts).</summary>
    public virtual string StopActionLabel => "Stop";

    /// <summary>Pause semantics, for the modes that have them (tagging); null otherwise.</summary>
    public virtual IRelayCommand? PauseAction => null;

    /// <summary>See <see cref="PauseAction"/>.</summary>
    public virtual IRelayCommand? ResumeAction => null;

    /// <summary>Show Pause only while it can actually pause: running and not paused.</summary>
    public bool ShowsPause => PauseAction is not null && IsRunning && !IsPaused;

    /// <summary>Show Resume only while the run is paused.</summary>
    public bool ShowsResume => ResumeAction is not null && IsPaused;

    /// <summary>Show Stop only while a run is in flight.</summary>
    public bool ShowsStop => StopAction is not null && IsRunning;

    /// <summary>
    /// The mode's action row follows the run state, so the bar re-reads it on
    /// every transition — a hidden Pause that stayed enabled would be a button
    /// that does nothing, and a Stop left behind after a run ends is worse.
    /// </summary>
    protected void NotifyRunVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowsPause));
        OnPropertyChanged(nameof(ShowsResume));
        OnPropertyChanged(nameof(ShowsStop));
    }

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
