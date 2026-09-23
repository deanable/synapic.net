using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Static Serilog bootstrap (spec §5.3 LoggingService): a single live log file
/// in the application directory, plus an in-memory sink the UI log viewer
/// binds to. The live file always starts empty for the current run, while the
/// previous run is rotated into <c>logs/archives</c> (kept for
/// <see cref="MaxArchivedLogs"/> runs) - without that, an exception raised on
/// an earlier launch is unrecoverable by the time you go looking for it.
/// The file captures everything from Debug up (regardless of the
/// UI log level) so server startup failures can be diagnosed afterwards.
/// </summary>
public static class SynapicLog
{
    private const int MaxBufferedEvents = 2000;

    /// <summary>Previous runs kept in <c>logs/archives</c> before pruning.</summary>
    public const int MaxArchivedLogs = 10;

    /// <summary>Directory holding rotated previous-run logs; empty before Initialize.</summary>
    public static string ArchivesDirectory { get; private set; } = "";

    private static readonly object InitLock = new();
    private static bool _initialized;
    private static Logger? _logger;
    private static UiLogSink? _uiSink;

    public static UiLogSink UiSink => _uiSink ?? throw new InvalidOperationException("SynapicLog not initialized");

    public static string LogFilePath { get; private set; } = "";

    /// <summary>Directory holding the log file; empty before Initialize.</summary>
    public static string LogDirectory { get; private set; } = "";

    public static void Initialize(string? logDirectory = null, string minimumLevel = "info")
    {
        lock (InitLock)
        {
            // A caller with no explicit directory (App at startup, a headless
            // test booting the real App) must adopt the logger that is already
            // running rather than point the process at a second file - tests
            // reset the global logger, and re-resolving the default directory
            // underneath them corrupted their log files.
            if (_initialized && logDirectory is null) return;

            var dir = logDirectory ?? ResolveLogDirectory();

            if (_initialized)
            {
                // Same directory again = a new "run" of the same target (how
                // the rotation tests simulate a restart): fall through and
                // rotate. A different directory = deliberate takeover: retire
                // the current logger first so its file handle is released.
                // The UiLogSink is deliberately kept: a headless App booted by
                // another test is subscribed to it, and dropping it would
                // silently detach that app's log view.
                if (string.Equals(dir, LogDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    try { Log.CloseAndFlush(); } catch { /* best effort */ }
                    _logger = null;
                    _initialized = false;
                }
                else
                {
                    return;
                }
            }

            Directory.CreateDirectory(dir);
            LogDirectory = dir;
            LogFilePath = Path.Combine(dir, "synapic.log");
            ArchivesDirectory = Path.Combine(dir, "archives");

            var archiveNote = ArchivePreviousRun();

            _uiSink ??= new UiLogSink(MaxBufferedEvents);
            var uiLevel = ParseLevel(minimumLevel);

            _logger = new LoggerConfiguration()
                // The file always gets full detail; the UI sink is filtered below.
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    LogFilePath,
                    rollingInterval: RollingInterval.Infinite,
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u4}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.Sink(_uiSink, restrictedToMinimumLevel: uiLevel)
                .CreateLogger();

            Log.Logger = _logger;
            _initialized = true;

            For("SynapicLog").Information(
                "Synapic log started - file: {LogFilePath} (live file starts empty each run; previous runs kept under {ArchivesDirectory}){ArchiveNote}",
                LogFilePath, ArchivesDirectory, archiveNote);
        }
    }

    /// <summary>
    /// Log location: next to the app executable (writable in dev checkouts and
    /// portable installs); falls back to %LOCALAPPDATA%/Synapic/logs when the
    /// install directory is read-only (e.g. Program Files).
    /// </summary>
    private static string ResolveLogDirectory()
    {
        var appLogs = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(appLogs);
            var probe = Path.Combine(appLogs, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return appLogs;
        }
        catch (Exception)
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Synapic", "logs");
            Directory.CreateDirectory(local);
            return local;
        }
    }

    /// <summary>
    /// Moves the previous run's log into <c>logs/archives/synapic-&lt;stamp&gt;.log</c>
    /// (stamp = that run's last-write time, so names are chronological) and
    /// prunes everything beyond <see cref="MaxArchivedLogs"/>. Returns a short
    /// note for the startup line, or an empty string when there was nothing to
    /// archive - in which case the run falls back to appending, which is what
    /// happens when the file is locked by a second app instance.
    /// </summary>
    private static string ArchivePreviousRun()
    {
        try
        {
            if (!File.Exists(LogFilePath)) return "";
            if (new FileInfo(LogFilePath).Length == 0)
            {
                File.Delete(LogFilePath);
                return "";
            }

            Directory.CreateDirectory(ArchivesDirectory);
            var stamp = File.GetLastWriteTime(LogFilePath).ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(ArchivesDirectory, $"synapic-{stamp}.log");
            for (var i = 1; File.Exists(target); i++)
                target = Path.Combine(ArchivesDirectory, $"synapic-{stamp}-{i}.log");

            File.Move(LogFilePath, target);
            PruneArchives();
            return $" (previous run archived as {Path.GetFileName(target)})";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static void PruneArchives()
    {
        var archived = Directory.GetFiles(ArchivesDirectory, "synapic-*.log")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < archived.Count - MaxArchivedLogs; i++)
        {
            try { File.Delete(archived[i]); }
            catch (IOException) { /* being read - keep it for the next run */ }
        }
    }

    /// <summary>
    /// Test hook: reset **and** initialize under one lock acquisition. Resetting
    /// and initializing as two separate calls leaves a window in which another
    /// test booting the real <c>App</c> claims the process-global logger first,
    /// sending this test's output to the default directory instead.
    /// </summary>
    public static void RestartForTests(string? logDirectory, string minimumLevel = "info")
    {
        lock (InitLock)
        {
            ResetForTests();
            Initialize(logDirectory, minimumLevel);
        }
    }

    /// <summary>Test hook: flush and dispose the logger so Initialize can run again.</summary>
    public static void ResetForTests()
    {
        lock (InitLock)
        {
            Log.CloseAndFlush();
            _logger = null;
            _initialized = false;
        }
    }

    public static ILogger For(string sourceContext) => (_logger ?? Log.Logger).ForContext("SourceContext", sourceContext);

    public static void Debug(string context, string message) => For(context).Debug(message);
    public static void Info(string context, string message) => For(context).Information(message);
    public static void Warning(string context, string message) => For(context).Warning(message);
    public static void Error(string context, string message) => For(context).Error(message);
    public static void Error(string context, string message, Exception ex) => For(context).Error(ex, message);

    private static LogEventLevel ParseLevel(string level) => level.ToLowerInvariant() switch
    {
        "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}

/// <summary>In-memory ring-buffer log sink for the UI log viewer.</summary>
public sealed class UiLogSink : ILogEventSink
{
    private readonly int _capacity;
    private readonly Queue<UiLogEvent> _events = new();
    private readonly MessageTemplateTextFormatter _formatter =
        new("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

    public UiLogSink(int capacity) => _capacity = capacity;

    public event Action<UiLogEvent>? Emitted;

    public IReadOnlyList<UiLogEvent> Snapshot()
    {
        lock (_events) return _events.ToList();
    }

    public void Emit(LogEvent logEvent)
    {
        using var buffer = new StringWriter();
        _formatter.Format(logEvent, buffer);
        var evt = new UiLogEvent(buffer.ToString(), logEvent.Level);

        lock (_events)
        {
            _events.Enqueue(evt);
            while (_events.Count > _capacity) _events.Dequeue();
        }

        Emitted?.Invoke(evt);
    }
}

public sealed record UiLogEvent(string RenderedMessage, LogEventLevel Level)
{
    public bool IsError => Level >= LogEventLevel.Error;
    public bool IsWarning => Level == LogEventLevel.Warning;
}
