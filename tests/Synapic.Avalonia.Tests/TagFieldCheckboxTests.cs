using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;
using Synapic.Avalonia.Views.Wizard;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The three Step 2 tag-field checkboxes decide which of the returned fields the
/// batch actually writes (ProcessingOrchestrator nulls out the rest). A click
/// that never reaches the view model therefore turns a "tag all three" run into
/// a keywords-only one, and nothing on screen or in the file log says so - so
/// these tests drive the real compiled XAML rather than the view model. A broken
/// Avalonia binding is silent: the control simply stops updating.
/// </summary>
public class TagFieldCheckboxTests
{
    private static (Step2Engine View, Step2EngineViewModel Vm, Session Session, Window Window) Build(
        EngineSettingsStore? store = null)
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar(), store);
        var view = new Step2Engine { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, vm, session, window);
    }

    private static void Click(Window window, Control control)
    {
        var centre = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var point = control.TranslatePoint(centre, window) ?? centre;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The XAML omits <c>Mode=</c>, which is only correct because Avalonia's
    /// IsChecked binds two-way by default. If that ever changes, clicking a
    /// checkbox stops updating the session and this test says why.
    /// </summary>
    [AvaloniaFact]
    public void IsChecked_DefaultsToTwoWay()
    {
        Assert.Equal(
            BindingMode.TwoWay,
            ToggleButton.IsCheckedProperty.GetMetadata(typeof(CheckBox)).DefaultBindingMode);
    }

    [AvaloniaFact]
    public void ClickingATagFieldCheckbox_ReachesTheViewModelAndTheSession()
    {
        var (view, vm, session, window) = Build();
        try
        {
            var categories = view.TagCategoriesBox;
            Assert.True(categories.IsChecked);

            Click(window, categories);

            // The click has to land on the control (guards the harness itself).
            Assert.False(categories.IsChecked);
            Assert.False(vm.TagCategories);
            Assert.False(session.Engine.TagCategories);
            Assert.False(session.Engine.ToTagFieldSelection().Category);

            // ...and back on, which is what a user does after noticing the drop.
            Click(window, categories);

            Assert.True(categories.IsChecked);
            Assert.True(session.Engine.ToTagFieldSelection().Category);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void UncheckedFields_AreNotWrittenEvenWhenTheModelReturnsThem()
    {
        var (view, vm, session, window) = Build();
        try
        {
            // What a stored keywords-only selection looks like after startup.
            vm.TagCategories = false;
            vm.TagDescription = false;

            var selection = session.Engine.ToTagFieldSelection();
            Assert.True(selection.Keywords);
            Assert.False(selection.Category);
            Assert.False(selection.Description);

            // The checkboxes must agree with what will actually be written.
            Assert.False(view.TagCategoriesBox.IsChecked);
            Assert.False(view.TagDescriptionBox.IsChecked);
            Assert.True(view.TagKeywordsBox.IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Dropping a field has to be visible while it can still be changed, because
    /// the model returns all three either way - the missing fields only show up
    /// later, in Daminion.
    /// </summary>
    [AvaloniaFact]
    public void APartialSelection_WarnsWithTheFieldsThatWillBeWritten()
    {
        var (view, _, session, window) = Build();
        try
        {
            var warning = view.TagFieldWarningText;

            // All three selected: nothing to warn about.
            Assert.False(warning.IsVisible);
            Assert.False(session.Engine.HasPartialTagFieldSelection);

            Click(window, view.TagDescriptionBox);

            Assert.True(warning.IsVisible);
            Assert.Contains("keywords and categories", warning.Text);
            Assert.Contains("all three", warning.Text);

            Click(window, view.TagCategoriesBox);

            Assert.Contains("keywords only", warning.Text);
            Assert.Equal("keywords only", session.Engine.ToTagFieldSelection().Summary);

            // Back to all three: the warning goes away.
            Click(window, view.TagDescriptionBox);
            Click(window, view.TagCategoriesBox);

            Assert.False(warning.IsVisible);
            Assert.True(session.Engine.ToTagFieldSelection().IsAll);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A stored keywords-only selection has to reach the checkboxes, so the form
    /// always shows the permutation that a run will write.
    /// </summary>
    [AvaloniaFact]
    public void StoredSelection_HydratesIntoTheCheckboxesAndTheSession()
    {
        if (!OperatingSystem.IsWindows()) return;

        var keyPath = $@"Software\Synapic\Tests_TagFields_{Guid.NewGuid():N}";
        var store = new EngineSettingsStore(keyPath);
        try
        {
            store.Save(new EngineSettingsParams(
                ModelId: "LiquidAI/LFM2.5-VL-450M",
                Task: "image-text-to-text",
                Device: "cpu",
                ConfidenceThreshold: 0.3,
                ProbabilityMode: "both",
                ProbabilityThreshold: 0.5,
                ProbabilityCandidates: Array.Empty<string>(),
                SystemPrompt: "",
                UserPrompt: "",
                EmbeddingRescueEnabled: false,
                TagKeywords: true,
                TagCategories: false,
                TagDescription: false));

            var (view, _, session, window) = Build(store);
            try
            {
                Assert.True(view.TagKeywordsBox.IsChecked);
                Assert.False(view.TagCategoriesBox.IsChecked);
                Assert.False(view.TagDescriptionBox.IsChecked);

                var selection = session.Engine.ToTagFieldSelection();
                Assert.True(selection.Keywords);
                Assert.False(selection.Category);
                Assert.False(selection.Description);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            store.Clear();
        }
    }
}
