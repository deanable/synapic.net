using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.ViewModels.Operations;

/// <summary>
/// The operation host (docs/ui-design.md §3/§6, decision D5): it owns the six
/// view models the shared operation template renders, and nothing else.
///
/// There is no step chain here any more — no current step, no Next/Back, no
/// mutual-exclusion route flags. Which mode is open is
/// <see cref="ShellViewModel.Current"/> and only that, so the app has one
/// navigation model instead of three. What this host does is hand a mode's
/// regions their content:
///
/// <code>
///   Region A  Source        shared by every mode        → Source
///   Region B  Parameters    the mode's own tunables     → TagParameters | Dedup | Upscale
///   Region C  Run · Report  progress, output, results   → TagRun + TagReport | Dedup | Upscale
/// </code>
///
/// It also keeps the cross-cutting wiring the step view models relied on the
/// wizard for: a tag-field change re-evaluates the tag run's Start gate (that
/// gate lives on the run itself), a running batch raises
/// <see cref="NavigationLockChanged"/> so the shell can lock leaving the mode
/// (§6.3), and leaving the tag mode commits the engine form to its store.
/// </summary>
public partial class OperationShellViewModel : ViewModelBase
{
    /// <summary>The tagging operation's key, as <see cref="IOperationViewModel.Key"/> spells it.</summary>
    public const string TagKey = "tag";
    public const string DedupKey = "dedup";
    public const string UpscaleKey = "upscale";

    public OperationShellViewModel(
        Session session,
        IInferenceSidecar sidecar,
        DaminionConnectionStore? connectionStore = null,
        EngineSettingsStore? engineStore = null,
        SystemPromptPresetStore? presetStore = null)
    {
        Source = new Step1DatasourceViewModel(session, connectionStore);
        TagParameters = new Step2EngineViewModel(session, sidecar, engineStore, presetStore);
        TagRun = new Step3ProcessViewModel(session, sidecar, Source);
        TagReport = new Step4ResultsViewModel(session, Source, TagRun);
        Dedup = new StepDedupViewModel(step1: Source);
        Upscale = new StepUpscaleViewModel(step1: Source, sidecar: sidecar);

        // The tag-field checkboxes gate the tagging run. The wizard used to own
        // half of that gate (Next); now the run owns all of it, so it has to
        // re-evaluate when the selection changes.
        TagParameters.PropertyChanged += OnTagParametersChanged;
        TagRun.PropertyChanged += OnRunStateChanged;
        Upscale.PropertyChanged += OnRunStateChanged;
    }

    /// <summary>Region A — the one shared source, for every mode (D4).</summary>
    public Step1DatasourceViewModel Source { get; }

    /// <summary>Tagging's Parameters region: model, device, tag fields, scoring, prompts.</summary>
    public Step2EngineViewModel TagParameters { get; }

    /// <summary>Tagging's run content: Start/Pause/Resume/Abort, progress, the run log.</summary>
    public Step3ProcessViewModel TagRun { get; }

    /// <summary>Tagging's report: the results grid, export, retry, verify.</summary>
    public Step4ResultsViewModel TagReport { get; }

    /// <summary>Deduplication's Parameters region and its run content (scan + apply).</summary>
    public StepDedupViewModel Dedup { get; }

    /// <summary>Upscaling's Parameters region and its run content.</summary>
    public StepUpscaleViewModel Upscale { get; }

    /// <summary>
    /// True while a batch is running (ui-design §6.3 run lock): the shell uses
    /// this to lock leaving the open mode, exactly as the wizard's navigation
    /// lock did before the collapse.
    /// </summary>
    public bool IsRunning => TagRun.IsRunning || Upscale.IsRunning;

    /// <summary>Raised when <see cref="IsRunning"/> changes, so the shell can re-read it.</summary>
    public event EventHandler? NavigationLockChanged;

    /// <summary>The mode's Parameters-region content (Region B), or null for an unknown key.</summary>
    public object? ParametersFor(string key) => key switch
    {
        TagKey => TagParameters,
        DedupKey => Dedup,
        UpscaleKey => Upscale,
        _ => null,
    };

    /// <summary>The mode's run content (Region C), or null for an unknown key.</summary>
    public object? RunFor(string key) => key switch
    {
        TagKey => TagRun,
        DedupKey => Dedup,
        UpscaleKey => Upscale,
        _ => null,
    };

    /// <summary>
    /// The mode's report content (Region C), or null when the mode has none:
    /// deduplication reviews its groups inside its own page, and upscaling
    /// reports through its progress, so only tagging has a separate grid.
    /// </summary>
    public object? ReportFor(string key) => key switch
    {
        TagKey => TagReport,
        _ => null,
    };

    /// <summary>
    /// Enter a mode: commit the mode being left, then run the new mode's entry
    /// side effects (the tagging form refreshes its model list from the running
    /// server, the results grid re-reads the last run) so opening a mode shows
    /// current state without a button press. The refresh is fire-and-forget: a
    /// server that is down must not block navigation, and manual entry still
    /// works (same contract the wizard's step entry had).
    /// </summary>
    public void EnterMode(string key, string? leavingKey = null)
    {
        Commit(leavingKey);

        switch (key)
        {
            case TagKey:
                _ = RefreshTagModeAsync();
                break;
        }
    }

    /// <summary>Back to the dashboard: commit the mode being left.</summary>
    public void LeaveMode(string? leavingKey) => Commit(leavingKey);

    private void Commit(string? leavingKey)
    {
        if (leavingKey is not TagKey) return;
        try
        {
            // Leaving the tagging form is what commits it to the engine store
            // (the wizard did this on step exit; the mode switch is the exit now).
            TagParameters.SaveToStore();
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(OperationShellViewModel), $"Failed to persist engine settings: {e.Message}");
        }
    }

    private async Task RefreshTagModeAsync()
    {
        try { await TagParameters.OnEnteredAsync(); }
        catch (Exception e)
        {
            SynapicLog.Debug(nameof(OperationShellViewModel), $"Model list refresh skipped: {e.Message}");
        }

        try { TagReport.Refresh(); }
        catch (Exception e)
        {
            SynapicLog.Debug(nameof(OperationShellViewModel), $"Results refresh skipped: {e.Message}");
        }
    }

    private void OnTagParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Step2EngineViewModel.TagKeywords)
            or nameof(Step2EngineViewModel.TagCategories)
            or nameof(Step2EngineViewModel.TagDescription))
        {
            TagRun.StartCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnRunStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Step3ProcessViewModel.IsRunning)
            or nameof(StepUpscaleViewModel.IsRunning))
        {
            NavigationLockChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
