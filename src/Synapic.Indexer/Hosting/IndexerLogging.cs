using System.Diagnostics;
using Serilog;
using Serilog.Events;
using Synapic.Core.Services;

namespace Synapic.Indexer.Hosting;

/// <summary>
/// The service's one logging pipeline (D-11-11, spec §8.1): a daily rolling file
/// under <c>%ProgramData%\Synapic\Logs</c> with 30 files retained, plus the
/// Application Event Log at Warning and above when its source exists.
///
/// <see cref="SynapicLog"/> is initialized with <c>includeDefaultFileSink: false</c>
/// so the engine services write into this same file — two log files for one
/// process would split exactly the trail a support case needs.
/// </summary>
public static class IndexerLogging
{
    public const string EventLogSource = "SynapicIndexer";
    public const string FileNameTemplate = "indexer-.log";
    public const int RetainedFileCountLimit = 30;

    public sealed record Result(bool EventLogSinkActive);

    public static Result Configure(IndexerPaths paths, string minimumLevel)
    {
        Directory.CreateDirectory(paths.LogsDirectory);

        var hasEventLogSource = EventLogSourceExists();

        // service.logLevel is the service's own setting: it filters the file.
        // (In the desktop app the equivalent level filters only the UI pane,
        // because a user's support log must keep Debug detail; here the log IS
        // the support trail and the administrator asked for this level.)
        var level = ParseLevel(minimumLevel);

        SynapicLog.Initialize(
            paths.LogsDirectory,
            minimumLevel,
            additionalSinks: configuration =>
            {
                configuration.WriteTo.File(
                    Path.Combine(paths.LogsDirectory, FileNameTemplate),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetainedFileCountLimit,
                    restrictedToMinimumLevel: level,
                    shared: false,
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

                if (hasEventLogSource)
                {
                    configuration.WriteTo.EventLog(
                        EventLogSource,
                        manageEventSource: false,
                        restrictedToMinimumLevel: LogEventLevel.Warning);
                }
            },
            includeDefaultFileSink: false);

        return new Result(hasEventLogSource);
    }

    /// <summary>
    /// Whether the installer already registered the source. Reading the registry
    /// key needs no elevation, but the framework walks every log to find it and
    /// throws <see cref="System.Security.SecurityException"/> for the logs an
    /// ordinary account cannot read (Security, State). "Cannot tell" is treated
    /// as "not registered": the file sink always works, and a missing Event Log
    /// sink is a warning rather than a startup failure.
    /// </summary>
    public static bool EventLogSourceExists()
    {
        try
        {
            return EventLog.SourceExists(EventLogSource);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    private static LogEventLevel ParseLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
