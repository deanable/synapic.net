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
using Synapic.Main.Views.Settings;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The shell after the sidebar redesign (docs/mock-up/Mockup.svg): a left sidebar
/// of four navigation rows — Settings, Tagging, Deduplication, Upscaling — and one
/// content panel to its right. Settings is where every configuration lives; the
/// other three are processing views. The old dashboard, the numbered sidebar and
/// the Back/Next/StartOver action bar are gone, and their absence is what this
/// asserts. These drive the real compiled MainWindow — a mistyped binding path
/// fails silently in Avalonia, so the wired state is what is asserted.
/// </summary>
public class MainLayoutShellTests
{
    private static MainWindowViewModel NewShell(string? exe = null, Session? session = null,
        ConfigService? config = null, IHelpService? help = null) =>
        new(new FakeSidecar(), new FakeBuildService(), session ?? new Session(), () => exe, null, null, _ => exe,
            configService: config, help: help);

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    /// <summary>The sidebar's four navigation rows, in the mock-up's order.</summary>
    private static System.Collections.Generic.List<Button> NavRows(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("navItem")).ToList();

    /// <summary>A row's label text — the part compact mode drops, so the row is
    /// identified by it rather than by its content.</summary>
    private static string Label(Button row) =>
        row.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("navLabel")).Text!;

    private static Border Sidebar(Window window) =>
        Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("sidebar"));

    [AvaloniaFact]
    public void Shell_has_a_sidebar_of_four_nav_rows_and_no_dashboard_or_action_bar()
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

            // The sidebar's four rows, in reading order, each wired to its view's
            // command (a broken binding leaves Command null and looks fine until
            // it is clicked).
            var nav = NavRows(window);
            Assert.Equal(
                new[] { "Settings", "Tagging", "Deduplication", "Upscaling" },
                nav.Select(Label).ToArray());
            Assert.All(nav, b => Assert.True(EffectivelyVisible(b), $"{b.Content} is not on screen"));
            Assert.Equal(vm.GoHomeCommand, nav[0].Command);
            Assert.Equal(vm.StartTaggingRouteCommand, nav[1].Command);
            Assert.Equal(vm.StartDedupRouteCommand, nav[2].Command);
            Assert.Equal(vm.StartUpscaleRouteCommand, nav[3].Command);

            // The app opens on Settings: its row is the lit one and nothing else is.
            Assert.True(vm.IsSettingsVisible);
            Assert.Null(vm.Shell.Current);
            Assert.Contains("active", nav[0].Classes);
            Assert.All(nav.Skip(1), b => Assert.DoesNotContain("active", b.Classes));

            // The chrome the redesign deletes is gone, and gone means absent from
            // the tree rather than merely hidden: no numbered sidebar entry, none
            // of the three labels the action bar used to carry, no dashboard panel.
            Assert.DoesNotContain(buttons, b => b.Classes.Contains("navMode"));
            Assert.DoesNotContain(buttons, b =>
                (b.Content as string) is "← Back" or "Next →" or "Start Over");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(),
                c => c.Classes.Contains("dashboardPanel"));

            // The Settings view — the first row's content — is what is on screen,
            // and it hosts the configuration sections.
            var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());
            Assert.True(EffectivelyVisible(settings));
            Assert.NotNull(settings.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "SectionDataSource"));
            Assert.NotNull(settings.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "SectionInferenceServer"));
            Assert.NotNull(settings.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "SectionEngine"));

            // The processing view is off screen while Settings is on screen.
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<OperationLayout>(), EffectivelyVisible);

            // Footer: exactly one Help button, pinned to the sidebar and wired.
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
            // app too). Open a mode to reach it.
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
    /// The sidebar's row is the "where am I" marker now: pressing a mode's row
    /// opens it and lights only that row, and the Settings row brings it back to
    /// the configuration view (one navigation model, so one marker).
    /// </summary>
    [AvaloniaFact]
    public void Sidebar_rows_switch_the_view_and_light_only_the_open_one()
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
            Assert.True(vm.IsSettingsVisible);

            // The tagging row is the entry point: its route command, exactly the
            // one the row carries.
            var tagRow = NavRows(window).Single(b => b.Command == vm.StartTaggingRouteCommand);
            tagRow.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsOperationVisible);
            Assert.False(vm.IsSettingsVisible);
            Assert.Equal("Tagging", vm.OperationTitle);
            Assert.Same(vm.TagOperation, vm.Shell.Current);

            var lit = Assert.Single(NavRows(window), b => b.Classes.Contains("active"));
            Assert.Same(tagRow, lit);

            // Pressing another row moves the light with it.
            var dedupRow = NavRows(window).Single(b => b.Command == vm.StartDedupRouteCommand);
            dedupRow.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(vm.DedupOperation, vm.Shell.Current);
            lit = Assert.Single(NavRows(window), b => b.Classes.Contains("active"));
            Assert.Same(dedupRow, lit);

            // The Settings row clears the open operation and comes back to the
            // configuration view rather than picking some other operation.
            var settingsRow = NavRows(window).Single(b => b.Command == vm.GoHomeCommand);
            settingsRow.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsSettingsVisible);
            Assert.Null(vm.Shell.Current);
            lit = Assert.Single(NavRows(window), b => b.Classes.Contains("active"));
            Assert.Same(settingsRow, lit);

            // The sidebar stays on screen in every view — it is the navigation.
            Assert.All(NavRows(window), b => Assert.True(EffectivelyVisible(b)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Below the width threshold the sidebar collapses to an icon-only rail so a
    /// narrow window keeps its content width; above it the labelled rows come
    /// back. The rows keep their icon either way — only the label goes.
    /// </summary>
    [AvaloniaFact]
    public void Sidebar_collapses_to_icons_below_the_width_threshold()
    {
        var wide = new MainWindow { DataContext = NewShell(), Width = 1280, Height = 800 };
        wide.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var sidebar = Sidebar(wide);
            Assert.DoesNotContain("compact", sidebar.Classes);
            Assert.All(NavRows(wide).SelectMany(r => r.GetVisualDescendants().OfType<TextBlock>())
                    .Where(t => t.Classes.Contains("navLabel")),
                t => Assert.True(t.IsVisible, "a label is hidden in the full sidebar"));
            Assert.Equal("Help",
                (string?)Assert.Single(wide.GetVisualDescendants().OfType<Button>(),
                    b => b.Name == "HelpButton").Content);
        }
        finally
        {
            wide.Close();
        }

        // A narrow window: the same rows, icon-only, and a narrower rail.
        var narrow = new MainWindow { DataContext = NewShell(), Width = 640, Height = 480 };
        narrow.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var sidebar = Sidebar(narrow);
            Assert.Contains("compact", sidebar.Classes);
            Assert.True(sidebar.Bounds.Width < 100, $"the rail is {sidebar.Bounds.Width} px wide, not collapsed");

            // Every row still carries its icon and its command; only the label is gone.
            var nav = NavRows(narrow);
            Assert.Equal(4, nav.Count);
            Assert.All(nav, row =>
            {
                var icon = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                    t => !t.Classes.Contains("navLabel"));
                Assert.False(string.IsNullOrWhiteSpace(icon.Text), "the row lost its icon");
            });
            Assert.All(nav.SelectMany(r => r.GetVisualDescendants().OfType<TextBlock>())
                    .Where(t => t.Classes.Contains("navLabel")),
                t => Assert.False(t.IsVisible, "a label is still on screen in the collapsed rail"));
            Assert.Equal("?",
                (string?)Assert.Single(narrow.GetVisualDescendants().OfType<Button>(),
                    b => b.Name == "HelpButton").Content);
        }
        finally
        {
            narrow.Close();
        }
    }

    /// <summary>
    /// The engine form has exactly one home now: the Settings view's Inference
    /// engine section. A processing view reports the run and never re-asks for the
    /// settings — no tag-field checkbox, no model list — so the form is never the
    /// only view of the state it describes nor a second editable copy of it.
    /// </summary>
    [AvaloniaFact]
    public void Engine_form_lives_on_settings_not_in_the_processing_view()
    {
        var session = new Session();
        session.Datasource.LocalPath = Path.GetTempPath();
        var shell = NewShell(Path.GetTempPath(), session);
        shell.Operations.TagParameters.TagCategories = false;

        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            // Editable on the Settings view (the app opens there).
            var form = Assert.Single(window.GetVisualDescendants().OfType<Views.Wizard.Step2Engine>());
            Assert.Same(shell.Operations.TagParameters, form.DataContext);
            Assert.Contains(form.GetVisualDescendants().OfType<CheckBox>(),
                c => c.Name == "TagCategoriesBox");

            // None in the processing view: open Tagging and the form is gone,
            // while the run surface is what is there — and the run surface is the
            // shared bar, the only one.
            shell.StartTaggingRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Views.Wizard.Step2Engine>(),
                EffectivelyVisible);
            var run = Assert.Single(window.GetVisualDescendants().OfType<RunStateBar>());
            Assert.Same(shell.Operations.TagRun, run.DataContext);
            Assert.Empty(run.GetVisualDescendants().OfType<CheckBox>());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ui-design §10 criterion 7: the two things that have to be reachable from
    /// everywhere stay on screen in every view. The server status and the help
    /// entry live in the sidebar, and the help entry still opens a topic from each
    /// view.
    /// </summary>
    [AvaloniaFact]
    public async System.Threading.Tasks.Task Server_status_and_help_stay_reachable_on_settings_and_every_view()
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
                ("settings", () => { }),
                ("tag", () => vm.StartTaggingRouteCommand.Execute(null)),
                ("dedup", () => vm.StartDedupRouteCommand.Execute(null)),
                ("upscale", () => vm.StartUpscaleRouteCommand.Execute(null)),
            };

            foreach (var (where, enter) in visits)
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                // The server status is on screen (its text is the state). More
                // than one element can carry that text — the sidebar is the one
                // that has to be visible.
                var statuses = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Text == vm.StatusText).ToList();
                Assert.Contains(statuses, EffectivelyVisible);

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
}
