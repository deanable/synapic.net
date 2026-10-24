using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Synapic.ServiceConsole.ViewModels;
using Synapic.ServiceConsole.Views;

namespace Synapic.ServiceConsole;

/// <summary>
/// The service console's bootstrap: one window, one view model, no DI container.
/// Everything heavier (engine, sidecar, tagging) deliberately does not exist
/// here - this application manages the background service and nothing else.
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new IndexerServiceViewModel(
                    new Services.IndexerServiceMonitor(),
                    new Services.IndexerProcessLauncher()),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
