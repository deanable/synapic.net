using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Synapic.Avalonia.Views;

namespace Synapic.Avalonia;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var config = new ConfigService().Load();
        SynapicLog.Initialize(minimumLevel: config.Ui.LogLevel);

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<Session>();
        services.AddSingleton<IInferenceSidecar, InferenceSidecarService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate DataContext from XAML designer previews.
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();

            // Opt-in auto-launch (spec §2 Auto-Launch): start the sidecar
            // transparently when ui.autoLaunchSidecar is enabled.
            if (config.Ui.AutoLaunchSidecar)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var sidecar = Services.GetRequiredService<IInferenceSidecar>();
                        await sidecar.StartAsync();
                    }
                    catch (Exception e)
                    {
                        SynapicLog.Error(nameof(App), $"Auto-launch of sidecar failed: {e.Message}");
                    }
                });
            }

            // Orphan-free shutdown (spec §2): always stop the sidecar on exit.
            desktop.ShutdownRequested += async (_, e) =>
            {
                var sidecar = Services.GetService<IInferenceSidecar>();
                if (sidecar is not null)
                {
                    e.Cancel = true; // finish shutdown after cleanup
                    await sidecar.StopAsync();
                    desktop.Shutdown();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
