using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The live log starts empty for every run, and the run it replaces is rotated
/// into <c>logs/archives</c> and pruned to <see cref="SynapicLog.MaxArchivedLogs"/>.
/// Deleting it outright (the old behaviour) made an exception raised on an
/// earlier launch unrecoverable by the time anyone went looking for it.
/// </summary>
[Collection("SynapicLogSerial")]
public class SynapicLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "synapic-log-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Live_file_starts_empty_and_the_previous_run_is_archived()
    {
        try
        {
            SynapicLog.RestartForTests(_dir, "info");
            SynapicLog.Info("Run1", "first-run message");

            // Second "run": no reset needed - re-Initialize on the same
            // directory is what rotates. Paths are read straight from _dir so
            // the assertions cannot be redirected by another test booting the
            // real App against the process-global logger.
            SynapicLog.Initialize(_dir, "info");
            SynapicLog.Info("Run2", "second-run message");
            // Release the live file's handle before reading it.
            SynapicLog.ResetForTests();

            var archives = Directory.GetFiles(Path.Combine(_dir, "archives"), "synapic-*.log");
            Assert.Contains("first-run message", File.ReadAllText(Assert.Single(archives)));

            var live = File.ReadAllText(Path.Combine(_dir, "synapic.log"));
            Assert.DoesNotContain("first-run message", live);
            Assert.Contains("second-run message", live);
        }
        finally
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Archives_are_pruned_to_the_configured_keep_count()
    {
        try
        {
            SynapicLog.RestartForTests(_dir, "info");

            // Older runs with distinct chronological names (the naming scheme
            // PruneArchives orders by), plus the live log about to be rotated.
            var archivesDir = Path.Combine(_dir, "archives");
            Directory.CreateDirectory(archivesDir);
            var extra = SynapicLog.MaxArchivedLogs + 3;
            for (var i = 0; i < extra; i++)
            {
                var name = $"synapic-20200101-{i:D6}.log";
                File.WriteAllText(Path.Combine(archivesDir, name), "old run");
            }
            SynapicLog.Info("Run", "about to be rotated");

            SynapicLog.Initialize(_dir, "info");
            SynapicLog.ResetForTests();

            var remaining = Directory.GetFiles(archivesDir, "synapic-*.log");
            Assert.Equal(SynapicLog.MaxArchivedLogs, remaining.Length);
            Assert.DoesNotContain(
                remaining,
                f => Path.GetFileName(f).Contains("20200101-000000")); // oldest is gone
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
            SynapicLog.RestartForTests(_dir, "info");
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
