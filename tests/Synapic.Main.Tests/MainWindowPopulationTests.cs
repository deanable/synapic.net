using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Regression tests for the startup NullReferenceException inside
/// MainWindow.!XamlIlPopulate: compiled XAML populate runs in the MainWindow
/// constructor and crashed on the BrushTransition-in-SolidColorBrush block
/// (MainWindow.axaml line 29). These tests construct the full view tree
/// headlessly so any populate-time crash fails the suite instead of the app.
/// </summary>
public class MainWindowPopulationTests
{
    [AvaloniaFact]
    public void MainWindow_xaml_populates_without_exception()
    {
        var window = new MainWindow();

        Assert.NotNull(window.Content);
    }

    [AvaloniaFact]
    public async Task MainWindow_datacontext_attaches_and_status_dot_binds()
    {
        var window = new MainWindow();
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(),
            () => null, null, null, _ => null);
        window.DataContext = vm;

        Assert.NotNull(vm.StartServerCommand);
        Assert.Equal(ServerUiState.Detecting, vm.ServerState);

        await vm.DetectServerAsync();

        Assert.Equal("Server not detected", vm.StatusText);
        Assert.Equal(Colors.Black, ((ISolidColorBrush)vm.ServerBrush).Color);
        Assert.True(vm.IsBuildButtonVisible);
        Assert.False(vm.IsWorkspaceEnabled);
    }

    [AvaloniaFact]
    public void Every_operation_view_populates_itself_from_the_host_content()
    {
        var host = new OperationShellViewModel(new Session(), new InferenceSidecarService());

        // Every mode view must populate its compiled XAML without throwing, and
        // each renders off the host's own view model — there is no step chain to
        // walk any more, so the content is whatever the host hands the template.
        var source = new DatasourceSourcePanel { DataContext = host.Source };
        var scope = new ScopeSelectionPanel { DataContext = host.Source };
        var parameters = new Step2Engine { DataContext = host.TagParameters };
        var run = new RunStateBar { DataContext = host.TagRun };
        var report = new Step4Results { DataContext = host.TagReport };
        var dedup = new StepDedup { DataContext = host.Dedup };
        var upscale = new StepUpscale { DataContext = host.Upscale };

        Assert.Same(host.Source, source.DataContext);
        Assert.Same(host.Source, scope.DataContext);
        Assert.Same(host.TagParameters, parameters.DataContext);
        Assert.Same(host.TagRun, run.DataContext);
        Assert.Same(host.TagReport, report.DataContext);
        Assert.Same(host.Dedup, dedup.DataContext);
        Assert.Same(host.Upscale, upscale.DataContext);

        // And the region lookups the template binds hand out those same
        // instances: Region A is shared, B/C follow the open mode.
        Assert.Same(host.TagParameters, host.ParametersFor(OperationShellViewModel.TagKey));
        Assert.Same(host.TagRun, host.RunFor(OperationShellViewModel.TagKey));
        Assert.Same(host.TagReport, host.ReportFor(OperationShellViewModel.TagKey));
        Assert.Same(host.Dedup, host.ParametersFor(OperationShellViewModel.DedupKey));
        Assert.Same(host.Dedup, host.RunFor(OperationShellViewModel.DedupKey));
        Assert.Null(host.ReportFor(OperationShellViewModel.DedupKey));
    }
}
