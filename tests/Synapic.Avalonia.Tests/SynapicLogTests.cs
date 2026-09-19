using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The log must be a single file, overwritten on every run (user request) so
/// each debugging session starts from a clean transcript that contains
/// everything from process launch to exit.
/// </summary>
public class SynapicLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "synapic-log-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Log_file_is_overwritten_per_run()
    {
        try
        {
            SynapicLog.ResetForTests();
            SynapicLog.Initialize(_dir, "info");
            SynapicLog.Info("Run1", "first-run message");
            SynapicLog.ResetForTests();

            var content1 = File.ReadAllText(SynapicLog.LogFilePath);
            Assert.Contains("first-run message", content1);

            // Second "run": the same file must start empty again.
            SynapicLog.Initialize(_dir, "info");
            SynapicLog.Info("Run2", "second-run message");
            SynapicLog.ResetForTests();

            var content2 = File.ReadAllText(SynapicLog.LogFilePath);
            Assert.DoesNotContain("first-run message", content2);
            Assert.Contains("second-run message", content2);
        }
        finally
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Debug_messages_reach_the_file_log()
    {
        try
        {
            SynapicLog.ResetForTests();
            SynapicLog.Initialize(_dir, "info");
            SynapicLog.Debug("RunDbg", "detailed diagnostic line");
            SynapicLog.ResetForTests();

            Assert.Contains("detailed diagnostic line", File.ReadAllText(SynapicLog.LogFilePath));
        }
        finally
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        // Leave the global logger in a clean state for other tests.
        SynapicLog.ResetForTests();
    }
}
