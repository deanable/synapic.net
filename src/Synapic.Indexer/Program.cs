using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synapic.Core.Services;
using Synapic.Indexer.Commands;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Hosting;
using Synapic.Indexer.Install;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;

namespace Synapic.Indexer;

/// <summary>
/// Process entry point. Arguments select a control mode (05-04); no arguments is
/// the service start the SCM performs. Configuration is read and validated before
/// any host is built, so a bad value can never reach a running service (D-11-10).
/// </summary>
internal static class Program
{
    private const int ExitHostFailed = 1;
    private const int ExitInvalidConfig = 2;
    private const int ExitPathsUnavailable = 3;

    private static async Task<int> Main(string[] args)
    {
        if (!ServiceCommandLine.TryParse(args, out var invocation, out var parseError))
        {
            if (parseError is not null) Console.Error.WriteLine(parseError);
            Console.Error.WriteLine(ServiceCommandLine.Usage);
            return parseError is null ? 0 : ServiceCommandLine.UsageExitCode;
        }

        var console = invocation.Mode == ServiceMode.Console;

        var paths = IndexerPaths.Default;

        // Status runs before anything is created: no directories, no log file, no
        // host, no scan. A read-only command that writes is not read-only.
        if (invocation.Mode == ServiceMode.Status)
            return StatusCommand.Run(paths, Console.Out, Console.Error, invocation.FailedOnly);

        try
        {
            paths.EnsureCreated();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Synapic Indexer cannot use {paths.Root}: {e.Message}");
            return ExitPathsUnavailable;
        }

        var store = new IndexerConfigStore(configFilePathOverride: paths.ConfigFilePath);
        var load = store.Load();
        var errors = load.Errors.Concat(IndexerConfigValidator.Validate(load.Config)).ToList();

        var logging = IndexerLogging.Configure(paths, IsKnownLevel(load.Config.Service.LogLevel)
            ? load.Config.Service.LogLevel
            : "Information");
        if (!logging.EventLogSinkActive)
        {
            SynapicLog.Warning(nameof(IndexerLogging),
                $"The '{IndexerLogging.EventLogSource}' Event Log source does not exist; logging to the file only. " +
                "Run Synapic.Indexer.exe --install to create it.");
        }

        foreach (var warning in load.Warnings)
            SynapicLog.Warning("IndexerConfig", $"Ignored setting - {warning}");

        // Control modes run before validation: --set-daminion-password is what a
        // Daminion config is waiting for, and --uninstall must work even when the
        // configuration is broken.
        switch (invocation.Mode)
        {
            case ServiceMode.SetDaminionPassword:
                return PasswordCommand.Run(store, PasswordCommand.ReadWithoutEcho, Console.Out);

            case ServiceMode.Install:
                return await new ServiceInstaller()
                    .InstallAsync(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0], paths)
                    .ConfigureAwait(false);

            case ServiceMode.Uninstall:
                return await new ServiceInstaller().UninstallAsync(paths).ConfigureAwait(false);
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"Synapic Indexer configuration error - {error}");
                SynapicLog.Error("IndexerConfig", $"Configuration error - {error}");
            }
            SynapicLog.Error("IndexerConfig",
                $"Startup aborted: {errors.Count} configuration error(s); nothing was started.");
            Serilog.Log.CloseAndFlush();
            return ExitInvalidConfig;
        }

        SynapicLog.Info("IndexerConfig",
            load.Sources.Count == 0
                ? "No configuration source found; using the built-in defaults."
                : $"Configuration read from {string.Join(", ", load.Sources)}.");
        SynapicLog.Info("Program", $"Starting Synapic Indexer v{typeof(Program).Assembly.GetName().Version} " +
                                  $"({(console ? "console" : "Windows service")} mode).");

        // The queue exists before the host does: the first scan must find a
        // schema, and recovery must run before any scan claims work (SCAN-06).
        var queue = new IndexingQueueRepository(paths.QueueFilePath);
        try
        {
            await queue.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            SynapicLog.Error("IndexerQueue",
                $"The job queue at {paths.QueueFilePath} could not be opened: {e.GetType().Name}: {e.Message}");
            Serilog.Log.CloseAndFlush();
            return ExitPathsUnavailable;
        }

        var signal = new WorkSignal();
        var intake = new WorkIntake(queue, signal);
        var recovered = await queue.RecoverInFlightAsync().ConfigureAwait(false);
        if (recovered > 0)
        {
            SynapicLog.Warning("IndexerQueue",
                $"Recovered {recovered} InFlight job(s) left by the previous run; they are Pending again.");
        }

        // Detection sources are built before the host so a configuration that
        // cannot work (a password that will not decrypt) exits with a message
        // instead of starting a service that fails every scan.
        var hasher = new Sha256FileHasher();
        var localSource = load.Config.Schedule.WatchPaths.Any(path => !string.IsNullOrWhiteSpace(path))
            ? new LocalFolderScanSource(load.Config, queue, hasher, intake)
            : null;

        DaminionWorkSource? daminionSource = null;
        if ((load.Config.Datasource.Type ?? "").Trim().Equals("daminion", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                daminionSource = new DaminionWorkSource(load.Config, intake);
            }
            catch (IndexerConfigurationException e)
            {
                Console.Error.WriteLine($"Synapic Indexer configuration error - {e.Message}");
                SynapicLog.Error("IndexerConfig", e.Message);
                Serilog.Log.CloseAndFlush();
                return ExitInvalidConfig;
            }
        }

        var sources = new List<IWorkSource>();
        if (ScanScheduler.ModeIncludesInterval(load.Config.Schedule.Mode))
        {
            if (localSource is not null) sources.Add(localSource);
            if (daminionSource is not null) sources.Add(daminionSource);
        }

        var writer = new MetadataWriterService();
        var tagFields = TagRequestFactory.Fields(load.Config.Tagging);
        var writePolicy = new PreserveExistingTagsPolicy(
            writer, tagFields, load.Config.Tagging.OverwriteExistingTags);
        Func<Synapic.Core.Services.Processing.DatasourceSelection>? daminionSelection =
            daminionSource is null ? null : () => DaminionSelectionBuilder.Build(load.Config);

        var scheduler = sources.Count > 0 ? new ScanScheduler(sources, load.Config) : null;
        var watcher = FolderWatcher.ModeIncludesWatch(load.Config.Schedule.Mode) && localSource is not null
            ? new FolderWatcher(load.Config, intake, new FileStabilityProbe(), hasher, queue,
                onOverflow: localSource.ScanAsync)
            : null;

        if (scheduler is null && watcher is null)
        {
            SynapicLog.Info("Program",
                "No work source is configured; the service will monitor the sidecar but find no work.");
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Service lifetime first, then clear every Microsoft logging provider:
        // AddWindowsService registers one of its own (Event Log at Information),
        // and Serilog is this process's single pipeline (D-11-11).
        if (!console)
            builder.Services.AddWindowsService(options => options.ServiceName = "SynapicIndexer");
        builder.Logging.ClearProviders();

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton(load.Config);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(queue);
        builder.Services.AddSingleton<IWorkIntake>(intake);
        builder.Services.AddSingleton<IWorkSignal>(signal);

        // The executor is built by DI so it receives the same supervisor the
        // worker owns; a second instance would restart a second sidecar.
        builder.Services.AddSingleton(sp => new BatchExecutor(
            sp.GetRequiredService<IInferenceSidecar>(),
            sp.GetRequiredService<ISidecarSupervisor>(),
            queue,
            load.Config,
            writer,
            hasher,
            writePolicy,
            daminionSelection: daminionSelection));

        // The sidecar: located beside the service executable (or the dev
        // checkout's artifacts), launched for the configured device, supervised
        // for readiness, idle stop and shutdown (D-11-13, D-11-14).
        var executable = SidecarExecutable.Locate();
        builder.Services.AddSingleton(executable);
        builder.Services.AddSingleton(new SidecarLaunchOptions
        {
            DeviceProvider = new StaticSidecarDeviceProvider(load.Config.Engine.Device),
            ExecutablePathOverride = executable.Path,
            // The service ships its sidecar and has no setup panel: refusing to
            // start because a dev checkout's source is newer would leave it idle
            // with no action a service operator could take.
            EnforceSourceStaleness = false,
        });
        builder.Services.AddSingleton<IInferenceSidecar, InferenceSidecarService>();
        builder.Services.AddSingleton<ISidecarActivity>(new QueueSidecarActivity(queue));
        builder.Services.AddSingleton<ISidecarSupervisor>(sp => new SidecarSupervisor(
            sp.GetRequiredService<IInferenceSidecar>(),
            sp.GetRequiredService<IndexerConfig>(),
            sp.GetRequiredService<IndexerPaths>(),
            sp.GetRequiredService<ISidecarActivity>(),
            sp.GetRequiredService<SidecarExecutable>()));

        // Order matters: the worker owns the sidecar's lifetime, detection runs
        // after it, and shutdown is reverse registration order (detection first,
        // then the sidecar).
        builder.Services.AddHostedService(sp => new IndexerWorkerService(
            load.Config,
            sp.GetRequiredService<ISidecarSupervisor>(),
            sp.GetRequiredService<BatchExecutor>(),
            sp.GetRequiredService<IWorkSignal>()));
        if (scheduler is not null) builder.Services.AddHostedService(_ => scheduler);
        if (watcher is not null) builder.Services.AddHostedService(_ => watcher);

        try
        {
            await builder.Build().RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception e)
        {
            SynapicLog.Error("Program", "The indexer host failed.", e);
            return ExitHostFailed;
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    private static bool IsKnownLevel(string level) =>
        level.Trim().ToLowerInvariant() is "verbose" or "debug" or "information" or "warning" or "error" or "fatal";
}
