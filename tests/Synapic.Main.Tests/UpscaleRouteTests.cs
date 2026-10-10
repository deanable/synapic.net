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
/// The third processing view: the sidebar's ✨ Upscaling row — the Daminion
/// "Feature enhancement" utility. It opens the shared processing view on
/// upscaling's own content (the run surface and its output, over the shared
/// source); the tagging steps and the dedup tab it used to gate are gone with
/// the step chain, and its configuration lives on the Settings view.
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

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    [AvaloniaFact]
    public void Upscale_route_opens_the_processing_view_on_the_upscale_operation()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartUpscaleRouteCommand.Execute(null);

        Assert.True(vm.IsOperationVisible);
        Assert.False(vm.IsSettingsVisible);
        Assert.Equal("Upscaling", vm.OperationTitle);
        Assert.Same(vm.UpscaleOperation, vm.Shell.Current);

        // Upscaling's run is the shared template's run slot, and it has no report.
        var host = vm.Operations;
        Assert.Same(host.Upscale, host.RunFor("upscale"));
        Assert.Null(host.ReportFor("upscale"));   // upscaling reports through its progress
        Assert.NotSame(host.TagRun, host.RunFor("upscale"));
        Assert.NotSame(host.Dedup, host.RunFor("upscale"));
    }

    [AvaloniaFact]
    public void Upscale_mode_reads_the_shared_source_and_is_ready_to_run()
    {
        var folder = Path.GetTempPath();
        var vm = Shell(SourceSession("local", folder));

        vm.StartUpscaleRouteCommand.Execute(null);

        Assert.True(vm.Operations.Upscale.SourceReady);
        Assert.Contains(folder, vm.Operations.Upscale.SourceSummary, StringComparison.Ordinal);

        // With a source and a sidecar there is no gate left.
        Assert.True(vm.UpscaleOperation.IsRunEnabled);
        Assert.Null(vm.UpscaleOperation.RunDisabledReason);
    }

    [AvaloniaFact]
    public void Upscale_run_is_gated_without_a_source_and_says_why()
    {
        var vm = Shell(SourceSession("local", localPath: ""));

        // Entering the view without a source is free (D6); the gate is the run's.
        vm.StartUpscaleRouteCommand.Execute(null);
        Assert.True(vm.IsOperationVisible);

        Assert.False(vm.Operations.Upscale.SourceReady);
        Assert.False(vm.UpscaleOperation.IsRunEnabled);
        Assert.NotNull(vm.UpscaleOperation.RunDisabledReason);
    }

    /// <summary>
    /// The real compiled XAML: the sidebar's Upscaling row must carry its command,
    /// and entering the view must hand the template the upscale content — not the
    /// previous view's. A mistyped binding path is silent in Avalonia, so this
    /// asserts the wired state instead of only the view model.
    /// </summary>
    [AvaloniaFact]
    public void Sidebar_upscale_row_opens_the_view_and_the_template_wires_its_slots()
    {
        var window = new MainWindow { DataContext = Shell(SourceSession("local", Path.GetTempPath())) };
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            var upscaleRow = buttons.Single(b =>
                b.Command == vm.StartUpscaleRouteCommand && b.Classes.Contains("navItem"));
            Assert.True(EffectivelyVisible(upscaleRow));

            upscaleRow.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // The template's content slots are the upscaling view model, and no
            // tagging form is on screen: it belongs to the Settings view.
            var layout = Assert.Single(window.GetVisualDescendants().OfType<OperationLayout>());
            Assert.Same(vm.Operations.Upscale, layout.Run);
            Assert.Null(layout.Report);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Step2Engine>(), EffectivelyVisible);

            // Switching view swaps the same template's content rather than stacking
            // a second view: dedup's run is dedup's.
            vm.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(vm.Operations.Dedup, layout.Run);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_row_returns_from_upscale_and_the_mode_reopens_with_its_state()
    {
        var vm = Shell(SourceSession("local", Path.GetTempPath()));

        vm.StartUpscaleRouteCommand.Execute(null);
        vm.Operations.Upscale.SelectedWorkflow = 2;   // fast
        vm.GoHomeCommand.Execute(null);

        Assert.True(vm.IsSettingsVisible);
        Assert.Null(vm.Shell.Current);

        vm.StartUpscaleRouteCommand.Execute(null);
        Assert.Same(vm.UpscaleOperation, vm.Shell.Current);
        Assert.Equal("Upscaling", vm.OperationTitle);

        // The view reopens with its own settings intact (resume, not reset) and the
        // shared source still ready for its run.
        Assert.Equal(2, vm.Operations.Upscale.SelectedWorkflow);
        Assert.True(vm.Operations.Upscale.SourceReady);
    }
}
