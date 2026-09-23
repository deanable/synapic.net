using Synapic.Avalonia.Services;
using Synapic.Avalonia.Views;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Local-only crash reporting (P6.1): every captured exception must produce a
/// report file containing the exception details and a snapshot of the session
/// log (the main log is rotated into <c>logs/archives</c> on the next run), old reports are pruned,
/// and terminal crashes skip the UI dialog (the process is going down).
/// </summary>
[Collection("SynapicLogSerial")]
public class CrashReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "synapic-crash-tests-" + Guid.NewGuid().ToString("N"));

    private CrashReporterService CreateReporter(int maxReports = 20) =>
        new(crashDirectory: Path.Combine(_dir, "crashes"),
            appVersion: () => "1.2.3",
            maxReportFiles: maxReports);

    public CrashReporterTests()
    {
        SynapicLog.RestartForTests(_dir, "info");
    }

    public void Dispose()
    {
        SynapicLog.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Capture_writes_report_file_with_exception_and_log_snapshot()
    {
        var reporter = CreateReporter();
        SynapicLog.Info("TestCtx", "log line that must appear in the crash report");

        try
        {
            throw new InvalidOperationException("boom-for-test");
        }
        catch (Exception ex)
        {
            var report = reporter.Capture(ex, source: "TestSource", isTerminal: false);

            Assert.NotNull(report);
            Assert.True(File.Exists(report!.ReportPath));
            Assert.Equal(report.ReportPath, CrashReporterService.LastReportPath);
            Assert.Equal(1, reporter.CrashCount);

            var content = File.ReadAllText(report.ReportPath!);
            Assert.Contains("InvalidOperationException", content);
            Assert.Contains("boom-for-test", content);
            Assert.Contains("App version:   1.2.3", content);
            Assert.Contains("log line that must appear in the crash report", content);
            Assert.Contains("TestSource", content);
        }
    }

    [Fact]
    public void Capture_includes_stack_trace()
    {
        var reporter = CreateReporter();
        try
        {
            throw new Exception("stack-check");
        }
        catch (Exception ex)
        {
            var report = reporter.Capture(ex, "TestSource", isTerminal: false);
            Assert.NotNull(report);
            Assert.Contains("at Synapic.Avalonia.Tests.CrashReporterTests", File.ReadAllText(report!.ReportPath!));
        }
    }

    [Fact]
    public void Install_and_uninstall_are_safe_and_idempotent()
    {
        var reporter = CreateReporter();
        reporter.Install();
        reporter.Install();
        reporter.Uninstall();
        reporter.Uninstall();
        Assert.Equal(0, reporter.CrashCount);
    }

    [Fact]
    public void Old_reports_are_pruned_to_the_cap()
    {
        var reporter = CreateReporter(maxReports: 2);
        for (var i = 0; i < 5; i++)
        {
            reporter.Capture(new Exception($"prune-{i}"), "TestSource", isTerminal: false);
        }

        var files = Directory.GetFiles(Path.Combine(_dir, "crashes"), "crash-*.txt");
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void FormatDiagnostics_contains_key_fields()
    {
        var report = new CrashReport(
            DateTime.UtcNow, "System.Exception", "msg", "stack", "Src", "9.9.9", "TestOS", IsTerminal: false);

        var text = CrashReporterUi.FormatDiagnostics(report);

        Assert.Contains("System.Exception", text);
        Assert.Contains("msg", text);
        Assert.Contains("9.9.9", text);
        Assert.Contains("TestOS", text);
    }

    [Fact]
    public void MakeSafeFileName_produces_filesystem_safe_names()
    {
        // Indirectly: a hostile source string must not break file creation.
        var reporter = CreateReporter();
        var report = reporter.Capture(new Exception("x"), source: "a/b\\c:d*e", isTerminal: false);
        Assert.NotNull(report);
        Assert.True(File.Exists(report!.ReportPath));
        Assert.DoesNotContain(":", Path.GetFileName(report.ReportPath!));
    }
}
