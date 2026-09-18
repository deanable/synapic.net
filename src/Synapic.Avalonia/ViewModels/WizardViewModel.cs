using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.ViewModels;

/// <summary>
/// Wizard orchestration (port of src/ui/app.py): linear navigation across
/// Step 1–4 + Dedup with per-step validation gates (session.validate_workflow_state).
/// </summary>
public partial class WizardViewModel : ViewModelBase
{
    private readonly Session _session;
    private readonly IInferenceSidecar _sidecar;

    public WizardViewModel(Session session, IInferenceSidecar sidecar)
    {
        _session = session;
        _sidecar = sidecar;

        Step1 = new Step1DatasourceViewModel(session);
        Step2 = new Step2EngineViewModel(session, sidecar);
        Step3 = new Step3ProcessViewModel(session, sidecar, Step1);
        Step4 = new Step4ResultsViewModel(session);
        Dedup = new StepDedupViewModel();

        CurrentStep = Step1;
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
        Step1DatasourceViewModel => 0,
        Step2EngineViewModel => 1,
        Step3ProcessViewModel => 2,
        Step4ResultsViewModel => 3,
        StepDedupViewModel => 4,
        _ => 0,
    };

    [RelayCommand]
    private void GoToStep2()
    {
        var (valid, error) = _session.ValidateForStep2();
        if (!valid) { ValidationError = error; return; }
        ValidationError = null;
        CurrentStep = Step2;
    }

    [RelayCommand]
    private void GoToStep3()
    {
        var (valid, error) = _session.ValidateForStep3(Step1.IsDaminionConnected);
        if (!valid) { ValidationError = error; return; }
        ValidationError = null;
        CurrentStep = Step3;
    }

    [RelayCommand]
    private void GoToStep4()
    {
        ValidationError = null;
        CurrentStep = Step4;
    }

    [RelayCommand]
    private void GoToDedup()
    {
        ValidationError = null;
        CurrentStep = Dedup;
    }

    [RelayCommand]
    private void Back()
    {
        ValidationError = null;
        CurrentStep = CurrentStepIndex switch
        {
            1 => Step1,
            2 => Step2,
            3 => Step3,
            4 => Step1,
            _ => Step1,
        };
    }

    [RelayCommand]
    private void StartOver()
    {
        ValidationError = null;
        CurrentStep = Step1;
    }
}
