using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

        // Crash reporting (P6.1, local-only): install before anything else so
        // even early-startup failures are captured. Reports land in the log
        // directory's crashes/ folder; nothing is ever sent over the network.
        var crashReporter = new CrashReporterService();
        crashReporter.Install();
        crashReporter.CrashCaptured += report =>
        {
            if (!report.IsTerminal)
                Dispatcher.UIThread.Post(() => Views.CrashDialogWindow.Show(report));
        };

        // .NET 10 Desktop Runtime self-heal: when the targeted runtime is
        // missing, silently download and install it, then continue startup.
        // Fire-and-forget: it must never block or crash the app launch, and
        // every step logs its outcome for diagnosis.
        _ = new DotNetRuntimeCheckService().EnsureRuntimeAsync(
            line => log.Information("[runtime] {RuntimeCheck}", line));

        log.Information("=== Synapic startup ===");
        log.Information("App version {Version}, app dir {AppDir}",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "?", AppContext.BaseDirectory);
        log.Information("Log file: {LogFilePath} (previous runs kept in {ArchivesDirectory})",
            SynapicLog.LogFilePath, SynapicLog.ArchivesDirectory);
        log.Information("Config file: {ConfigPath} (autoLaunchSidecar={AutoLaunch}, logLevel={LogLevel})",
            configService.FilePath, config.Ui.AutoLaunchSidecar, config.Ui.LogLevel);
        log.Information("Sidecar executable detected: {SidecarExe}", InferenceSidecarService.FindExecutable() ?? "<not found>");
        log.Information("Models root (HF_HOME for the sidecar): {ModelsRoot}", InferenceSidecarService.ModelsRoot());
        if (OperatingSystem.IsWindows())
            log.Information("Daminion connection params persist in registry: HKCU\\Software\\Synapic\\Daminion (password DPAPI-protected)");

        // Usage telemetry (P6.1, spec §11 Q5): strictly opt-in via
        // ui.telemetryEnabled, local counters only — no network, ever.
        TelemetryService.Shared = new TelemetryService(
            enabled: config.Ui.TelemetryEnabled,
            filePath: Path.Combine(SynapicLog.LogDirectory, "synapic-usage.json"),
            appVersion: typeof(App).Assembly.GetName().Version?.ToString(3),
            osFamily: OperatingSystem.IsWindows() ? "Windows"
                : OperatingSystem.IsMacOS() ? "macOS" : "Linux");
        TelemetryService.Shared.RecordAppLaunch();

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<Session>();
        services.AddSingleton(new DaminionConnectionStore());
        services.AddSingleton(new EngineSettingsStore());
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
            if (!config.Ui.AutoLaunchSidecar)
            {
                log.Information("Auto-launch disabled by config - waiting for the user to press Start Server");
            }
            else if (InferenceSidecarService.FindExecutable() is null)
            {
                // Nothing to launch yet: the setup panel offers the CPU/CUDA
                // builds and the workspace stays disabled until one exists.
                log.Information("Not auto-launching: no sidecar is built yet - build one from the setup panel");
            }
            else
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
