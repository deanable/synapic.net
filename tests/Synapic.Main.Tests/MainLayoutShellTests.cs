using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The shell after the navigation collapse (ui-design §6.1/§6.2, D-01/D5): a
/// header (title · breadcrumb · source profile · Settings), the content — either
/// the dashboard or the open operation's template — the server footer and the
/// diagnostics drawer. The numbered sidebar and the Back/Next/StartOver action
/// bar are gone, and their absence is what this asserts. These drive the real
/// compiled MainWindow — a mistyped binding path fails silently in Avalonia, so
/// the wired state is what is asserted.
/// </summary>
public class MainLayoutShellTests
{
    private static MainWindowViewModel NewShell(string? exe = null, Session? session = null,
        ConfigService? config = null, IHelpService? help = null) =>
        new(new FakeSidecar(), new FakeBuildService(), session ?? new Session(), () => exe, null, null, _ => exe,
            configService: config, help: help);

    [AvaloniaFact]
    public void Shell_has_a_header_a_footer_and_the_log_and_no_sidebar_or_action_bar()
    {
        // A config file of this test's own: the diagnostics toggle persists, and
        // that must not be the developer's real config.json.
        var tempDir = Path.Combine(Path.GetTempPath(), "synapic-shell-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { DataContext = NewShell(config: new ConfigService(Path.Combine(tempDir, "config.json"))) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            // Header: the source profile is on screen without entering a flow.
            Assert.NotEmpty(window.GetVisualDescendants().OfType<SourceStatusStrip>());

            // The dashboard is the home view (ui-design §2.1): four panels, the
            // way into every operation and into the settings content.
            var panels = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("dashboardPanel")).ToList();
            Assert.Equal(new[] { "SettingsPanel", "TagPanel", "DedupPanel", "UpscalePanel" },
                panels.Select(p => p.Name!).ToArray());

            // One Settings entry point survives — the header shortcut. The sidebar
            // and action-bar copies went with the chrome, so a re-added duplicate
            // fails here.
            var settings = buttons.Where(b => b.Command == vm.SettingsCommand).ToList();
            Assert.Single(settings);
            Assert.NotNull(settings[0].Command);

            // The chrome D-01 deletes is gone, and gone means absent from the tree
            // rather than merely hidden: no numbered sidebar entry, none of the
            // three labels the action bar used to carry.
            Assert.DoesNotContain(buttons, b =>
                b.Classes.Contains("navItem") || b.Classes.Contains("navMode"));
            Assert.DoesNotContain(buttons, b =>
                (b.Content as string) is "← Back" or "Next →" or "Start Over");

            // On the dashboard each operation is entered from exactly one place:
            // its panel. The operation view's mode rail binds these same commands,
            // and it is off screen here, so the count is over what is on screen —
            // the entry points, not every binding of the command.
            Assert.Single(buttons.Where(EffectivelyVisible), b => b.Command == vm.StartTaggingRouteCommand);
            Assert.Single(buttons.Where(EffectivelyVisible), b => b.Command == vm.StartDedupRouteCommand);
            Assert.Single(buttons.Where(EffectivelyVisible), b => b.Command == vm.StartUpscaleRouteCommand);

            // The Dashboard entry lives in the header and is off screen while the
            // dashboard is what is on screen (§6.2: it is the way back from a mode).
            var home = Assert.Single(buttons, b => b.Command == vm.GoHomeCommand);
            Assert.False(EffectivelyVisible(home),
                "the Dashboard entry is on screen while the dashboard is already open");

            // Footer: exactly one Help button and it is wired (a broken binding
            // leaves Command null and looks fine until someone clicks it).
            var help = Assert.Single(buttons, b => (b.Content as string) == "Help");
            Assert.NotNull(help.Command);
            Assert.True(help.Command!.CanExecute(null));

            // §4.5 + D7: the log view survives the redesign as the diagnostics
            // drawer — reachable, but not an always-visible strip. It is hidden
            // until Settings → Logging asks for it, and the toggle really works.
            var log = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), l => l.Name == "LogList");
            Assert.False(EffectivelyVisible(log),
                "the log strip is on screen before Settings → Logging enables diagnostics");
            vm.AppSettings.ShowDiagnostics = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(EffectivelyVisible(log),
                "Settings → Logging did not show the diagnostics view");

            // §6.4: the open operation's primary action carries the accent colour,
            // which also proves the token really resolved (App.axaml is the test
            // app too). On the dashboard nothing is open, so this needs a mode.
            vm.StartTaggingRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var runBar = Assert.Single(window.GetVisualDescendants().OfType<RunStateBar>());
            var start = Assert.Single(runBar.GetVisualDescendants().OfType<Button>(),
                b => b.Classes.Contains("accent"));
            var accent = Assert.IsAssignableFrom<SolidColorBrush>(start.Background);
            Assert.Equal(Color.FromRgb(0x25, 0x63, 0xEB), accent.Color);
        }
        finally
        {
            window.Close();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// The "where am I" marker the sidebar's active entry used to carry is the
    /// header breadcrumb now: entering a mode from its dashboard panel puts the
    /// mode's name there, and the header's Dashboard entry brings it back to the
    /// dashboard (D5 — one navigation model, so one marker).
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_panel_enters_a_mode_and_the_breadcrumb_marks_it()
    {
        // A usable local source, so the mode's own gates are about the work and
        // not about a missing source.
        var session = new Session();
        session.Datasource.LocalPath = Path.GetTempPath();
        var window = new MainWindow { DataContext = NewShell(Path.GetTempPath(), session) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            Assert.True(vm.IsDashboardVisible);
            Assert.Equal("Dashboard", vm.Breadcrumb);

            // The tagging panel is the entry point: its route command, exactly the
            // one the card carries.
            var panel = Assert.Single(window.GetVisualDescendants().OfType<Button>().Where(EffectivelyVisible),
                b => b.Command == vm.StartTaggingRouteCommand);
            panel.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsOperationVisible);
            Assert.Equal("Tagging", vm.OperationTitle);
            Assert.Equal("Dashboard / Tagging", vm.Breadcrumb);
            Assert.Same(vm.TagOperation, vm.Shell.Current);

            // The header's Dashboard entry is now on screen, and it clears the
            // open operation rather than picking some other step.
            var home = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Command == vm.GoHomeCommand);
            Assert.True(EffectivelyVisible(home));
            home.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsDashboardVisible);
            Assert.Equal("Dashboard", vm.Breadcrumb);
            Assert.Null(vm.Shell.Current);
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
    /// inside the main window, with no step to walk through to reach it (D5).
    /// </summary>
    [AvaloniaFact]
    public void Tagging_parameters_are_inline_in_the_parameters_region_not_a_window()
    {
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
            Dispatcher.UIThread.RunJobs();

            // The full form — model, device, tag fields, prompts — is inside the
            // Parameters region of the same window, not a modal of its own.
            var parameters = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("parametersRegion"));
            var form = Assert.Single(window.GetVisualDescendants().OfType<Step2Engine>());
            Assert.Same(vm.Operations.TagParameters, form.DataContext);
            Assert.Contains(form, parameters.GetVisualDescendants().OfType<Control>());
            Assert.Contains(parameters.GetVisualDescendants().OfType<CheckBox>(),
                c => c.Name == "TagCategoriesBox");

            // And the window owning it is the shell itself — nothing opened a
            // second window to hold it.
            Assert.Same(window, form.GetVisualAncestors().OfType<Window>().Last());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ui-design §10 criterion 7: deleting the chrome removes navigation, not the
    /// two things that have to be reachable from everywhere. The server status and
    /// the help entry stay on screen on the dashboard and in each of the three
    /// modes, and the help entry still opens a topic from every one of them.
    /// </summary>
    [AvaloniaFact]
    public async Task Server_status_and_help_stay_reachable_on_the_dashboard_and_every_mode()
    {
        var help = new FakeHelpService();
        var window = new MainWindow { DataContext = NewShell(help: help) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            await vm.DetectServerAsync();

            var visits = new (string Where, Action Enter)[]
            {
                ("dashboard", () => { }),
                ("tag", () => vm.StartTaggingRouteCommand.Execute(null)),
                ("dedup", () => vm.StartDedupRouteCommand.Execute(null)),
                ("upscale", () => vm.StartUpscaleRouteCommand.Execute(null)),
            };

            foreach (var (where, enter) in visits)
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                // The server status footer is on screen (its text is the state).
                // More than one element can carry that text — the footer is the
                // one that has to be visible.
                var statuses = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Text == vm.StatusText).ToList();
                Assert.True(statuses.Any(EffectivelyVisible),
                    $"{where}: the server status is not on screen");

                // And the help entry is present, enabled and opens a topic.
                var helpButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    b => (b.Content as string) == "Help");
                Assert.True(EffectivelyVisible(helpButton), $"{where}: the Help button is not on screen");
                Assert.True(helpButton.Command!.CanExecute(null));

                helpButton.Command.Execute(null);
                Assert.Equal(HelpTopics.Home, help.OpenedTopics[^1]);
            }

            Assert.Equal(visits.Length, help.OpenedTopics.Count);
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
