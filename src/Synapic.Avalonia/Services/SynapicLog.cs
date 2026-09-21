using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Static Serilog bootstrap (spec §5.3 LoggingService): a single log file
/// that is OVERWRITTEN on every run (one clean file per debugging session)
/// in the application directory, plus an in-memory sink the UI log viewer
/// binds to. The file captures everything from Debug up (regardless of the
/// UI log level) so server startup failures can be diagnosed afterwards.
/// </summary>
public static class SynapicLog
{
    private const int MaxBufferedEvents = 2000;

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
            if (_initialized) return;

            var dir = logDirectory ?? ResolveLogDirectory();
            Directory.CreateDirectory(dir);
            LogDirectory = dir;
            LogFilePath = Path.Combine(dir, "synapic.log");

            // Overwrite per run: delete the previous session's file first.
            // If it is locked (e.g. a second app instance), keep appending.
            try
            {
                if (File.Exists(LogFilePath)) File.Delete(LogFilePath);
            }
            catch (IOException)
            {
                // Locked by another instance - append to it instead.
            }

            _uiSink = new UiLogSink(MaxBufferedEvents);
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
                "Synapic log started - file: {LogFilePath} (overwritten on each app run)", LogFilePath);
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
