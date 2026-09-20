using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.ViewModels;

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

    public WizardViewModel(Session session, IInferenceSidecar sidecar, DaminionConnectionStore? connectionStore = null)
    {
        _session = session;
        _sidecar = sidecar;

        Step1 = new Step1DatasourceViewModel(session, connectionStore);
        Step2 = new Step2EngineViewModel(session, sidecar);
        Step3 = new Step3ProcessViewModel(session, sidecar, Step1);
        Step4 = new Step4ResultsViewModel(session, Step1, Step3);
        Dedup = new StepDedupViewModel();

        // Step 2's tag-field checkboxes gate navigation and the Step 3 Start
        // button; re-evaluate those commands whenever the selection changes.
        Step2.PropertyChanged += OnStep2PropertyChanged;

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

    public Step1DatasourceViewModel Step1 { get; }
    public Step2EngineViewModel Step2 { get; }
    public Step3ProcessViewModel Step3 { get; }
    public Step4ResultsViewModel Step4 { get; }
    public StepDedupViewModel Dedup { get; }

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
        _ => 0,
    };

    /// <summary>Tab enablement: you can only jump forward to steps you've reached.</summary>
    public bool CanGoToStep2Tab => CurrentStepIndex >= 1;
    public bool CanGoToStep3Tab => CurrentStepIndex >= 2;
    public bool CanGoToStep4Tab => CurrentStepIndex >= 3;
    public bool CanGoToDedupTab => CurrentStepIndex >= 3;
    public bool CanStartOver => CurrentStepIndex > 0;

    /// <summary>Short hint under the nav bar (processing lock etc.).</summary>
    public string NavHintText =>
        IsNavigationLocked ? "Processing in progress — navigation locked. Abort from Step 3 first." : "";

    /// <summary>Label of the forward action for the current step (shell nav bar).</summary>
    public string NextButtonText => CurrentStepIndex switch
    {
        0 => "Next: Engine \u2192",
        1 => "Next: Process \u2192",
        2 => "Next: Results \u2192",
        _ => "Start Over",
    };

    partial void OnCurrentStepChanged(ObservableObject value)
    {
        OnPropertyChanged(nameof(CurrentStepIndex));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(CanGoToStep2Tab));
        OnPropertyChanged(nameof(CanGoToStep3Tab));
        OnPropertyChanged(nameof(CanGoToStep4Tab));
        OnPropertyChanged(nameof(CanGoToDedupTab));
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
        _ => "",
    };

    /// <summary>Navigation is locked while a batch runs (step3_process.py behavior).</summary>
    public bool IsNavigationLocked => Step3.IsRunning;

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
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (CurrentStepIndex)
        {
            case 0:
                var (valid2, error2) = _session.ValidateForStep2();
                if (!valid2) { ValidationError = error2; return; }
                await EnterStepAsync(Step2);
                break;
            case 1:
                var (valid3, error3) = _session.ValidateForStep3(Step1.IsDaminionConnected);
                if (!valid3) { ValidationError = error3; return; }
                Step2.MakeSelectionValid();
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

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
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
        _ = EnterStepAsync(Step3);
    }

    private bool CanGoToStep3() => _session.Engine.HasSelectedTagField;

    [RelayCommand]
    private void GoToStep4() => _ = EnterStepAsync(Step4);

    [RelayCommand(CanExecute = nameof(CanOpenDedup))]
    private void GoToDedup() => _ = EnterStepAsync(Dedup);

    private bool CanOpenDedup() => !IsNavigationLocked;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void StartOver()
    {
        ValidationError = null;
        CurrentStep = Step1;
    }

    /// <summary>Re-evaluate nav locks when processing starts/stops.</summary>
    public void NotifyProcessingChanged()
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        GoToDedupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNavigationLocked));
        OnPropertyChanged(nameof(NavHintText));
        GoToStep2Command.NotifyCanExecuteChanged();
        GoToStep3Command.NotifyCanExecuteChanged();
        GoToStep4Command.NotifyCanExecuteChanged();
    }
}
