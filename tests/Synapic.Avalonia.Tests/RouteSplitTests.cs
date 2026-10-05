using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.ViewModels;
using Synapic.Avalonia.Views;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The entry-point split: Synapic opens on a start screen with two routes —
/// Tagging (the four-step wizard) and Deduplication (Datasource → Dedup) —
/// instead of making everyone walk the wizard to reach the dedup step.
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

    [AvaloniaFact]
    public void App_opens_on_the_chooser_with_no_route_selected()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        Assert.Equal(MainWindowViewModel.HomeRoute, vm.Route);
        Assert.True(vm.IsHomeVisible);
        Assert.False(vm.IsWizardVisible);
        Assert.False(vm.IsTaggingRoute);
        Assert.False(vm.IsDedupRoute);
        Assert.Equal("", vm.RouteTitle);
    }

    [AvaloniaFact]
    public void Tagging_route_starts_the_four_step_wizard()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartTaggingRouteCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.TaggingRoute, vm.Route);
        Assert.True(vm.IsWizardVisible);
        Assert.False(vm.IsHomeVisible);
        Assert.Equal("Tagging", vm.RouteTitle);
        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.True(vm.Wizard.ShowTaggingTabs);
        Assert.Equal("Next: Engine \u2192", vm.Wizard.NextButtonText);
        // Deduplication stays a tail-end step of the tagging wizard.
        Assert.False(vm.Wizard.CanGoToDedupTab);
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
        Assert.Equal(folder, vm.Wizard.Dedup.FolderPath);   // Step 1's folder carries over
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
    /// The real compiled XAML: the start-screen cards must carry their commands,
    /// and switching routes must show/hide the nav bar and the tagging tabs.
    /// A mistyped binding path is silent in Avalonia, so this asserts the wired
    /// state instead of only the view model.
    /// </summary>
    [AvaloniaFact]
    public void Start_screen_cards_and_route_visibility_are_bound_in_the_real_window()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("local", Path.GetTempPath())) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            var tagCard = buttons.Single(b => b.Command == vm.StartTaggingRouteCommand);
            var dedupCard = buttons.Single(b => b.Command == vm.StartDedupRouteCommand);
            var homeButton = buttons.Single(b => b.Command == vm.GoHomeCommand);
            Assert.True(tagCard.IsVisible);
            Assert.True(dedupCard.IsVisible);

            // Hidden on the start screen, shown once a route is active.
            Assert.False(EffectivelyVisible(homeButton));
            Assert.False(IsTabVisible(window, "2 · Engine"));

            vm.StartDedupRouteCommand.Execute(null);
            Assert.True(EffectivelyVisible(homeButton));
            Assert.False(IsTabVisible(window, "2 · Engine"));      // dedup route: no tagging steps
            Assert.False(IsTabVisible(window, "3 · Process"));
            Assert.False(IsTabVisible(window, "4 · Results"));
            Assert.True(IsTabVisible(window, "🧹 Deduplication"));
            // The route reads as Datasource → Deduplication, so step 1 is on screen too.
            Assert.True(IsTabVisible(window, "1 · Datasource"));

            vm.StartTaggingRouteCommand.Execute(null);
            Assert.True(IsTabVisible(window, "2 · Engine"));
            Assert.True(IsTabVisible(window, "3 · Process"));
            Assert.True(IsTabVisible(window, "4 · Results"));
            Assert.True(IsTabVisible(window, "🧹 Deduplication"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Effective visibility: a control whose own flag is on is still
    /// hidden while the nav bar (its ancestor) is collapsed on the start screen.</summary>
    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    private static bool IsTabVisible(Window window, string content)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content is string text && text == content);
        return button is not null && EffectivelyVisible(button);
    }

    [AvaloniaFact]
    public void Home_returns_to_the_chooser_and_the_route_resumes_where_it_was()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartDedupRouteCommand.Execute(null);
        vm.Wizard.NextCommand.Execute(null);   // datasource → dedup
        vm.GoHomeCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.HomeRoute, vm.Route);
        Assert.True(vm.IsHomeVisible);
        Assert.False(vm.IsWizardVisible);

        // Picking the other route later starts it from the Datasource step.
        vm.StartTaggingRouteCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.TaggingRoute, vm.Route);
        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.True(vm.Wizard.ShowTaggingTabs);
    }
}
