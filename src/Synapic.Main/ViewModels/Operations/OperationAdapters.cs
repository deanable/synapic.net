using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;
using Synapic.Shared.Contracts;

namespace Synapic.Main.ViewModels.Operations;

/// <summary>
/// The adapters' change plumbing: a bound "not ready" reason or a parameters
/// read-back must not go stale while the wrapped mode updates underneath.
/// </summary>
internal static class OperationChange
{
    /// <summary>Every member of <see cref="IOperationViewModel"/>, re-raised together.</summary>
    private static readonly string[] Members =
    {
        nameof(IOperationViewModel.Key),
        nameof(IOperationViewModel.Title),
        nameof(IOperationViewModel.Description),
        nameof(IOperationViewModel.HelpTopic),
        nameof(IOperationViewModel.ParametersSummary),
        nameof(IOperationViewModel.Run),
        nameof(IOperationViewModel.IsRunEnabled),
        nameof(IOperationViewModel.RunDisabledReason),
        nameof(IOperationViewModel.Report),
    };

    /// <summary>
    /// Raise every member rather than mapping which wrapped property feeds which
    /// member: an extra notification is cheap, a stale bound value is not.
    /// </summary>
    internal static void RaiseAll(INotifyPropertyChanged sender, PropertyChangedEventHandler? handler)
    {
        foreach (var member in Members)
            handler?.Invoke(sender, new PropertyChangedEventArgs(member));
    }
}

/// <summary>
/// Thin adapter: exposes the tagging route through <see cref="IOperationViewModel"/>
/// (docs/ui-refactor-plan.md §4 phase 1). Every member reads or invokes the wrapped
/// step view model — no state is copied and no logic is re-implemented, so the
/// wizard keeps behaving exactly as it did.
/// </summary>
public sealed class TagOperationViewModel : IOperationViewModel
{
    private readonly Step3ProcessViewModel _step;
    private readonly Step4ResultsViewModel? _results;

    /// <param name="step">The tagging route's process step; owns the run.</param>
    /// <param name="results">The results step that shows what a batch wrote; null
    /// when the shell hosting the adapter has no results step yet.</param>
    public TagOperationViewModel(Step3ProcessViewModel step, Step4ResultsViewModel? results = null)
    {
        _step = step;
        _results = results;
        _step.PropertyChanged += (_, _) => OperationChange.RaiseAll(this, PropertyChanged);
        _step.StartCommand.CanExecuteChanged += (_, _) => OperationChange.RaiseAll(this, PropertyChanged);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key => "tag";

    public string Title => "Tagging";

    /// <summary>The chooser card's copy, reused so the dashboard says what the start screen said.</summary>
    public string Description =>
        "Run the AI model over a datasource: check the source and the model, tune the tagging settings, " +
        "process the batch, then review what was written.";

    public string HelpTopic => HelpTopics.ForStepIndex(2);

    /// <summary>The run's parameters, read back from the same request the batch sends.</summary>
    public string ParametersSummary
    {
        get
        {
            var request = _step.BuildTagRequest();
            var options = request.Options ?? new TagOptions();
            var prompt = string.IsNullOrWhiteSpace(options.SystemPrompt) ? "default prompt" : "custom prompt";
            return $"model {request.ModelId} · confidence {options.ConfidenceThreshold:0.00} · " +
                   $"probability {options.ProbabilityMode} · {prompt}";
        }
    }

    /// <summary>The existing batch start command — the same one the wizard's Process button invokes.</summary>
    public IAsyncRelayCommand Run => _step.StartCommand;

    public bool IsRunEnabled => _step.StartCommand.CanExecute(null);

    /// <summary>The step's own start gate, in words — one source with the button.</summary>
    public string? RunDisabledReason
    {
        get
        {
            if (IsRunEnabled) return null;
            return _step.IsRunning ? "A batch is already running" : _step.StartBlockedReason;
        }
    }

    /// <summary>The results grid, or null before the first batch.</summary>
    public object? Report => _results is { Results.Count: > 0 } ? _results : null;
}

/// <summary>
/// Thin adapter: exposes an image-matching search — the deduplication route or
/// the similarity route — through <see cref="IOperationViewModel"/> (CONTEXT
/// D-05 — the run is the scan; Apply stays on the step itself).
///
/// Both routes are the same scan under the two <see cref="ImageMatchProfile"/>s,
/// so the identity members read themselves off the wrapped step
/// (<see cref="StepDedupViewModel.OperationKey"/> and friends) rather than being
/// copied in here: two adapters over two instances then differ in exactly the
/// way the two operations do.
/// </summary>
public sealed class DedupOperationViewModel : IOperationViewModel
{
    private readonly StepDedupViewModel _scan;

    public DedupOperationViewModel(StepDedupViewModel scan)
    {
        _scan = scan;
        _scan.PropertyChanged += (_, _) => OperationChange.RaiseAll(this, PropertyChanged);
        _scan.ScanCommand.CanExecuteChanged += (_, _) => OperationChange.RaiseAll(this, PropertyChanged);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>"dedup" or "similar", as the wrapped step's own profile spells it.</summary>
    public string Key => _scan.OperationKey;

    /// <summary>"Deduplication" or "Similar images".</summary>
    public string Title => _scan.OperationTitle;

    /// <summary>The chooser card's copy, reused so the dashboard says what the start screen said.</summary>
    public string Description => _scan.IsSimilar
        ? "Group visually similar images in a local folder or in the Daminion catalog scope, review the " +
          "groups, then keep one of each and act on the rest."
        : "Find duplicate images in a local folder or in the Daminion catalog scope, review the groups, " +
          "then tag, move or delete the extras.";

    /// <summary>The operation's own topic: the similarity search has one of its own.</summary>
    public string HelpTopic => _scan.IsSimilar ? HelpTopics.Similar : HelpTopics.ForStepIndex(4);

    /// <summary>The scan rules read-back the review page already shows.</summary>
    public string ParametersSummary => _scan.ScanSettingsSummary;

    /// <summary>The existing scan command — Apply/delete stay on the dedup step.</summary>
    public IAsyncRelayCommand Run => _scan.ScanCommand;

    public bool IsRunEnabled => _scan.ScanCommand.CanExecute(null);

    /// <summary>The step's own scan gate, in words — one source with the button.</summary>
    public string? RunDisabledReason
    {
        get
        {
            if (IsRunEnabled) return null;
            if (_scan.IsScanning) return "A scan is already running";
            if (_scan.IsApplying) return "An apply is in progress";
            return _scan.StartBlockedReason;
        }
    }

    /// <summary>The reviewed groups, or null before the first scan.</summary>
    public object? Report => _scan.Groups.Count > 0 ? _scan.Groups : null;
}

/// <summary>
/// Thin adapter: exposes the upscaling route through <see cref="IOperationViewModel"/>.
/// </summary>
public sealed class UpscaleOperationViewModel : IOperationViewModel
{
    private readonly StepUpscaleViewModel _upscale;

    public UpscaleOperationViewModel(StepUpscaleViewModel upscale)
    {
        _upscale = upscale;
        _upscale.PropertyChanged += (_, _) => OperationChange.RaiseAll(this, PropertyChanged);
        _upscale.StartCommand.CanExecuteChanged += (_, _) => OperationChange.RaiseAll(this, PropertyChanged);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key => "upscale";

    public string Title => "Upscaling";

    /// <summary>The chooser card's copy, reused so the dashboard says what the start screen said.</summary>
    public string Description =>
        "Enhance image resolution — the Daminion Feature enhancement utility: AI (Swin2SR) or fast " +
        "Lanczos upscaling over the chosen source.";

    public string HelpTopic => HelpTopics.Upscale;

    /// <summary>The parameters read-back the run page already shows.</summary>
    public string ParametersSummary => _upscale.SettingsSummary;

    /// <summary>The existing upscale start command.</summary>
    public IAsyncRelayCommand Run => _upscale.StartCommand;

    public bool IsRunEnabled => _upscale.StartCommand.CanExecute(null);

    /// <summary>The step's own start gate, in words — one source with the button.</summary>
    public string? RunDisabledReason
    {
        get
        {
            if (IsRunEnabled) return null;
            return _upscale.IsRunning ? "An upscale run is already running" : _upscale.StartBlockedReason;
        }
    }

    /// <summary>The run's output (its log) so far, or null before the first run.</summary>
    public object? Report => _upscale.LogLines.Count > 0 ? _upscale.LogLines : null;
}
