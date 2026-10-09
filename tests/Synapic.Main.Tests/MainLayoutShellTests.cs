using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
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

            // Header: the source profile is on screen without entering a flow.
            Assert.NotEmpty(window.GetVisualDescendants().OfType<SourceStatusStrip>());

            // The home route is the dashboard (ui-design §2.1): four panels, the
            // way into every operation and into the settings content.
            var panels = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("dashboardPanel")).ToList();
            Assert.Equal(new[] { "SettingsPanel", "TagPanel", "DedupPanel", "UpscalePanel" },
                panels.Select(p => p.Name!).ToArray());

            // The Settings shortcut has three entry points — header, sidebar and
            // the action bar — and all of them are wired (a broken binding leaves
            // Command null, which looks fine until somebody clicks it).
            var settings = buttons.Where(b => b.Command == vm.SettingsCommand).ToList();
            Assert.Equal(3, settings.Count);
            Assert.All(settings, b => Assert.NotNull(b.Command));

            // Sidebar: one dashboard entry (the way back to the dashboard from
            // inside an operation), and every mode header reachable both from the
            // dashboard panels and from the sidebar.
            Assert.Single(buttons, b => b.Command == vm.GoHomeCommand);
            Assert.Equal("2 · Operation type", buttons.Single(b => b.Command == vm.GoHomeCommand).Content);
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

            var sourceEntry = Assert.Single(buttons, b => (b.Content as string) == "1 · Source & model");
            var settingsEntry = Assert.Single(buttons, b => (b.Content as string) == "3 · Settings…");
            var process = Assert.Single(buttons, b => (b.Content as string) == "4 · Process");
            Assert.True(EffectivelyVisible(process));
            Assert.DoesNotContain("active", process.Classes);
            Assert.Contains("active", sourceEntry.Classes);   // the route opens on it

            // Enter the settings page: the sidebar entry for it lights up (§6.4),
            // source & model stops being the current one, and the run step becomes
            // reachable (that is the step Next leaves for).
            vm.Wizard.GoToStep2Command.Execute(null);
            Assert.Same(vm.Wizard.Step2, vm.Wizard.CurrentStep);
            Assert.Contains("active", settingsEntry.Classes);
            Assert.DoesNotContain("active", sourceEntry.Classes);
            Assert.True(vm.Wizard.CanGoToStep3Tab);
            Assert.True(process.IsEnabled);

            // Leaving it clears the highlight again.
            vm.Wizard.GoToStep1Command.Execute(null);
            Assert.Same(vm.Wizard.Step1, vm.Wizard.CurrentStep);
            Assert.DoesNotContain("active", settingsEntry.Classes);
            Assert.Contains("active", sourceEntry.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ui-design §10 criterion 4 / D3: the three operation settings dialogs are
    /// retired. They are gone from the built assembly (a re-introduced dialog
    /// class fails here, not just a grep), and the tagging form those dialogs
    /// hosted now lives inline in the operation template's Parameters region —
    /// inside the main window, in the same view model the step renders.
    /// </summary>
    [AvaloniaFact]
    public void Tagging_parameters_are_inline_in_the_parameters_region_not_a_window()
    {
        // The tagging route needs a usable source before its settings step opens
        // (the step gate is the session's, exactly as in the app).
        var session = new Session();
        session.Datasource.LocalPath = Path.GetTempPath();
        var window = new MainWindow { DataContext = NewShell(Path.GetTempPath(), session) };
        window.Show();
        try
        {
            var assembly = typeof(MainWindowViewModel).Assembly;
            Assert.Null(assembly.GetType("Synapic.Main.Views.Settings.EngineSettingsDialog"));
            Assert.Null(assembly.GetType("Synapic.Main.Views.Settings.DedupSettingsDialog"));
            Assert.Null(assembly.GetType("Synapic.Main.Views.Settings.UpscaleSettingsDialog"));

            var vm = (MainWindowViewModel)window.DataContext!;
            vm.StartTaggingRouteCommand.Execute(null);
            vm.Wizard.GoToStep2Command.Execute(null);
            Assert.Same(vm.Wizard.Step2, vm.Wizard.CurrentStep);
            Dispatcher.UIThread.RunJobs();

            // The full form — model, device, tag fields, prompts — is inside the
            // Parameters region of the same window, not a modal of its own.
            var parameters = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("parametersRegion"));
            var form = Assert.Single(window.GetVisualDescendants().OfType<Step2Engine>());
            Assert.Same(vm.Wizard.Step2, form.DataContext);
            Assert.Contains(form, parameters.GetVisualDescendants().OfType<Control>());
            Assert.Contains(parameters.GetVisualDescendants().OfType<CheckBox>(),
                c => c.Name == "TagCategoriesBox");

            // And the window owning it is the shell itself.
            Assert.Same(window, form.GetVisualAncestors().OfType<Window>().Last());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Effective visibility: a control whose own flag is on is still
    /// hidden while an ancestor is collapsed.</summary>
    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);
}
