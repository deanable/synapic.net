using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.ViewModels;

/// <summary>
/// Wizard orchestration (port of src/ui/app.py): linear navigation across
/// Step 1–4 + Dedup with per-step validation gates (session.validate_workflow_state),
/// step-entry side effects (Step 2 model list refresh, Step 4 grid refresh),
/// and Next/Back controls owned by the shell so every step shares one nav bar.
/// </summary>
public partial class WizardViewModel : ViewModelBase
{
    private readonly Session _session;
    private readonly IInferenceSidecar _sidecar;
    private readonly EngineSettingsStore? _engineStore;

    public WizardViewModel(Session session, IInferenceSidecar sidecar,
        DaminionConnectionStore? connectionStore = null,
        EngineSettingsStore? engineStore = null,
        SystemPromptPresetStore? presetStore = null)
    {
        _session = session;
        _sidecar = sidecar;
        _engineStore = engineStore;

        Step1 = new Step1DatasourceViewModel(session, connectionStore);
        Step2 = new Step2EngineViewModel(session, sidecar, engineStore, presetStore);
        Step3 = new Step3ProcessViewModel(session, sidecar, Step1);
        Step4 = new Step4ResultsViewModel(session, Step1, Step3);
        Dedup = new StepDedupViewModel(step1: Step1);
        Upscale = new StepUpscaleViewModel(step1: Step1, sidecar: sidecar);

        // Step 2's tag-field checkboxes gate navigation and the Step 3 Start
        // button; re-evaluate those commands whenever the selection changes.
        Step2.PropertyChanged += OnStep2PropertyChanged;
        // Processing start/stop drives the wizard-wide navigation lock; the
        // shell's buttons only redraw when NotifyCanExecuteChanged fires.
        Step3.PropertyChanged += OnStep3PropertyChanged;
        // The upscale run locks navigation exactly like a tagging batch.
        Upscale.PropertyChanged += OnUpscalePropertyChanged;

        CurrentStep = Step1;
    }

    private void OnStep2PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Step2EngineViewModel.TagKeywords)
            or nameof(Step2EngineViewModel.TagCategories)
            or nameof(Step2EngineViewModel.TagDescription))
        {
            NextCommand.NotifyCanExecuteChanged();
            GoToStep3Command.NotifyCanExecuteChanged();
            Step3.StartCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Persist Step 2 settings when the user leaves the engine step.</summary>
    private void PersistStep2IfLoaded()
    {
        try { Step2.SaveToStore(); }
        catch { /* never let persistence failures break navigation */ }
    }

    private void OnStep3PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Step3ProcessViewModel.IsRunning))
            NotifyProcessingChanged();
    }

    private void OnUpscalePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StepUpscaleViewModel.IsRunning))
            NotifyProcessingChanged();
    }

    public Step1DatasourceViewModel Step1 { get; }
    public Step2EngineViewModel Step2 { get; }
    public Step3ProcessViewModel Step3 { get; }
    public Step4ResultsViewModel Step4 { get; }
    public StepDedupViewModel Dedup { get; }
    public StepUpscaleViewModel Upscale { get; }

    // ── Route split (the app opens on a chooser: Tagging vs Deduplication vs
    // Upscaling; each route shows only the steps its workflow needs) ────────

    /// <summary>
    /// True while the deduplication route owns the wizard: the shell offers only
    /// Datasource → Deduplication, the tagging steps (Engine/Process/Results) are
    /// hidden, and Next on Step 1 lands on the dedup step instead of Step 2.
    /// False = the classic four-step tagging wizard.
    /// </summary>
    [ObservableProperty]
    private bool _isDedupRoute;

    /// <summary>
    /// True while the upscaling route owns the wizard: Datasource → Upscaling
    /// (the Daminion "Feature enhancement" utility), tagging steps hidden.
    /// </summary>
    [ObservableProperty]
    private bool _isUpscaleRoute;

    /// <summary>Enter the tagging route (start screen → Step 1 → … → Results).</summary>
    public void EnterTaggingRoute()
    {
        IsDedupRoute = false;
        IsUpscaleRoute = false;
        ValidationError = null;
        CurrentStep = Step1;
    }

    /// <summary>Enter the deduplication route (start screen → Step 1 → Deduplication).</summary>
    public void EnterDedupRoute()
    {
        IsDedupRoute = true;
        IsUpscaleRoute = false;
        ValidationError = null;
        CurrentStep = Step1;
    }

    /// <summary>Enter the upscaling route (start screen → Step 1 → Upscaling).</summary>
    public void EnterUpscaleRoute()
    {
        IsDedupRoute = false;
        IsUpscaleRoute = true;
        ValidationError = null;
        CurrentStep = Step1;
    }

    /// <summary>Engine/Process/Results tabs only exist in the tagging route.</summary>
    public bool ShowTaggingTabs => !IsDedupRoute && !IsUpscaleRoute;

    /// <summary>
    /// The Datasource tab reads as "step 1" of the tagging wizard (only once
    /// you have left it) and as the first stop of the dedup/upscale routes,
    /// where it is always on screen — that is what makes those routes look
    /// like Datasource → their step rather than one floating tab.
    /// </summary>
    public bool ShowDatasourceTab => IsDedupRoute || IsUpscaleRoute || CurrentStepIndex > 0;

    /// <summary>
    /// The dedup tab is reachable from Step 1 in the dedup route (that is the
    /// whole point of the split) and from Results in the tagging route. It
    /// hides while the upscale route owns the wizard.
    /// </summary>
    public bool ShowDedupTab => !IsUpscaleRoute;

    public bool CanGoToDedupTab => IsDedupRoute || CurrentStepIndex >= 3;

    /// <summary>The upscale tab hides while the dedup route owns the wizard (the
    /// mirror image of ShowDedupTab).</summary>
    public bool ShowUpscaleTab => !IsDedupRoute;

    /// <summary>Reachable from Step 1 in the upscale route (the whole point of the
    /// split) and from Results in the tagging route.</summary>
    public bool CanGoToUpscaleTab => IsUpscaleRoute || CurrentStepIndex >= 3;

    partial void OnIsDedupRouteChanged(bool value) => NotifyRouteGates();

    partial void OnIsUpscaleRouteChanged(bool value) => NotifyRouteGates();

    /// <summary>Re-evaluate everything whose state depends on which route owns the wizard.</summary>
    private void NotifyRouteGates()
    {
        OnPropertyChanged(nameof(ShowTaggingTabs));
        OnPropertyChanged(nameof(ShowDatasourceTab));
        OnPropertyChanged(nameof(ShowDedupTab));
        OnPropertyChanged(nameof(CanGoToDedupTab));
        OnPropertyChanged(nameof(ShowUpscaleTab));
        OnPropertyChanged(nameof(CanGoToUpscaleTab));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(CurrentStepTitle));
        GoToDedupCommand.NotifyCanExecuteChanged();
        GoToUpscaleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Push the Step 1 source choice onto the dedup step so the route carries
    /// what the user just picked: Daminion → catalog source, local → the folder
    /// (an existing folder typed straight into the dedup step is never clobbered).
    /// </summary>
    private void PrefillDedupSource()
    {
        var daminion = string.Equals(_session.Datasource.Type, "daminion", StringComparison.OrdinalIgnoreCase);
        Dedup.DatasourceType = daminion ? "daminion" : "local";
        if (!daminion && !string.IsNullOrWhiteSpace(_session.Datasource.LocalPath))
            Dedup.FolderPath = _session.Datasource.LocalPath;
    }

    [ObservableProperty]
    private ObservableObject _currentStep;

    [ObservableProperty]
    private string? _validationError;

    public int CurrentStepIndex => CurrentStep switch
    {
        Step2EngineViewModel => 1,
        Step3ProcessViewModel => 2,
        Step4ResultsViewModel => 3,
        StepDedupViewModel => 4,
        StepUpscaleViewModel => 5,
        _ => 0,
    };

    /// <summary>Tab enablement: you can only jump forward to steps you've reached.</summary>
    public bool CanGoToStep2Tab => !IsDedupRoute && CurrentStepIndex >= 1;
    public bool CanGoToStep3Tab => !IsDedupRoute && CurrentStepIndex >= 2;
    public bool CanGoToStep4Tab => !IsDedupRoute && CurrentStepIndex >= 3;
    public bool CanStartOver => CurrentStepIndex > 0;

    /// <summary>Short hint under the nav bar (processing lock etc.).</summary>
    public string NavHintText => !IsNavigationLocked ? ""
        : Upscale.IsRunning
            ? "Upscaling in progress — navigation locked. Press Stop first."
            : "Processing in progress — navigation locked. Abort from Step 3 first.";

    /// <summary>Label of the forward action for the current step (shell nav bar).</summary>
    public string NextButtonText => CurrentStepIndex switch
    {
        0 => IsDedupRoute ? "Next: Deduplication \u2192"
            : IsUpscaleRoute ? "Next: Upscaling \u2192"
            : "Next: Engine \u2192",
        1 => "Next: Process \u2192",
        2 => "Next: Results \u2192",
        _ => "Start Over",
    };

    // Which sidebar item reads as the current one (the sidebar is the nav now,
    // so the active entry has to follow the step the same way the old tab strip did.
    public bool IsSourceStepActive => CurrentStepIndex == 0;
    public bool IsEngineStepActive => CurrentStepIndex == 1;
    public bool IsProcessStepActive => CurrentStepIndex == 2;
    public bool IsResultsStepActive => CurrentStepIndex == 3;
    public bool IsDedupStepActive => CurrentStepIndex == 4;
    public bool IsUpscaleStepActive => CurrentStepIndex == 5;

    partial void OnCurrentStepChanged(ObservableObject value)
    {
        OnPropertyChanged(nameof(CurrentStepIndex));
        OnPropertyChanged(nameof(IsSourceStepActive));
        OnPropertyChanged(nameof(IsEngineStepActive));
        OnPropertyChanged(nameof(IsProcessStepActive));
        OnPropertyChanged(nameof(IsResultsStepActive));
        OnPropertyChanged(nameof(IsDedupStepActive));
        OnPropertyChanged(nameof(IsUpscaleStepActive));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(CanGoToStep2Tab));
        OnPropertyChanged(nameof(CanGoToStep3Tab));
        OnPropertyChanged(nameof(CanGoToStep4Tab));
        OnPropertyChanged(nameof(CanGoToDedupTab));
        OnPropertyChanged(nameof(CanGoToUpscaleTab));
        OnPropertyChanged(nameof(ShowDatasourceTab));
        OnPropertyChanged(nameof(CanStartOver));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        GoToStep3Command.NotifyCanExecuteChanged();
        Step3.StartCommand.NotifyCanExecuteChanged();
    }

    public string CurrentStepTitle => CurrentStepIndex switch
    {
        0 => "1 · Datasource",
        1 => "2 · Engine",
        2 => "3 · Process",
        3 => "4 · Results",
        4 => "Deduplication",
        5 => "Upscaling",
        _ => "",
    };

    /// <summary>Navigation is locked while a batch runs (step3_process.py behavior) —
    /// including an upscale run, which is the same kind of batch.</summary>
    public bool IsNavigationLocked => Step3.IsRunning || Upscale.IsRunning;

    /// <summary>Step-entry side effect; also invoked by the Back command.</summary>
    private async Task EnterStepAsync(ObservableObject step)
    {
        CurrentStep = step;
        ValidationError = null;
        switch (step)
        {
            case Step2EngineViewModel vm2:
                // Populate the model picker from the running server (no-op if empty/failed).
                try { await vm2.OnEnteredAsync(); } catch { /* server may be down; manual entry still works */ }
                break;
            case Step4ResultsViewModel vm4:
                vm4.Refresh();
                break;
        }

        // When the user reaches the Results step (after a run completes), snapshot
        // the current wizard + engine state to config.json so both the registry store
        // (Step 1/2 form) and the JSON config file carry the same working set.
        if (step is Step4ResultsViewModel)
            MainWindowPersistConfig();
    }

    /// <summary>Persist config via the main window's helper (no-op when running headless in tests).</summary>
    private void MainWindowPersistConfig()
    {
        try
        {
            var mw = App.Services.GetService(typeof(MainWindowViewModel)) as MainWindowViewModel;
            mw?.PersistConfig();
        }
        catch { /* never let config persistence break navigation */ }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (CurrentStepIndex)
        {
            case 0:
                var (valid2, error2) = _session.ValidateForStep2();
                if (!valid2) { ValidationError = error2; return; }

                if (IsDedupRoute)
                {
                    // Deduplication route: the datasource step exists purely to
                    // feed the dedup scan, so Next skips Engine/Process/Results.
                    PrefillDedupSource();
                    await EnterStepAsync(Dedup);
                    break;
                }

                if (IsUpscaleRoute)
                {
                    // Upscaling route: the datasource step feeds the upscale batch
                    // (the step reads its source straight off Step 1 — no prefill).
                    await EnterStepAsync(Upscale);
                    break;
                }

                PersistStep2IfLoaded();
                await EnterStepAsync(Step2);
                break;
            case 1:
                var (valid3, error3) = _session.ValidateForStep3(Step1.IsDaminionConnected);
                if (!valid3) { ValidationError = error3; return; }
                Step2.MakeSelectionValid();
                PersistStep2IfLoaded();
                await EnterStepAsync(Step3);
                break;
            case 2:
                await EnterStepAsync(Step4);
                break;
            default:
                StartOver();
                break;
        }
    }

    // On Step 2, Next also requires at least one tag field to be selected.
    private bool CanGoNext() =>
        !IsNavigationLocked &&
        (CurrentStepIndex != 1 || _session.Engine.HasSelectedTagField);

    /// <summary>
    /// Sidebar "Source" entry: go straight to the datasource step from anywhere.
    /// The old nav bar reused Back for this, which from Results landed on Process.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoToStep1()
    {
        if (CurrentStepIndex == 1) PersistStep2IfLoaded();
        await EnterStepAsync(Step1);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        // Persist Step 2 before leaving it in either direction.
        if (CurrentStepIndex == 1) PersistStep2IfLoaded();

        var target = CurrentStepIndex switch
        {
            1 => (ObservableObject)Step1,
            2 => Step2,
            3 => Step3,
            4 => Step1,
            _ => Step1,
        };
        await EnterStepAsync(target);
    }

    private bool CanGoBack() => !IsNavigationLocked;

    [RelayCommand]
    private void GoToStep2()
    {
        var (valid, error) = _session.ValidateForStep2();
        if (!valid) { ValidationError = error; return; }
        _ = EnterStepAsync(Step2);
    }

    [RelayCommand(CanExecute = nameof(CanGoToStep3))]
    private void GoToStep3()
    {
        var (valid, error) = _session.ValidateForStep3(Step1.IsDaminionConnected);
        if (!valid) { ValidationError = error; return; }
        Step2.MakeSelectionValid();
        PersistStep2IfLoaded();
        _ = EnterStepAsync(Step3);
    }

    private bool CanGoToStep3() => _session.Engine.HasSelectedTagField;

    [RelayCommand]
    private void GoToStep4() => _ = EnterStepAsync(Step4);

    [RelayCommand(CanExecute = nameof(CanOpenDedup))]
    private void GoToDedup()
    {
        if (IsDedupRoute)
        {
            // Same gate as Next on Step 1: the dedup step needs a source, and
            // the scope it uses is the one chosen right here.
            var (valid, error) = _session.ValidateForStep2();
            if (!valid) { ValidationError = error; return; }
            PrefillDedupSource();
        }
        _ = EnterStepAsync(Dedup);
    }

    private bool CanOpenDedup() => !IsNavigationLocked;

    [RelayCommand(CanExecute = nameof(CanOpenUpscale))]
    private void GoToUpscale()
    {
        if (IsUpscaleRoute)
        {
            // Same gate as Next on Step 1: the upscale step needs a source, and
            // the scope it uses is the one chosen right here.
            var (valid, error) = _session.ValidateForStep2();
            if (!valid) { ValidationError = error; return; }
        }
        _ = EnterStepAsync(Upscale);
    }

    private bool CanOpenUpscale() => !IsNavigationLocked;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void StartOver()
    {
        ValidationError = null;
        PersistStep2IfLoaded();
        MainWindowPersistConfig();
        CurrentStep = Step1;
        _session.ResetStats();
    }

    /// <summary>Re-evaluate nav locks when processing starts/stops.</summary>
    public void NotifyProcessingChanged()
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        GoToStep1Command.NotifyCanExecuteChanged();
        GoToDedupCommand.NotifyCanExecuteChanged();
        GoToUpscaleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNavigationLocked));
        OnPropertyChanged(nameof(NavHintText));
        GoToStep2Command.NotifyCanExecuteChanged();
        GoToStep3Command.NotifyCanExecuteChanged();
        GoToStep4Command.NotifyCanExecuteChanged();
    }
}
