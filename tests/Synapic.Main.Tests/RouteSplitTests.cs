using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Settings;
using Synapic.Main.Views.Wizard;
using Xunit;
using Shapes = Avalonia.Controls.Shapes;

namespace Synapic.Main.Tests;

/// <summary>
/// The entry point as the sidebar redesign defines it (docs/mock-up/Mockup.svg):
/// the app opens on the Settings view — the first sidebar row, where every
/// configuration lives — and the other three rows open the processing views,
/// which show only the shared source (read-only) and their own output. There is
/// no dashboard and no numbered sequence: Shell.Current is the only navigation
/// state, and null means the Settings view.
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

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    [AvaloniaFact]
    public void App_opens_on_settings_with_nothing_open()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        Assert.True(vm.IsSettingsVisible);
        Assert.False(vm.IsOperationVisible);
        Assert.Equal("", vm.OperationTitle);

        // The Settings view means nothing is open.
        Assert.Null(vm.Shell.Current);
    }

    /// <summary>
    /// The shell opens straight onto the Settings view with its configuration
    /// sections — the data source/connection, the inference server and the
    /// inference engine — and no dashboard panel grid at all.
    /// </summary>
    [AvaloniaFact]
    public void Cold_start_opens_the_settings_view_with_its_configuration_sections()
    {
        var vm = Shell(SourceSession("daminion", localPath: ""));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.True(vm.IsSettingsVisible);

            var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());
            Assert.True(EffectivelyVisible(settings));
            foreach (var section in new[] { "SectionDataSource", "SectionInferenceServer", "SectionEngine", "SectionDedup", "SectionUpscale" })
                Assert.NotNull(settings.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == section));

            // The dashboard's panels and route cards are gone from the tree.
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(),
                c => c.Classes.Contains("dashboardPanel") || c.Classes.Contains("routeCard"));

            // The processing view is not on screen.
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<OperationLayout>(), EffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tagging_route_opens_the_processing_view_on_the_tagging_operation()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartTaggingRouteCommand.Execute(null);

        Assert.True(vm.IsOperationVisible);
        Assert.False(vm.IsSettingsVisible);
        Assert.Equal("Tagging", vm.OperationTitle);
        // The open operation is the tagging adapter.
        Assert.Same(vm.TagOperation, vm.Shell.Current);
        Assert.Equal("tag", vm.Shell.Current!.Key);

        // The operation's own content is in its slots the moment it opens.
        var host = vm.Operations;
        Assert.Same(host.TagRun, host.RunFor("tag"));
        Assert.Same(host.TagReport, host.ReportFor("tag"));
        Assert.False(host.IsRunning);
    }

    [AvaloniaFact]
    public void Dedup_route_opens_the_processing_view_on_the_dedup_operation()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartDedupRouteCommand.Execute(null);

        Assert.True(vm.IsOperationVisible);
        Assert.Equal("Deduplication", vm.OperationTitle);
        Assert.Same(vm.DedupOperation, vm.Shell.Current);

        var host = vm.Operations;
        Assert.Same(host.Dedup, host.RunFor("dedup"));
        Assert.Null(host.ReportFor("dedup"));   // dedup reviews its groups inside its own page
        Assert.NotSame(host.TagRun, host.RunFor("dedup"));
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task Dedup_mode_reads_the_shared_source_with_no_step_in_between()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartDedupRouteCommand.Execute(null);
        await vm.Operations.Source.RefreshCountAsync();

        Assert.Equal(folder, vm.Operations.Dedup.FolderPath);   // the shared source carries over
        Assert.True(vm.Operations.Dedup.IsLocal);
        Assert.True(vm.DedupOperation.IsRunEnabled);
        Assert.Null(vm.DedupOperation.RunDisabledReason);
    }

    [AvaloniaFact]
    public void Dedup_run_is_gated_without_a_source_and_says_why()
    {
        var vm = Shell(SourceSession("local", localPath: ""));

        // Entering the view without a source is free (D6) — the gate belongs to
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
    /// The shared source is configured once, on the Settings view (DatasourceSourcePanel
    /// over the one shell-owned source), and every processing view shows it
    /// read-only — same instance, same label, same record count — and reads it.
    /// </summary>
    [AvaloniaFact]
    public async System.Threading.Tasks.Task Settings_owns_the_source_form_and_every_view_shows_the_same_source()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));
        await vm.Operations.Source.RefreshCountAsync();
        var count = vm.Operations.Source.CountText;
        Assert.False(string.IsNullOrWhiteSpace(count),
            "the shared source has no record count, so this cannot be checked");

        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            // On Settings the source is editable: its form is on screen, bound to
            // the one shared instance, and it is the only copy in the window.
            var form = Assert.Single(window.GetVisualDescendants().OfType<DatasourceSourcePanel>());
            Assert.Same(vm.Operations.Source, form.DataContext);
            Assert.True(EffectivelyVisible(form));

            // Every processing view keeps the same source, read-only: the strip is
            // on screen and there is no editable form.
            foreach (var (route, enter) in new (string, Action)[]
                     {
                         ("tag", () => vm.StartTaggingRouteCommand.Execute(null)),
                         ("dedup", () => vm.StartDedupRouteCommand.Execute(null)),
                         ("upscale", () => vm.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();   // the view is visible now: let the template realize it

                Assert.DoesNotContain(window.GetVisualDescendants().OfType<DatasourceSourcePanel>(),
                    EffectivelyVisible);

                var strip = Assert.Single(window.GetVisualDescendants().OfType<SourceStatusStrip>(),
                    EffectivelyVisible);
                Assert.Same(vm.Operations.Source, strip.DataContext);
                Assert.True(EffectivelyVisible(strip), $"{route}: the source summary is not on screen");

                var texts = strip.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text).ToList();
                Assert.Contains(vm.Operations.Source.LocalSourceText, texts);
                // The count keeps itself current on the shared source (it is read
                // off the same instance the Settings view's form shows).
                Assert.Equal(count, vm.Operations.Source.CountText);

                // The operations read that same source rather than a copy of it.
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
    /// The real compiled XAML: the sidebar rows carry their commands, and entering
    /// a view swaps the content and hands the template the open operation's run
    /// and report. A mistyped binding path is silent in Avalonia, so this asserts
    /// the wired state instead of only the view model.
    /// </summary>
    [AvaloniaFact]
    public void Sidebar_rows_and_view_visibility_are_bound_in_the_real_window()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("local", Path.GetTempPath())) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;

            var nav = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("navItem")).ToList();
            var settings = nav.Single(b => b.Command == vm.GoHomeCommand);
            var tag = nav.Single(b => b.Command == vm.StartTaggingRouteCommand);
            var dedup = nav.Single(b => b.Command == vm.StartDedupRouteCommand);
            var upscale = nav.Single(b => b.Command == vm.StartUpscaleRouteCommand);
            Assert.All(nav, b => Assert.True(EffectivelyVisible(b)));

            // Settings is on screen at launch; no processing view is.
            Assert.True(EffectivelyVisible(Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>())));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<OperationLayout>(), EffectivelyVisible);
            Assert.Contains("active", settings.Classes);

            // Each row brings its view on screen with its own run/report slots and
            // lights up only itself.
            foreach (var (mode, row, enter, run, report) in new (string, Button, Action, object, object?)[]
                     {
                         ("dedup", dedup, () => vm.StartDedupRouteCommand.Execute(null), vm.Operations.Dedup, null),
                         ("upscale", upscale, () => vm.StartUpscaleRouteCommand.Execute(null), vm.Operations.Upscale, null),
                         ("tag", tag, () => vm.StartTaggingRouteCommand.Execute(null), vm.Operations.TagRun, vm.Operations.TagReport),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                var layout = Assert.Single(window.GetVisualDescendants().OfType<OperationLayout>());
                Assert.True(EffectivelyVisible(layout), $"{mode}: the processing view is not on screen");
                Assert.Same(run, layout.Run);
                Assert.Same(report, layout.Report);

                Assert.DoesNotContain(window.GetVisualDescendants().OfType<SettingsTab>(), EffectivelyVisible);
                Assert.Contains("active", row.Classes);
                Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    b => b.Classes.Contains("navItem") && b.Classes.Contains("active"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ui-design D6: entering a view is never a dead end and never gated. What is
    /// gated is the run, and the gate says why.
    /// </summary>
    [AvaloniaFact]
    public void Entering_a_view_is_free_and_only_the_run_is_gated()
    {
        var vm = Shell(SourceSession("local", localPath: ""));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var nav = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("navItem")).ToList();
            Assert.All(nav, row => Assert.True(row.IsEnabled,
                "D6: entering a view is free — no sidebar row is disabled"));

            // No source yet: the *run* is what is blocked, and it says why.
            Assert.False(vm.DedupOperation.IsRunEnabled);
            Assert.Equal("Choose a folder to scan", vm.DedupOperation.RunDisabledReason);

            vm.StartDedupRouteCommand.Execute(null);
            Assert.Same(vm.DedupOperation, vm.Shell.Current);
            Assert.False(vm.DedupOperation.IsRunEnabled);

            vm.GoHomeCommand.Execute(null);

            // The summary keeps telling the truth about the source: the folder
            // light is red while no folder exists…
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
    /// The Settings view's source form lays out above the sections below it: the
    /// dense form does not swallow the viewport. Headless layout runs the real
    /// measure/arrange, so this catches a panel that did.
    /// </summary>
    [AvaloniaFact]
    public void Settings_lays_the_source_form_out_above_the_engine_section()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("daminion", localPath: "")) };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(window.GetVisualDescendants().OfType<DatasourceSourcePanel>());
            var engine = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                b => b.Name == "SectionEngine");

            Assert.True(panel.Bounds.Height > 0, "the source form did not lay out");
            Assert.True(engine.Bounds.Height > 0, "the engine section did not lay out");

            var panelEnd = panel.TranslatePoint(new Point(0, panel.Bounds.Height), window)!.Value.Y;
            var engineTop = engine.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(engineTop >= panelEnd,
                $"the engine section overlaps the source form (source ends at {panelEnd}, engine starts at {engineTop})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Daminion connect form has exactly one home: the Settings view's Data
    /// source section. No processing view carries a source form, so the source
    /// cannot be configured in two places.
    /// </summary>
    [AvaloniaFact]
    public void Daminion_connect_form_lives_on_the_settings_view_only()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("daminion", localPath: "")) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;

            Assert.True(vm.IsSettingsVisible);
            var source = Assert.Single(window.GetVisualDescendants().OfType<DatasourceSourcePanel>());
            Assert.Same(vm.Operations.Source, source.DataContext);

            var connect = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Command == vm.Operations.Source.ConnectCommand);
            Assert.True(EffectivelyVisible(connect));
            Assert.Contains(connect.GetVisualAncestors().OfType<Control>(),
                c => c.Name == "SectionDataSource");

            // Inside an operation there is no source form to configure.
            vm.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<DatasourceSourcePanel>(),
                EffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Home_returns_to_settings_and_another_view_opens_on_the_shared_source()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartDedupRouteCommand.Execute(null);
        Assert.Same(vm.DedupOperation, vm.Shell.Current);

        vm.GoHomeCommand.Execute(null);

        Assert.True(vm.IsSettingsVisible);
        Assert.False(vm.IsOperationVisible);
        Assert.Null(vm.Shell.Current);

        // Picking another view later opens it on the same shared source: what
        // survives the trip is the configured source and settings.
        vm.StartTaggingRouteCommand.Execute(null);
        Assert.Same(vm.TagOperation, vm.Shell.Current);
        Assert.Equal("Tagging", vm.OperationTitle);
        Assert.Equal(folder, vm.Operations.Dedup.FolderPath);   // still the one source (D4)
    }
}
