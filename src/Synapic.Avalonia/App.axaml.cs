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
        var configService = new ConfigService();
        var config = configService.Load();
        SynapicLog.Initialize(minimumLevel: config.Ui.LogLevel);
        var log = SynapicLog.For(nameof(App));

        log.Information("=== Synapic startup ===");
        log.Information("App version {Version}, app dir {AppDir}",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "?", AppContext.BaseDirectory);
        log.Information("Log file (overwritten per run): {LogFilePath}", SynapicLog.LogFilePath);
        log.Information("Config file: {ConfigPath} (autoLaunchSidecar={AutoLaunch}, logLevel={LogLevel})",
            configService.FilePath, config.Ui.AutoLaunchSidecar, config.Ui.LogLevel);
        log.Information("Sidecar executable detected: {SidecarExe}", InferenceSidecarService.FindExecutable() ?? "<not found>");
        log.Information("Models root (HF_HOME for the sidecar): {ModelsRoot}", InferenceSidecarService.ModelsRoot());

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<Session>();
        services.AddSingleton<IInferenceSidecar, InferenceSidecarService>();
        services.AddSingleton<ISidecarBuildService, SidecarBuildService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            // Attach the view model here: the window's XAML declares no
            // DataContext, and command-less buttons render disabled without it.
            mainWindow.DataContext = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = mainWindow;

            // Detect the sidecar executable so the Start/Stop buttons and the
            // status indicator are correct at launch. This never adopts a
            // server left over from a previous run: the lifecycle is strictly
            // one server per app instance (started on launch, stopped on exit).
            _ = ((MainWindowViewModel)mainWindow.DataContext).DetectServerAsync();

            // Server lifecycle (user requirement): the inference server starts
            // when the application launches. ui.autoLaunchSidecar (default
            // true) opts out only for manual-only use.
            if (config.Ui.AutoLaunchSidecar)
            {
                log.Information("Auto-launching the inference server (ui.autoLaunchSidecar=true)");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var sidecar = Services.GetRequiredService<IInferenceSidecar>();
                        await sidecar.StartAsync();
                    }
                    catch (Exception e)
                    {
                        SynapicLog.Error(nameof(App), "Auto-launch of sidecar failed", e);
                    }
                });
            }
            else
            {
                log.Information("Auto-launch disabled by config - waiting for the user to press Start Server");
            }

            // Orphan-free shutdown (spec §2): always stop the sidecar on exit.
            // The sidecar is also in a kill-on-close job object, so even a
            // crash or task-manager kill of this app takes the server down.
            var shutdownHandled = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (shutdownHandled) return; // Shutdown() below re-raises this event
                shutdownHandled = true;
                e.Cancel = true; // finish shutdown after cleanup
                log.Information("App shutdown requested - cancelling build (if any) and stopping the sidecar");
                Services.GetService<ISidecarBuildService>()?.Cancel();
                try
                {
                    var sidecar = Services.GetService<IInferenceSidecar>();
                    if (sidecar is not null)
                        await sidecar.StopAsync();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Stopping the sidecar during shutdown failed");
                }
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
