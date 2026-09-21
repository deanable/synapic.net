using System.IO;
using System.Text;

namespace Synapic.Avalonia.Services;

/// <summary>One captured crash: the exception plus the environment context.</summary>
public sealed record CrashReport(
    DateTime TimestampUtc,
    string ExceptionType,
    string Message,
    string StackTrace,
    string Source,
    string AppVersion,
    string OsVersion,
    bool IsTerminal)
{
    /// <summary>File the report was written to (null when capture failed).</summary>
    public string? ReportPath { get; init; }
}

/// <summary>
/// Local-only crash reporting (plan P6.1, user decision: no network at all).
/// Installs global handlers for AppDomain unhandled exceptions, task-scheduler
/// unobserved exceptions, and (on Windows) WPF-style thread exceptions via the
/// .NET runtime. Every crash is written as its own file under
/// <c>logs/crashes/</c> next to the app log, together with a copy of the
/// current session log, so a crash can be diagnosed even though the main log
/// is overwritten on the next run. Nothing ever leaves the machine.
/// </summary>
public sealed class CrashReporterService
{
    private readonly string _crashDirectory;
    private readonly Func<string> _appVersion;
    private readonly int _maxReportFiles;
    private readonly object _writeLock = new();
    private int _crashCount;

    /// <summary>Raised after a crash is captured, on the crashing thread.</summary>
    public event Action<CrashReport>? CrashCaptured;

    /// <summary>Path of the most recently written report file (null before any capture).</summary>
    public static string? LastReportPath { get; private set; }

    public CrashReporterService(
        string? crashDirectory = null,
        Func<string>? appVersion = null,
        int maxReportFiles = 20)
    {
        _crashDirectory = crashDirectory
            ?? Path.Combine(SynapicLog.LogDirectory.Length > 0
                ? SynapicLog.LogDirectory
                : Path.Combine(AppContext.BaseDirectory, "logs"), "crashes");
        _appVersion = appVersion ?? (() =>
            typeof(CrashReporterService).Assembly.GetName().Version?.ToString(3) ?? "unknown");
        _maxReportFiles = maxReportFiles;
    }

    public string CrashDirectory => _crashDirectory;

    /// <summary>Number of crashes captured in this process run (for the UI).</summary>
    public int CrashCount => _crashCount;

    /// <summary>
    /// Install the global handlers. Called once at startup, before any other
    /// service runs, so even early-failure crashes are captured.
    /// </summary>
    public void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SynapicLog.Info(nameof(CrashReporterService), $"Crash reporting active - reports in {Path.GetFullPath(_crashDirectory)}");
    }

    /// <summary>Test hook: detach the global handlers.</summary>
    public void Uninstall()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Capture(ex, source: "AppDomain.UnhandledException", isTerminal: e.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Unobserved task exceptions do not kill the process, but they are a
        // latent bug signal — capture them as non-terminal crashes and mark
        // observed so they never escalate to a process crash.
        e.SetObserved();
        Capture(e.Exception, source: "TaskScheduler.UnobservedTaskException", isTerminal: false);
    }

    /// <summary>
    /// Capture an exception into a timestamped report file plus a copy of the
    /// current session log. Never throws: a failing crash reporter must not
    /// turn a recoverable error into a lost session.
    /// </summary>
    public CrashReport? Capture(Exception exception, string source, bool isTerminal)
    {
        try
        {
            var report = new CrashReport(
                DateTime.UtcNow,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                exception.StackTrace ?? "<no stack trace>",
                source,
                _appVersion(),
                Environment.OSVersion.VersionString,
                isTerminal);

            lock (_writeLock)
            {
                _crashCount++;
                Directory.CreateDirectory(_crashDirectory);
                var stamp = report.TimestampUtc.ToString("yyyyMMdd-HHmmss-fff");
                var safeSource = MakeSafeFileName(source);
                var reportPath = Path.Combine(_crashDirectory, $"crash-{stamp}-{safeSource}.txt");

                var sb = new StringBuilder();
                sb.AppendLine($"=== Synapic crash report #{_crashCount} ===");
                sb.AppendLine($"Time (UTC):    {report.TimestampUtc:O}");
                sb.AppendLine($"Source:        {report.Source}");
                sb.AppendLine($"Terminal:      {report.IsTerminal}");
                sb.AppendLine($"App version:   {report.AppVersion}");
                sb.AppendLine($"OS:            {report.OsVersion}");
                sb.AppendLine($"Exception:     {report.ExceptionType}");
                sb.AppendLine($"Message:       {report.Message}");
                sb.AppendLine();
                sb.AppendLine("--- Stack trace ---");
                sb.AppendLine(report.StackTrace);
                sb.AppendLine();
                sb.AppendLine("--- Session log snapshot ---");
                var sessionLog = ReadSessionLogOrExplanation();
                sb.AppendLine(sessionLog);
                File.WriteAllText(reportPath, sb.ToString());

                PruneOldReports();
                SynapicLog.Error(nameof(CrashReporterService),
                    $"Crash captured ({(isTerminal ? "terminal" : "non-terminal")}): {report.ExceptionType}: {report.Message} -> {reportPath}");
                report = report with { ReportPath = reportPath };
                LastReportPath = reportPath;
                CrashCaptured?.Invoke(report);
                return report;
            }
        }
        catch (Exception reporterEx)
        {
            // Last resort: the original exception must not be swallowed by a
            // crashing reporter, but the reporter failure should still surface
            // through whatever logging exists.
            try { SynapicLog.Error(nameof(CrashReporterService), $"Crash reporter failed: {reporterEx}"); } catch { /* beyond help */ }
            return null;
        }
    }

    /// <summary>
    /// Copy the current session log into the report: the main log file is
    /// deleted on the next app run, so the crash file must carry its own
    /// transcript to remain diagnosable.
    /// </summary>
    private string ReadSessionLogOrExplanation()
    {
        try
        {
            var logPath = SynapicLog.LogFilePath;
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                return "(session log not available)";
            // Cap the snapshot: a runaway log must not create multi-GB crash files.
            const long maxBytes = 2 * 1024 * 1024;
            // Serilog keeps the file open for writing; without FileShare.ReadWrite
            // the read fails on Windows ("being used by another process") and the
            // report would ship without its transcript.
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var keepBytes = Math.Min(stream.Length, maxBytes);
            stream.Seek(-keepBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            if (stream.Length > maxBytes)
                content = $"(last {maxBytes / 1024} KB of the session log){Environment.NewLine}{content}";
            return content;
        }
        catch (Exception e)
        {
            return $"(session log could not be read: {e.Message})";
        }
    }

    /// <summary>Keep the newest reports; crash folders must not grow forever.</summary>
    private void PruneOldReports()
    {
        try
        {
            var files = Directory.GetFiles(_crashDirectory, "crash-*.txt")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();
            foreach (var stale in files.Skip(_maxReportFiles))
                File.Delete(stale);
        }
        catch
        {
            // Pruning is best-effort; never fail a capture here.
        }
    }

    private static string MakeSafeFileName(string source)
    {
        var sb = new StringBuilder(source.Length);
        foreach (var c in source)
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "unknown" : (s.Length > 60 ? s[..60] : s);
    }
}
