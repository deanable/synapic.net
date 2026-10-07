using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views;
using Synapic.Main.Views.Settings;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The redesigned shell (proposal §2): a persistent source header, a sidebar
/// that is the navigation, an action bar under the content, a server footer and
/// the log strip. These drive the real compiled MainWindow — a mistyped binding
/// path fails silently in Avalonia, so the wired state is what is asserted.
/// </summary>
public class MainLayoutShellTests
{
    private static MainWindowViewModel NewShell(string? exe = null, Session? session = null) =>
        new(new FakeSidecar(), new FakeBuildService(), session ?? new Session(), () => exe, null, null, _ => exe);

    [AvaloniaFact]
    public void Shell_has_a_header_a_sidebar_an_action_bar_a_footer_and_the_log()
    {
        var window = new MainWindow { DataContext = NewShell() };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            // Header: the source profile is on screen without entering a flow,
            // and the settings entry point is there exactly once.
            Assert.NotEmpty(window.GetVisualDescendants().OfType<SourceStatusStrip>());
            Assert.Single(buttons, b => b.Command == vm.OpenSettingsCommand);

            // Sidebar: one Dashboard entry, and every mode header reachable both
            // from the start-screen cards and from the sidebar.
            Assert.Single(buttons, b => b.Command == vm.GoHomeCommand);
            Assert.Equal(2, buttons.Count(b => b.Command == vm.StartTaggingRouteCommand));
            Assert.Equal(2, buttons.Count(b => b.Command == vm.StartDedupRouteCommand));
            Assert.Equal(2, buttons.Count(b => b.Command == vm.StartUpscaleRouteCommand));

            // Footer: exactly one Help button and it is wired (a broken binding
            // leaves Command null and looks fine until someone clicks it).
            var help = Assert.Single(buttons, b => (b.Content as string) == "Help");
            Assert.NotNull(help.Command);
            Assert.True(help.Command!.CanExecute(null));

            // The action bar carries the forward pair the sidebar cannot.
            Assert.Single(buttons, b => (b.Content as string) == "← Back");
            Assert.Single(buttons, b => b.Command == vm.Wizard.NextCommand);

            // §4.5: the log view survives the redesign.
            Assert.NotEmpty(window.GetVisualDescendants().OfType<ListBox>());

            // §6.4: the primary action carries the accent colour, which also
            // proves the token really resolved (App.axaml is the test app too).
            var next = Assert.Single(buttons, b => b.Command == vm.Wizard.NextCommand);
            var accent = Assert.IsAssignableFrom<SolidColorBrush>(next.Background);
            Assert.Equal(Color.FromRgb(0x25, 0x63, 0xEB), accent.Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Sidebar_enters_a_mode_and_marks_the_entry_that_is_on_screen()
    {
        // A usable local source: Step 2's gate is a folder that exists, and the
        // session is the shell's own so the sidebar navigates on real state.
        var session = new Session();
        session.Datasource.LocalPath = Path.GetTempPath();
        var window = new MainWindow { DataContext = NewShell(Path.GetTempPath(), session) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.StartTaggingRouteCommand.Execute(null);
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            var engine = Assert.Single(buttons, b => (b.Content as string) == "2 · Engine");
            Assert.True(EffectivelyVisible(engine));
            Assert.DoesNotContain("active", engine.Classes);

            // Enter the engine step: the sidebar entry lights up (§6.4).
            vm.Wizard.GoToStep2Command.Execute(null);
            Assert.Same(vm.Wizard.Step2, vm.Wizard.CurrentStep);
            Assert.Contains("active", engine.Classes);

            // Leaving it clears the highlight again.
            vm.Wizard.GoToStep1Command.Execute(null);
            Assert.Same(vm.Wizard.Step1, vm.Wizard.CurrentStep);
            Assert.DoesNotContain("active", engine.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_dialog_shows_the_engine_view_over_the_wizards_own_view_model()
    {
        var window = new MainWindow { DataContext = NewShell() };
        window.Show();
        var dialog = new EngineSettingsDialog { DataContext = window.DataContext is MainWindowViewModel vm ? vm.Wizard.Step2 : null };
        dialog.Show();
        try
        {
            // One view model, two views: the dialog edits the same instance the
            // wizard step renders, so nothing can drift between them.
            Assert.NotNull(dialog.DataContext);
            Assert.IsType<Step2EngineViewModel>(dialog.DataContext);

            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Single(buttons, b => (b.Content as string) == "Apply");
            Assert.Single(buttons, b => (b.Content as string) == "Close");

            // The settings controls really are inside the dialog — model picker,
            // device, tag fields, prompts.
            Assert.NotEmpty(dialog.GetVisualDescendants().OfType<ComboBox>());
            Assert.NotEmpty(dialog.GetVisualDescendants().OfType<CheckBox>());
            Assert.NotEmpty(dialog.GetVisualDescendants().OfType<TextBox>());
        }
        finally
        {
            dialog.Close();
            window.Close();
        }
    }

    /// <summary>Effective visibility: a control whose own flag is on is still
    /// hidden while an ancestor is collapsed.</summary>
    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);
}
