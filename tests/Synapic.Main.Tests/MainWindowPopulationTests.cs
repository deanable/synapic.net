using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Synapic.Avalonia.Views;
using Synapic.Avalonia.Views.Wizard;
using Xunit;

namespace Synapic.Avalonia.Tests;

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
    public void Wizard_navigates_and_all_step_views_populate()
    {
        var wizard = new WizardViewModel(new Session(), new InferenceSidecarService());

        // Every step view must populate its compiled XAML without throwing.
        var step1 = new Step1Datasource { DataContext = wizard.Step1 };
        var step2 = new Step2Engine { DataContext = wizard.Step2 };
        var step3 = new Step3Process { DataContext = wizard.Step3 };
        var step4 = new Step4Results { DataContext = wizard.Step4 };
        var dedup = new StepDedup { DataContext = wizard.Dedup };

        Assert.Same(wizard.Step1, step1.DataContext);
        Assert.Same(wizard.Step1, wizard.CurrentStep);

        wizard.StartOverCommand.Execute(null);
        Assert.Same(wizard.Step1, wizard.CurrentStep);
    }
}
