using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Wizard;
using Xunit;
using Synapic.Shared.Contracts;
using Avalonia.Threading;

namespace Synapic.Main.Tests;

public class Step2EngineTests
{
    /// <summary>
    /// The exact defect behind the reported NullReferenceException: a two-way
    /// <c>SelectedItem</c> binding writes <c>null</c> back into the view model
    /// (Avalonia does this whenever the items view resets or the control lets
    /// go of its selection), and <c>SelectedDevice.Value</c> dereferenced it.
    /// Reflection stands in for the binding so the assertion does not depend on
    /// reproducing the exact Avalonia timing.
    /// </summary>
    [Fact]
    public void AViewPushingANullDeviceSelection_IsRejected()
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar());
        var selected = typeof(Step2EngineViewModel)
            .GetProperty(nameof(Step2EngineViewModel.SelectedDevice))!;

        selected.SetValue(vm, null!);   // exactly what the binding does

        Assert.NotNull(vm.SelectedDevice);
        Assert.Equal("cpu", vm.Device);              // get_Device() must not throw
        Assert.Equal("cpu", session.Engine.Device);
    }

    /// <summary>
    /// Regression: the Device combo binds <c>SelectedItem</c> two-way to
    /// <c>SelectedDevice</c>, so rebuilding <c>DeviceOptions</c> while the view
    /// is on screen pushes <c>null</c> back into the view model. From there
    /// <c>OnSelectedDeviceChanged</c> → <c>PushToSession</c> → <c>get_Device()</c>
    /// threw a NullReferenceException. The settings dialog made this reachable:
    /// it is a second view of the same view model, so a device refresh can now
    /// happen with a combo box attached.
    /// </summary>
    [AvaloniaFact]
    public void RefreshingDevices_WithTheViewBound_NeverNullsTheSelection()
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar());
        var window = new Window { Content = new Step2Engine { DataContext = vm } };
        window.Show();
        try
        {
            Assert.Equal("cpu", vm.Device);

            vm.RefreshDeviceOptions();

            Assert.NotNull(vm.SelectedDevice);
            Assert.Equal("cpu", vm.Device);          // get_Device() must not throw
            Assert.Equal("cpu", session.Engine.Device);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The engine form is the Parameters region's host of the same view model
    /// inside the shell: opening the tagging mode binds it, and a device refresh
    /// can happen with the combo box attached, so the view model must never hand a
    /// null to <c>get_Device()</c>. Replaces the retired settings-dialog pin (the
    /// dialog was the third host, gone with ui-design D3) and the step-1 picker
    /// (a duplicate of this form, gone with D5).
    /// </summary>
    [AvaloniaFact]
    public void EngineFormInTheParametersRegion_NeverNullsTheDeviceSelection()
    {
        var session = new Session();
        session.Datasource.LocalPath = System.IO.Path.GetTempPath();
        var shell = new MainWindowViewModel(new FakeSidecar(), session,
            () => null, null, null, _ => null,
            computeAvailabilityProbe: () => new ComputeAvailability(Cuda: false, Mps: false));
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var vm = shell.Operations.TagParameters;
            shell.StartTaggingRouteCommand.Execute(null);
            Assert.Equal("cpu", vm.Device);              // the form is bound

            vm.RefreshDeviceOptions();
            Assert.Equal("cpu", vm.Device);              // after a refresh
            Assert.NotNull(vm.SelectedDevice);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Same guard for the other moment: leaving the mode for the dashboard tears
    /// the combo box down while the view model is still the host's, and entering
    /// the mode again builds it back up.
    /// </summary>
    [AvaloniaFact]
    public void LeavingTheMode_WithTheViewLive_NeverNullsTheDeviceSelection()
    {
        var session = new Session();
        session.Datasource.LocalPath = System.IO.Path.GetTempPath();
        var shell = new MainWindowViewModel(new FakeSidecar(),
            session, () => "synapic-inference.exe", null, null, _ => "synapic-inference.exe",
            computeAvailabilityProbe: () => new ComputeAvailability(Cuda: false, Mps: false));
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.StartTaggingRouteCommand.Execute(null);
            Assert.Equal("cpu", shell.Operations.TagParameters.Device);

            shell.GoHomeCommand.Execute(null);            // tears the combo box down
            Assert.Equal("cpu", shell.Operations.TagParameters.Device);

            shell.StartTaggingRouteCommand.Execute(null); // and builds it again
            Assert.Equal("cpu", shell.Operations.TagParameters.Device);
        }
        finally
        {
            window.Close();
        }

        Assert.Equal("cpu", shell.Operations.TagParameters.Device);   // after the window closes
    }

    [AvaloniaFact]
    public void Candidate_labels_are_visible_and_drive_the_next_tag_request()
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar());
        var view = new Step2Engine { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            var candidates = view.GetVisualDescendants().OfType<TextBox>()
                .Single(textBox => textBox.Watermark?.Contains("Candidate labels", StringComparison.Ordinal) == true);
            candidates.Text = "cat, dog, bird";

            Assert.Equal(new[] { "cat", "dog", "bird" }, session.Engine.ProbabilityCandidates);
            var request = new Step3ProcessViewModel(session, new FakeSidecar(), new Step1DatasourceViewModel(session))
                .BuildTagRequest();
            Assert.Equal(new[] { "cat", "dog", "bird" }, request.Options!.CandidateLabels);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Rendered_cancel_models_cancels_list_refresh_and_restores_actions()
    {
        var enteredList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sidecar = new ModelOperationSidecar
        {
            OnListModels = async ct =>
            {
                enteredList.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return Array.Empty<Synapic.Shared.Contracts.ModelInfo>();
                }
                catch (OperationCanceledException)
                {
                    listCancelled.TrySetResult();
                    throw;
                }
            },
        };
        var vm = new Step2EngineViewModel(new Session(), sidecar);
        var view = new Step2Engine { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();

        try
        {
            var refresh = vm.RefreshModelsCommand.ExecuteAsync(null);
            await enteredList.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsLoadingModels);
            var cancel = view.GetVisualDescendants().OfType<Button>()
                .Single(button => (string?)button.Content == "Cancel model action");
            Assert.True(cancel.IsVisible);
            Assert.True(cancel.IsEnabled);
            Assert.False(vm.RefreshModelsCommand.CanExecute(null));
            Assert.False(vm.DownloadSelectedCommand.CanExecute(null));

            cancel.Command!.Execute(null);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.True(listCancelled.Task.IsCompleted);
            Assert.False(vm.IsLoadingModels);
            Assert.False(cancel.IsVisible);
            Assert.True(vm.RefreshModelsCommand.CanExecute(null));
            Assert.True(vm.DownloadSelectedCommand.CanExecute(null));
            Assert.Equal("Model list refresh cancelled", vm.ModelsMessage);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void PinsMultimodalTask_AndCorrectsStaleSessionValue()
    {
        var session = new Session();
        session.Engine.Task = "image-classification"; // value an older build could persist

        var vm = new Step2EngineViewModel(session, new FakeSidecar());

        Assert.Equal(Step2EngineViewModel.MultimodalTask, session.Engine.Task);
        Assert.Equal("image-text-to-text", session.Engine.Task);

        // Changing the model must not reintroduce a per-field task.
        vm.ManualModelId = "LiquidAI/LFM2.5-VL-1.6B";
        Assert.Equal("image-text-to-text", session.Engine.Task);
    }

    [Fact]
    public void TagFieldCheckboxes_PushToSessionAndSelection()
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar());

        // All three default on (original behavior).
        Assert.True(session.Engine.TagKeywords);
        Assert.True(session.Engine.TagCategories);
        Assert.True(session.Engine.TagDescription);

        vm.TagKeywords = false;

        Assert.False(session.Engine.TagKeywords);
        var fields = session.Engine.ToTagFieldSelection();
        Assert.False(fields.Keywords);
        Assert.True(fields.Category);
        Assert.True(fields.Description);
    }

    /// <summary>
    /// The model's sensitivity settings are sliders, not number pickers: a drag
    /// has to write through to the view model (and to the session the run
    /// reads), and the readout beside the slider has to show the value that was
    /// dragged to. A OneWay binding would leave the batch tagging at the old
    /// threshold while the slider shows the new one.
    /// </summary>
    [AvaloniaFact]
    public void Sensitivity_sliders_write_through_to_the_view_model()
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar());
        var view = new Step2Engine { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            var confidence = view.FindControl<Slider>("ConfidenceThresholdSlider");
            var probability = view.FindControl<Slider>("ProbabilityThresholdSlider");
            Assert.NotNull(confidence);   // still sliders, not number pickers
            Assert.NotNull(probability);

            Assert.Equal(0.3, confidence!.Value, 3);    // the session defaults
            Assert.Equal(0.5, probability!.Value, 3);

            confidence.Value = 0.75;                    // exactly what a drag produces
            probability.Value = 0.25;

            Assert.Equal(0.75, vm.ConfidenceThreshold, 3);
            Assert.Equal(0.75, session.Engine.ConfidenceThreshold, 3);
            Assert.Equal(0.25, vm.ProbabilityThreshold, 3);
            Assert.Equal(0.25, session.Engine.ProbabilityThreshold, 3);

            var request = new Step3ProcessViewModel(session, new FakeSidecar(), new Step1DatasourceViewModel(session))
                .BuildTagRequest();
            Assert.Equal(0.75, request.Options!.ConfidenceThreshold, 3);
            Assert.Equal(0.25, request.Options.ProbabilityThreshold, 3);

            // The readout beside each slider reports the value it was set to.
            Assert.Equal(0.75.ToString("0.00"),
                view.FindControl<TextBlock>("ConfidenceThresholdValue")!.Text);
            Assert.Equal(0.25.ToString("0.00"),
                view.FindControl<TextBlock>("ProbabilityThresholdValue")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The settings summary is the only place a run's model is named before it
    /// starts, so with no model picked it has to say so rather than render an
    /// empty box that reads like a broken form.
    /// </summary>
    [Fact]
    public void Model_summary_says_nothing_is_chosen_until_a_model_is_picked()
    {
        // Nothing picked from the model list and nothing typed into the id box:
        // the state a cleared form is in.
        var session = new Session();
        session.Engine.ModelId = "";
        var vm = new Step2EngineViewModel(session, new FakeSidecar());

        Assert.Null(vm.SelectedModel);
        Assert.Equal("", vm.ManualModelId);
        Assert.Equal("Not chosen yet — pick a model on step 1 or here.", vm.ModelSummary);

        // A hand-typed id is reported exactly as typed: it is the id the sidecar
        // will be asked for, so the summary cannot paraphrase it.
        vm.ManualModelId = "org/hand-picked-vlm";
        Assert.Equal("org/hand-picked-vlm", vm.ModelSummary);
    }

    /// <summary>
    /// The scoring readout is where a run's thresholds are visible before it
    /// starts, so each mode has to name itself and its candidate list: a
    /// "probability" run that read like the model's own answer would be the
    /// wrong promise to make on the settings surface.
    /// </summary>
    [Fact]
    public void Scoring_summary_names_each_probability_mode_and_its_candidates()
    {
        var half = 0.5.ToString("0.00");
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar());

        vm.ProbabilityModeIndex = 0;   // llm
        Assert.Equal("LLM only (the model's own answer)", vm.ProbabilitySummary);

        vm.ProbabilityModeIndex = 1;   // probability only
        Assert.Equal($"Probability only (candidates at {half})", vm.ProbabilitySummary);

        vm.ProbabilityCandidates = "tree, forest";
        Assert.Equal($"Probability only (candidates at {half}: tree, forest)", vm.ProbabilitySummary);

        vm.ProbabilityModeIndex = 2;   // both
        Assert.Equal($"LLM + probability (candidates at {half}: tree, forest)", vm.ProbabilitySummary);
    }

    private sealed class ModelOperationSidecar : FakeSidecar
    {
        public Func<CancellationToken, Task<ModelInfo[]>>? OnListModels { get; init; }

        public override Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) =>
            OnListModels is { } handler ? handler(ct) : base.ListModelsAsync(ct);
    }
}
