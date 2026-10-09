using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The third mode: the dashboard's ✨ Upscaling card — the Daminion "Feature
/// enhancement" utility. It opens the one operation template on upscaling's own
/// content (Parameters and Output both the upscale view model, Region A the one
/// shared source); the tagging steps and the dedup tab it used to gate are gone
/// with the step chain (D5).
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
    public void Upscale_route_opens_the_template_on_the_upscale_mode()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartUpscaleRouteCommand.Execute(null);

        Assert.True(vm.IsOperationVisible);
        Assert.False(vm.IsDashboardVisible);
        Assert.Equal("Upscaling", vm.OperationTitle);
        Assert.Equal("Dashboard / Upscaling", vm.Breadcrumb);
        Assert.Same(vm.UpscaleOperation, vm.Shell.Current);

        // One layout, three modes: upscaling's tunables and its run are the same
        // view model in the template's two content regions, and no tagging or
        // dedup content is reachable from here (D5 — there is no step chain).
        var host = vm.Operations;
        Assert.Same(host.Upscale, host.ParametersFor("upscale"));
        Assert.Same(host.Upscale, host.RunFor("upscale"));
        Assert.Null(host.ReportFor("upscale"));   // upscaling reports through its progress
        Assert.NotSame(host.TagParameters, host.ParametersFor("upscale"));
        Assert.NotSame(host.Dedup, host.ParametersFor("upscale"));
    }

    [AvaloniaFact]
    public void Upscale_mode_reads_the_shared_source_and_is_ready_to_run()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartUpscaleRouteCommand.Execute(null);

        // The mode reads its source straight off the shared source — nothing to
        // prefill and no step to advance through.
        Assert.True(vm.Operations.Upscale.SourceReady);
        Assert.Contains(folder, vm.Operations.Upscale.SourceSummary, StringComparison.Ordinal);

        // And the run is offered: with a source and a sidecar there is no gate left.
        Assert.True(vm.UpscaleOperation.IsRunEnabled);
        Assert.Null(vm.UpscaleOperation.RunDisabledReason);
    }

    [AvaloniaFact]
    public void Upscale_run_is_gated_without_a_source_and_says_why()
    {
        var vm = Shell(SourceSession("local", localPath: ""));

        // Entering the mode without a source is free (D6); the gate is the run's.
        vm.StartUpscaleRouteCommand.Execute(null);
        Assert.True(vm.IsOperationVisible);

        Assert.False(vm.Operations.Upscale.SourceReady);
        Assert.False(vm.UpscaleOperation.IsRunEnabled);
        Assert.NotNull(vm.UpscaleOperation.RunDisabledReason);
    }

    /// <summary>
    /// The real compiled XAML: the third card must carry its command, and entering
    /// the mode must hand the template the upscale content — not the previous
    /// mode's form. A mistyped binding path is silent in Avalonia, so this asserts
    /// the wired state instead of only the view model.
    /// </summary>
    [AvaloniaFact]
    public void Third_card_opens_the_upscale_mode_and_the_template_wires_its_regions()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("local", Path.GetTempPath())) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            // The dashboard's third card is the way into the mode.
            var upscaleCard = buttons.Single(b =>
                b.Command == vm.StartUpscaleRouteCommand && b.Classes.Contains("routeCard"));
            Assert.True(EffectivelyVisible(upscaleCard));

            upscaleCard.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // The template's content regions are the upscaling view model, and no
            // tagging form is on screen: it belongs to the tag mode.
            var layout = Assert.Single(window.GetVisualDescendants().OfType<OperationLayout>());
            Assert.Same(vm.Operations.Upscale, layout.Parameters);
            Assert.Same(vm.Operations.Upscale, layout.Run);
            Assert.Null(layout.Report);
            Assert.Empty(window.GetVisualDescendants().OfType<Step2Engine>());

            // Switching mode swaps the same template's content rather than stacking
            // a second view: dedup's regions are dedup's.
            vm.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(vm.Operations.Dedup, layout.Parameters);
            Assert.Same(vm.Operations.Dedup, layout.Run);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Home_returns_to_the_dashboard_and_the_upscale_mode_reopens_with_its_state()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartUpscaleRouteCommand.Execute(null);
        vm.Operations.Upscale.SelectedWorkflow = 2;   // fast
        vm.GoHomeCommand.Execute(null);

        Assert.True(vm.IsDashboardVisible);
        Assert.Null(vm.Shell.Current);

        vm.StartUpscaleRouteCommand.Execute(null);
        Assert.Same(vm.UpscaleOperation, vm.Shell.Current);
        Assert.Equal("Upscaling", vm.OperationTitle);

        // The mode reopens with its own settings intact (resume, not reset) and the
        // shared source still ready for its run.
        Assert.Equal(2, vm.Operations.Upscale.SelectedWorkflow);
        Assert.True(vm.Operations.Upscale.SourceReady);
    }

    /// <summary>Effective visibility: a control whose own flag is on is still
    /// hidden while the content view it lives in is collapsed.</summary>
    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);
}
