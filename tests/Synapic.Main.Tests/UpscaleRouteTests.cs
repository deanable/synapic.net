using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The third route: the start screen's ✨ Upscaling card — the Daminion
/// "Feature enhancement" utility. Datasource → Upscaling, with the tagging
/// steps and the dedup tab gated off while the route owns the wizard (and
/// vice versa on the dedup route).
/// </summary>
public class UpscaleRouteTests
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
    public void Upscale_route_opens_on_Datasource_with_the_tagging_and_dedup_tabs_gated()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartUpscaleRouteCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.UpscaleRoute, vm.Route);
        Assert.True(vm.IsUpscaleRoute);
        Assert.False(vm.IsDedupRoute);
        Assert.False(vm.IsTaggingRoute);
        Assert.Equal("Upscaling", vm.RouteTitle);
        Assert.Equal(0, vm.Wizard.CurrentStepIndex);

        var w = vm.Wizard;
        Assert.False(w.ShowTaggingTabs);          // Engine/Process/Results hidden
        Assert.True(w.ShowDatasourceTab);         // the route reads Datasource → Upscaling
        Assert.False(w.CanGoToStep2Tab);
        Assert.False(w.ShowDedupTab);             // dedup yields while upscale owns the wizard
        Assert.True(w.ShowUpscaleTab);
        Assert.True(w.CanGoToUpscaleTab);         // reachable straight from Step 1
        Assert.False(w.CanGoToDedupTab);
        Assert.Equal("Next: Upscaling \u2192", w.NextButtonText);
    }

    [AvaloniaFact]
    public async Task Upscale_route_Next_skips_the_wizard_and_lands_on_the_upscale_step()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartUpscaleRouteCommand.Execute(null);
        await vm.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Same(vm.Wizard.Upscale, vm.Wizard.CurrentStep);
        Assert.Equal(5, vm.Wizard.CurrentStepIndex);
        Assert.Equal("Upscaling", vm.Wizard.CurrentStepTitle);
        Assert.Equal("Start Over", vm.Wizard.NextButtonText);
        Assert.Null(vm.Wizard.ValidationError);

        // The step reads its source straight off Step 1 — no prefill needed.
        Assert.True(vm.Wizard.Upscale.SourceReady);
        Assert.Contains(folder, vm.Wizard.Upscale.SourceSummary, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Upscale_route_refuses_to_leave_Datasource_without_a_source()
    {
        var vm = Shell(SourceSession("local", localPath: ""));

        vm.StartUpscaleRouteCommand.Execute(null);
        await vm.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.NotNull(vm.Wizard.ValidationError);
        Assert.Contains("folder", vm.Wizard.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The real compiled XAML: the third card must carry its command, and the
    /// Upscaling tab must appear/disappear with the route that owns the wizard.
    /// A mistyped binding path is silent in Avalonia, so this asserts the wired
    /// state instead of only the view model.
    /// </summary>
    [AvaloniaFact]
    public void Third_card_and_upscaling_tab_are_bound_in_the_real_window()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("local", Path.GetTempPath())) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            // The start-screen card specifically: the sidebar's mode header is
            // bound to the same command on purpose.
            var upscaleCard = buttons.Single(b => b.Command == vm.StartUpscaleRouteCommand && b.Classes.Contains("routeCard"));
            Assert.True(EffectivelyVisible(upscaleCard));

            // Start screen: the nav bar (and its tabs) is collapsed.
            Assert.False(IsTabVisible(window, "✨ Upscaling"));

            vm.StartDedupRouteCommand.Execute(null);
            Assert.True(IsTabVisible(window, "🧹 Deduplication"));
            Assert.False(IsTabVisible(window, "✨ Upscaling"));   // dedup owns the wizard

            vm.StartUpscaleRouteCommand.Execute(null);
            Assert.True(IsTabVisible(window, "✨ Upscaling"));
            Assert.False(IsTabVisible(window, "🧹 Deduplication")); // upscale owns the wizard
            Assert.False(IsTabVisible(window, "2 · Engine"));

            vm.StartTaggingRouteCommand.Execute(null);
            Assert.True(IsTabVisible(window, "✨ Upscaling"));     // reachable from Results
            Assert.True(IsTabVisible(window, "🧹 Deduplication"));
            Assert.True(IsTabVisible(window, "2 · Engine"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Home_returns_to_the_chooser_and_the_upscale_route_can_be_reentered()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartUpscaleRouteCommand.Execute(null);
        vm.GoHomeCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.HomeRoute, vm.Route);
        Assert.True(vm.IsHomeVisible);

        vm.StartUpscaleRouteCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.UpscaleRoute, vm.Route);
        Assert.Equal(0, vm.Wizard.CurrentStepIndex);
        Assert.True(vm.Wizard.ShowUpscaleTab);
        Assert.False(vm.Wizard.ShowTaggingTabs);
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
}
