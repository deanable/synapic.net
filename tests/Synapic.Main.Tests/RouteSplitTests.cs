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
using Synapic.Main.Views.Wizard;
using Xunit;
using Shapes = Avalonia.Controls.Shapes;

namespace Synapic.Main.Tests;

/// <summary>
/// The entry point as the redesign defines it (docs/ui-design.md §2.1, §10
/// criteria 1 and 3, decisions D1/D2/D3/D6): the app opens on the dashboard —
/// four panels, Settings + Tag + Dedup + Upscale — entering an operation is
/// free while its run stays gated, and one shared source is what every route
/// shows. The strangler-window navigation facts (route booleans, sidebar
/// entries, Back/Next) still hold here until Phase 3 collapses them.
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
    public void App_opens_on_the_dashboard_with_no_route_selected()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        Assert.Equal(MainWindowViewModel.HomeRoute, vm.Route);
        Assert.True(vm.IsHomeVisible);
        Assert.False(vm.IsWizardVisible);
        Assert.False(vm.IsTaggingRoute);
        Assert.False(vm.IsDedupRoute);
        Assert.Equal("", vm.RouteTitle);

        // D-03: the dashboard means nothing is open.
        Assert.Null(vm.Shell.Current);
    }

    /// <summary>
    /// ui-design §10 criterion 1: cold start lands on the dashboard with exactly
    /// four panels — Settings, Tag, Dedup, Upscale — and each one opens its view.
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

            // The three operation panels open their mode through the same command
            // the sidebar's mode header uses — one entry point per operation here,
            // one in the sidebar.
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
    public void Tagging_route_starts_the_wizard_at_source_and_model()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartTaggingRouteCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.TaggingRoute, vm.Route);
        Assert.True(vm.IsWizardVisible);
        Assert.False(vm.IsHomeVisible);
        Assert.Equal("Tagging", vm.RouteTitle);
        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.True(vm.Wizard.ShowTaggingTabs);
        Assert.Equal("Next: Settings \u2192", vm.Wizard.NextButtonText);
        // Deduplication stays a tail-end step of the tagging wizard.
        Assert.False(vm.Wizard.CanGoToDedupTab);
        // D-03: the open operation is the tagging adapter.
        Assert.Same(vm.TagOperation, vm.Shell.Current);
        Assert.Equal("tag", vm.Shell.Current!.Key);
    }

    [AvaloniaFact]
    public void Dedup_route_offers_only_Datasource_and_Deduplication()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartDedupRouteCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.DedupRoute, vm.Route);
        Assert.True(vm.IsDedupRoute);
        Assert.Equal("Deduplication", vm.RouteTitle);
        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.False(vm.Wizard.ShowTaggingTabs);   // Engine/Process/Results hidden
        Assert.False(vm.Wizard.CanGoToStep2Tab);
        Assert.False(vm.Wizard.CanGoToStep3Tab);
        Assert.False(vm.Wizard.CanGoToStep4Tab);
        Assert.True(vm.Wizard.CanGoToDedupTab);    // reachable straight from Step 1
        Assert.Equal("Next: Deduplication \u2192", vm.Wizard.NextButtonText);
        Assert.Same(vm.DedupOperation, vm.Shell.Current);
    }

    [AvaloniaFact]
    public async Task Dedup_route_Next_skips_the_wizard_and_lands_on_Dedup_with_the_source()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartDedupRouteCommand.Execute(null);
        await vm.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Same(vm.Wizard.Dedup, vm.Wizard.CurrentStep);
        Assert.Equal(4, vm.Wizard.CurrentStepIndex);
        Assert.Equal(folder, vm.Wizard.Dedup.FolderPath);   // the shared source carries over
        Assert.True(vm.Wizard.Dedup.IsLocal);
        Assert.Null(vm.Wizard.ValidationError);
    }

    [AvaloniaFact]
    public async Task Dedup_route_refuses_to_leave_Datasource_without_a_source()
    {
        var vm = Shell(SourceSession("local", localPath: ""));

        vm.StartDedupRouteCommand.Execute(null);
        await vm.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.NotNull(vm.Wizard.ValidationError);
        Assert.Contains("folder", vm.Wizard.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Dedup_route_selects_the_catalog_source_on_the_dedup_step()
    {
        var vm = Shell(SourceSession("daminion", localPath: ""));

        vm.StartDedupRouteCommand.Execute(null);
        await vm.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Same(vm.Wizard.Dedup, vm.Wizard.CurrentStep);
        Assert.True(vm.Wizard.Dedup.IsDaminion);
        Assert.False(vm.Wizard.Dedup.IsLocal);
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
        await vm.Wizard.Step1.RefreshCountAsync();
        var count = vm.Wizard.Step1.CountText;
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

                var strip = Assert.Single(window.GetVisualDescendants().OfType<DataSourceStrip>());
                Assert.Same(vm.Wizard.Step1, strip.DataContext);

                var texts = strip.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text).ToList();
                Assert.Contains(count, texts);
                Assert.Contains(vm.Wizard.Step1.LocalSourceText, texts);

                // The modes read that same source rather than a copy of it.
                Assert.Equal(folder, vm.Wizard.Dedup.FolderPath);
                Assert.Contains(folder, vm.Wizard.Upscale.SourceSummary);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The real compiled XAML: the dashboard panels must carry their commands,
    /// and switching routes must show/hide the nav bar and the tagging tabs.
    /// A mistyped binding path is silent in Avalonia, so this asserts the wired
    /// state instead of only the view model.
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_panels_and_route_visibility_are_bound_in_the_real_window()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("local", Path.GetTempPath())) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            // The dashboard panels, not the sidebar's mode headers: both are
            // bound to the same route commands by design (the sidebar switches mode).
            var tagCard = buttons.Single(b => b.Command == vm.StartTaggingRouteCommand && b.Classes.Contains("routeCard"));
            var dedupCard = buttons.Single(b => b.Command == vm.StartDedupRouteCommand && b.Classes.Contains("routeCard"));
            var operationEntry = buttons.Single(b => b.Command == vm.GoHomeCommand);
            Assert.True(tagCard.IsVisible);
            Assert.True(dedupCard.IsVisible);

            // The dashboard entry ("2 · Operation type") is the way back to the
            // dashboard from inside a route, so it is on screen always;
            // the sidebar's workflow group only appears once a route is active.
            Assert.Equal("2 · Operation type", operationEntry.Content);
            Assert.True(EffectivelyVisible(operationEntry));
            Assert.False(IsTabVisible(window, "4 · Process"));
            Assert.False(IsTabVisible(window, "3 · Settings…"));

            vm.StartDedupRouteCommand.Execute(null);
            Assert.False(IsTabVisible(window, "4 · Process"));      // dedup route: no tagging steps
            Assert.False(IsTabVisible(window, "5 · Results"));
            Assert.True(IsTabVisible(window, "4 · Deduplication"));
            Assert.True(IsTabVisible(window, "3 · Settings…"));
            // The route reads as Source & model → Deduplication, so step 1 is on screen too.
            Assert.True(IsTabVisible(window, "1 · Source & model"));

            vm.StartTaggingRouteCommand.Execute(null);
            Assert.True(IsTabVisible(window, "4 · Process"));
            Assert.True(IsTabVisible(window, "5 · Results"));
            // The other operations stay one click away through their mode
            // headers; their own run steps appear once they own the wizard.
            Assert.True(IsTabVisible(window, "🧹  Dedup"));
            Assert.False(IsTabVisible(window, "4 · Deduplication"));
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
            Assert.False(vm.CanStartRoute);

            var folder = Path.GetTempPath();
            vm.Wizard.Step1.LocalPath = folder;

            // …green once it does, and with a usable source the run opens up.
            Assert.True(vm.CanStartRoute);
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
    /// The dashboard has to stay usable with the source panel on it: the panel is
    /// laid out above the three operation panels, and they keep a real size
    /// instead of being pushed out of the window. Headless layout runs the real
    /// measure/arrange, so this catches a panel that swallowed the viewport.
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_lays_the_source_panel_out_above_the_operation_panels()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("daminion", localPath: "")) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var panel = window.GetVisualDescendants().OfType<DatasourceSourcePanel>().First();
            var card = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Command == vm.StartTaggingRouteCommand && b.Classes.Contains("routeCard"));

            Assert.True(panel.Bounds.Height > 0, "the source panel did not lay out");
            Assert.True(card.Bounds.Width > 0 && card.Bounds.Height > 0, "the operation panels did not lay out");

            var panelTop = panel.TranslatePoint(new Point(0, 0), window)!.Value;
            var cardTop = card.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(cardTop.Y >= panelTop.Y + panel.Bounds.Height,
                $"the panels overlap the source panel (panel ends at {panelTop.Y + panel.Bounds.Height}, card starts at {cardTop.Y})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Daminion connect form is on the dashboard and only there — inside its
    /// Settings panel, not on an operation panel — and Step 1 reports the source
    /// instead of asking for it a second time.
    /// </summary>
    [AvaloniaFact]
    public void Daminion_connect_form_lives_on_the_dashboard_Settings_panel_only()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("daminion", localPath: "")) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var connect = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Command == vm.Wizard.Step1.ConnectCommand);

            Assert.True(vm.IsHomeVisible);          // the dashboard is what is on top
            Assert.False(vm.IsWizardVisible);
            Assert.True(EffectivelyVisible(connect));

            var panel = connect.GetVisualAncestors().OfType<Control>()
                .First(c => c.Classes.Contains("dashboardPanel"));
            Assert.Equal("SettingsPanel", panel.Name);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Home_returns_to_the_dashboard_and_the_route_resumes_where_it_was()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartDedupRouteCommand.Execute(null);
        vm.Wizard.NextCommand.Execute(null);   // datasource → dedup
        Assert.Same(vm.DedupOperation, vm.Shell.Current);

        vm.GoHomeCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.HomeRoute, vm.Route);
        Assert.True(vm.IsHomeVisible);
        Assert.False(vm.IsWizardVisible);
        Assert.Null(vm.Shell.Current);          // D-03: the dashboard means nothing is open

        // Picking the other route later starts it from the Datasource step.
        vm.StartTaggingRouteCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.TaggingRoute, vm.Route);
        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.True(vm.Wizard.ShowTaggingTabs);
        Assert.Same(vm.TagOperation, vm.Shell.Current);
    }

    /// <summary>Effective visibility: a control whose own flag is on is still
    /// hidden while the nav bar (its ancestor) is collapsed on the dashboard.</summary>
    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    private static bool IsTabVisible(Window window, string content)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content is string text && text == content);
        return button is not null && EffectivelyVisible(button);
    }
}
