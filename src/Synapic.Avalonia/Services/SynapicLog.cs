using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Static Serilog bootstrap (spec §5.3 LoggingService): rolling file sink in
/// the app data directory plus an in-memory sink the UI log viewer binds to.
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

    public static void Initialize(string? logDirectory = null, string minimumLevel = "info")
    {
        lock (InitLock)
        {
            if (_initialized) return;

            logDirectory ??= AppConfig.DefaultDirectory;
            Directory.CreateDirectory(logDirectory);
            LogFilePath = Path.Combine(logDirectory, "logs", "synapic-.log");

            _uiSink = new UiLogSink(MaxBufferedEvents);

            var levelSwitch = new LoggingLevelSwitch(ParseLevel(minimumLevel));

            _logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .WriteTo.File(
                    LogFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.Sink(_uiSink)
                .CreateLogger();

            Log.Logger = _logger;
            _initialized = true;
        }
    }

    public static ILogger For(string sourceContext) => (_logger ?? Log.Logger).ForContext("SourceContext", sourceContext);

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
