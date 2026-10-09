using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Dashboard;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;
using Shapes = Avalonia.Controls.Shapes;

namespace Synapic.Main.Tests;

/// <summary>
/// The entry point as the redesign defines it (docs/ui-design.md §2.1, §10
/// criteria 1 and 3, decisions D1/D2/D3/D6): the app opens on the dashboard —
/// four panels, Settings + Tag + Dedup + Upscale — entering an operation is
/// free while its run stays gated, and one shared source is what every mode
/// shows. A mode opens straight onto the three-region template: the route
/// booleans, the sidebar entries and the Back/Next chain are gone, and
/// Shell.Current is the only navigation state (Phase 3 plan 02).
/// AvaloniaFact boots the real App headlessly, which is what initializes the
/// process-global SynapicLog the shell subscribes to (same as ServerDetectionTests).
/// </summary>
public class RouteSplitTests
{
    private static Session SourceSession(string type, string localPath)
    {
        var session = new Session();
        session.Datasource.Type = type;
        session.Datasource.LocalPath = localPath;
        if (type == "daminion")
            session.Datasource.DaminionUrl = "http://damserver.local/daminion";
        return session;
    }

    private static MainWindowViewModel Shell(Session session) =>
        new(new FakeSidecar(), new FakeBuildService(), session, () => null, null, null, _ => null);

    /// <summary>The dashboard's panels, by the class every one of them carries.</summary>
    private static List<Control> DashboardPanels(Window window) =>
        window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Classes.Contains("dashboardPanel"))
            .ToList();

    [AvaloniaFact]
    public void App_opens_on_the_dashboard_with_nothing_open()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        Assert.True(vm.IsDashboardVisible);
        Assert.False(vm.IsOperationVisible);
        Assert.Equal("Dashboard", vm.Breadcrumb);
        Assert.Equal("", vm.OperationTitle);

        // D-03: the dashboard means nothing is open.
        Assert.Null(vm.Shell.Current);
    }

    /// <summary>
    /// ui-design §10 criterion 1: cold start lands on the dashboard with exactly
    /// four panels — Settings, Tag, Dedup, Upscale — and each operation panel
    /// opens its mode. The Settings panel renders its content in place; there is
    /// nothing for it to open (that is §5's settings view, Phase 3).
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_shows_exactly_four_panels_named_Settings_Tag_Dedup_Upscale()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var panels = DashboardPanels(window);
            Assert.Equal(4, panels.Count);
            Assert.Equal(new[] { "SettingsPanel", "TagPanel", "DedupPanel", "UpscalePanel" },
                panels.Select(p => p.Name).ToArray());
            Assert.All(panels, p => Assert.True(EffectivelyVisible(p),
                $"{p.Name} is not on screen when the app starts"));

            // The three operation panels open their mode through the route
            // command, and this is the only place that command is bound now — the
            // sidebar's duplicate mode headers went with the chrome (D5).
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            foreach (var command in new[]
                     {
                         vm.StartTaggingRouteCommand,
                         vm.StartDedupRouteCommand,
                         vm.StartUpscaleRouteCommand,
                     })
                Assert.Single(buttons, b => b.Command == command && b.Classes.Contains("routeCard"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tagging_route_opens_the_template_on_the_tagging_mode()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartTaggingRouteCommand.Execute(null);

        Assert.True(vm.IsOperationVisible);
        Assert.False(vm.IsDashboardVisible);
        Assert.Equal("Tagging", vm.OperationTitle);
        Assert.Equal("Dashboard / Tagging", vm.Breadcrumb);
        // D-03: the open operation is the tagging adapter.
        Assert.Same(vm.TagOperation, vm.Shell.Current);
        Assert.Equal("tag", vm.Shell.Current!.Key);

        // Straight onto the three-region template: the mode's own content is in
        // its regions the moment it opens — there is no step to advance to (D5).
        var host = vm.Operations;
        Assert.Same(host.TagParameters, host.ParametersFor("tag"));
        Assert.Same(host.TagRun, host.RunFor("tag"));
        Assert.Same(host.TagReport, host.ReportFor("tag"));
        Assert.False(host.IsRunning);
    }

    [AvaloniaFact]
    public void Dedup_route_opens_the_template_on_the_dedup_mode()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartDedupRouteCommand.Execute(null);

        Assert.True(vm.IsOperationVisible);
        Assert.Equal("Deduplication", vm.OperationTitle);
        Assert.Same(vm.DedupOperation, vm.Shell.Current);

        // One layout, three modes: the template's Parameters and Output slots are
        // the dedup view model, and no tagging step stands between them (D5).
        var host = vm.Operations;
        Assert.Same(host.Dedup, host.ParametersFor("dedup"));
        Assert.Same(host.Dedup, host.RunFor("dedup"));
        Assert.Null(host.ReportFor("dedup"));   // dedup reviews its groups inside its own page
        Assert.NotSame(host.TagParameters, host.ParametersFor("dedup"));
        Assert.NotSame(host.TagRun, host.RunFor("dedup"));
    }

    [AvaloniaFact]
    public async Task Dedup_mode_reads_the_shared_source_with_no_step_in_between()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartDedupRouteCommand.Execute(null);
        await vm.Operations.Source.RefreshCountAsync();

        // The mode reads the shared source directly: opening it is all it takes,
        // and with a folder that exists the run is ready.
        Assert.Equal(folder, vm.Operations.Dedup.FolderPath);   // the shared source carries over
        Assert.True(vm.Operations.Dedup.IsLocal);
        Assert.True(vm.DedupOperation.IsRunEnabled);
        Assert.Null(vm.DedupOperation.RunDisabledReason);
    }

    [AvaloniaFact]
    public void Dedup_run_is_gated_without_a_source_and_says_why()
    {
        var vm = Shell(SourceSession("local", localPath: ""));

        // Entering the mode without a source is free (D6) — the gate belongs to
        // the run, not to navigation.
        vm.StartDedupRouteCommand.Execute(null);
        Assert.True(vm.IsOperationVisible);

        Assert.False(vm.DedupOperation.IsRunEnabled);
        Assert.NotNull(vm.DedupOperation.RunDisabledReason);
        Assert.Contains("folder", vm.DedupOperation.RunDisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void Dedup_mode_reads_the_catalog_source_from_the_shared_source()
    {
        var vm = Shell(SourceSession("daminion", localPath: ""));

        vm.StartDedupRouteCommand.Execute(null);

        Assert.True(vm.Operations.Dedup.IsDaminion);
        Assert.False(vm.Operations.Dedup.IsLocal);
    }

    /// <summary>
    /// ui-design §10 criterion 3 — source is shared: a folder chosen once is the
    /// source Region A shows on every route (same instance, same label, same
    /// record count), and it is the source the modes themselves read.
    /// </summary>
    [AvaloniaFact]
    public async Task Region_A_strip_shows_the_same_source_and_count_on_every_route()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));
        await vm.Operations.Source.RefreshCountAsync();
        var count = vm.Operations.Source.CountText;
        Assert.False(string.IsNullOrWhiteSpace(count),
            "the shared source has no record count, so criterion 3 cannot be checked");

        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            foreach (var (route, enter) in new (string, Action)[]
                     {
                         ("tag", () => vm.StartTaggingRouteCommand.Execute(null)),
                         ("dedup", () => vm.StartDedupRouteCommand.Execute(null)),
                         ("upscale", () => vm.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();   // the route is visible now: let the template realize Region A

                var strip = Assert.Single(window.GetVisualDescendants().OfType<DatasourceSourcePanel>());
                Assert.Same(vm.Operations.Source, strip.DataContext);
                Assert.True(EffectivelyVisible(strip), $"{route}: Region A is not on screen");

                var texts = strip.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text).ToList();
                Assert.Contains(count, texts);
                Assert.Contains(vm.Operations.Source.LocalSourceText, texts);

                // The modes read that same source rather than a copy of it.
                Assert.Equal(folder, vm.Operations.Dedup.FolderPath);
                Assert.Contains(folder, vm.Operations.Upscale.SourceSummary);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The real compiled XAML: the dashboard panels must carry their commands,
    /// and entering a mode must swap the two content views and hand the template
    /// the open mode's regions. A mistyped binding path is silent in Avalonia, so
    /// this asserts the wired state instead of only the view model.
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_panels_and_mode_visibility_are_bound_in_the_real_window()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("local", Path.GetTempPath())) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            // The dashboard panels carry their commands — one card per operation.
            var tagCard = buttons.Single(b => b.Command == vm.StartTaggingRouteCommand && b.Classes.Contains("routeCard"));
            var dedupCard = buttons.Single(b => b.Command == vm.StartDedupRouteCommand && b.Classes.Contains("routeCard"));
            var upscaleCard = buttons.Single(b => b.Command == vm.StartUpscaleRouteCommand && b.Classes.Contains("routeCard"));
            Assert.True(EffectivelyVisible(tagCard));
            Assert.True(EffectivelyVisible(dedupCard));
            Assert.True(EffectivelyVisible(upscaleCard));

            // Exactly one of the two content views is on screen: the dashboard now,
            // with the header's Dashboard entry off screen because it is not needed.
            Assert.True(EffectivelyVisible(Assert.Single(
                window.GetVisualDescendants().OfType<DashboardView>())));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<OperationLayout>(), EffectivelyVisible);
            Assert.False(EffectivelyVisible(Assert.Single(buttons, b => b.Command == vm.GoHomeCommand)));

            // Each mode brings the template on screen with its own regions, takes
            // the dashboard off it, and lights up the header entry that goes back.
            foreach (var (mode, enter, parameters, run) in new (string, Action, object, object)[]
                     {
                         ("dedup", () => vm.StartDedupRouteCommand.Execute(null), vm.Operations.Dedup, vm.Operations.Dedup),
                         ("upscale", () => vm.StartUpscaleRouteCommand.Execute(null), vm.Operations.Upscale, vm.Operations.Upscale),
                         ("tag", () => vm.StartTaggingRouteCommand.Execute(null), vm.Operations.TagParameters, vm.Operations.TagRun),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                var layout = Assert.Single(window.GetVisualDescendants().OfType<OperationLayout>());
                Assert.True(EffectivelyVisible(layout), $"{mode}: the template is not on screen");
                Assert.Same(parameters, layout.Parameters);
                Assert.Same(run, layout.Run);

                Assert.DoesNotContain(window.GetVisualDescendants().OfType<DashboardView>(), EffectivelyVisible);
                Assert.True(EffectivelyVisible(Assert.Single(buttons, b => b.Command == vm.GoHomeCommand)));
                Assert.Equal($"Dashboard / {vm.OperationTitle}", vm.Breadcrumb);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ui-design D6: the dashboard is never a dead end. The panels are always
    /// enabled and entering a mode is free; what is gated is the run, and the
    /// gate says why. (The start screen used to disable the three cards until a
    /// source existed — that gating is what D6 replaces.)
    /// The retired `CanStartRoute` card gate is deliberately asserted nowhere: no
    /// view binds it any more, so this fact pins what the screen does — the panels
    /// stay enterable and the run carries the gate with its reason.
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_entry_is_free_and_only_the_run_is_gated()
    {
        var vm = Shell(SourceSession("local", localPath: ""));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var panels = DashboardPanels(window);
            var cards = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("routeCard")).ToList();

            Assert.Equal(3, cards.Count);
            Assert.All(panels, p => Assert.True(EffectivelyVisible(p)));
            Assert.All(cards, card => Assert.True(card.IsEnabled,
                "D6: entering a mode is free — no dashboard panel is disabled"));

            // No source yet: the *run* is what is blocked, and it says why.
            Assert.False(vm.DedupOperation.IsRunEnabled);
            Assert.Equal("Choose a folder to scan", vm.DedupOperation.RunDisabledReason);

            // Entering the route without a source is allowed, and the gate is
            // still there once inside — it belongs to the region that is blocked.
            vm.StartDedupRouteCommand.Execute(null);
            Assert.Same(vm.DedupOperation, vm.Shell.Current);
            Assert.False(vm.DedupOperation.IsRunEnabled);

            vm.GoHomeCommand.Execute(null);

            // The strip keeps telling the truth about the source: the folder light
            // is red while no folder exists…
            var strip = window.GetVisualDescendants().OfType<SourceStatusStrip>().First();
            var lights = strip.GetVisualDescendants().OfType<Shapes.Ellipse>().ToList();
            Assert.Equal(2, lights.Count);
            Assert.Equal(Colors.Red, ((ISolidColorBrush)lights[1].Fill!).Color);

            var folder = Path.GetTempPath();
            vm.Operations.Source.LocalPath = folder;

            // …green once it does, and with a usable source the run opens up.
            Assert.Equal(Colors.ForestGreen, ((ISolidColorBrush)lights[1].Fill!).Color);
            Assert.True(vm.DedupOperation.IsRunEnabled);
            Assert.Null(vm.DedupOperation.RunDisabledReason);

            var folderLine = Assert.Single(strip.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text?.StartsWith("Folder:") == true);
            Assert.Contains(folder, folderLine.Text!);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Region A holds the whole source form now, so the region below it has to
    /// keep a real size: the source is laid out above Parameters and Output, and
    /// the dense form does not swallow the viewport. Headless layout runs the
    /// real measure/arrange, so this catches a panel that did.
    /// </summary>
    [AvaloniaFact]
    public void Operation_regions_lay_the_source_form_out_above_the_parameters_region()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("daminion", localPath: "")) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(window.GetVisualDescendants().OfType<DatasourceSourcePanel>());
            var parameters = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("parametersRegion"));

            Assert.True(panel.Bounds.Height > 0, "the source form did not lay out");
            Assert.True(parameters.Bounds.Height > 0, "the Parameters region did not lay out");

            var panelEnd = panel.TranslatePoint(new Point(0, panel.Bounds.Height), window)!.Value.Y;
            var parametersTop = parameters.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(parametersTop >= panelEnd,
                $"the Parameters region overlaps the source form (source ends at {panelEnd}, Parameters starts at {parametersTop})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Daminion connect form has exactly one home: the Data source region of
    /// the operation template (ui-design §8 — local folder, Daminion login,
    /// scope and filters all land there). The dashboard's Settings panel belongs
    /// to app-wide settings now (§5) and carries no source form at all, so the
    /// source cannot be configured in two places.
    /// </summary>
    [AvaloniaFact]
    public void Daminion_connect_form_lives_in_the_data_source_region_only()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("daminion", localPath: "")) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;

            // The dashboard is what is on top, and its Settings panel is the §5
            // settings view: no source form on it.
            Assert.True(vm.IsDashboardVisible);
            Assert.False(vm.IsOperationVisible);
            var settingsPanel = Assert.Single(window.GetVisualDescendants().OfType<Control>(),
                c => c.Name == "SettingsPanel");
            Assert.Empty(settingsPanel.GetVisualDescendants().OfType<DatasourceSourcePanel>());

            // Inside an operation the one shared source is configured in Region A,
            // and that is the only copy of the form in the window.
            vm.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var source = Assert.Single(window.GetVisualDescendants().OfType<DatasourceSourcePanel>());
            Assert.Same(vm.Operations.Source, source.DataContext);

            var connect = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Command == vm.Operations.Source.ConnectCommand);
            Assert.True(EffectivelyVisible(connect));
            Assert.Contains(connect.GetVisualAncestors().OfType<Control>(),
                c => c.Classes.Contains("dataSourceRegion"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Home_returns_to_the_dashboard_and_another_mode_opens_on_the_shared_source()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartDedupRouteCommand.Execute(null);
        Assert.Same(vm.DedupOperation, vm.Shell.Current);

        vm.GoHomeCommand.Execute(null);

        Assert.True(vm.IsDashboardVisible);
        Assert.False(vm.IsOperationVisible);
        Assert.Equal("Dashboard", vm.Breadcrumb);
        Assert.Null(vm.Shell.Current);          // D-03: the dashboard means nothing is open

        // Picking another mode later opens it on the same shared source: there is
        // no step position to resume mid-flow, and what survives the trip is the
        // configured source and settings.
        vm.StartTaggingRouteCommand.Execute(null);
        Assert.Same(vm.TagOperation, vm.Shell.Current);
        Assert.Equal("Tagging", vm.OperationTitle);
        Assert.Equal(folder, vm.Operations.Dedup.FolderPath);   // still the one source (D4)
    }

    /// <summary>Effective visibility: a control whose own flag is on is still
    /// hidden while the content view it lives in is collapsed on the dashboard.</summary>
    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);
}
