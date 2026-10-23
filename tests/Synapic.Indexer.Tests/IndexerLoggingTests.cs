using System.Diagnostics;
using Synapic.Core.Services;
using Synapic.Indexer.Hosting;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class IndexerLoggingTests : IDisposable
{
    private readonly string _root;
    private readonly IndexerPaths _paths;

    public IndexerLoggingTests()
    {
        SynapicLog.ResetForTests();
        _root = Path.Combine(Path.GetTempPath(), "synapic-indexer-log-" + Guid.NewGuid().ToString("N"));
        _paths = new IndexerPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        SynapicLog.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string ReadLog()
    {
        SynapicLog.ResetForTests(); // flushes
        var files = Directory.GetFiles(_paths.LogsDirectory, "indexer-*.log");
        Assert.Single(files);
        return File.ReadAllText(files[0]);
    }

    [Fact]
    public void The_service_writes_one_daily_file_and_no_second_log()
    {
        IndexerLogging.Configure(_paths, "Information");

        SynapicLog.Info("Test", "engine line reached the service log");

        var text = ReadLog();
        Assert.Contains("engine line reached the service log", text);
        Assert.False(File.Exists(Path.Combine(_paths.LogsDirectory, "synapic.log")));
        Assert.True(File.Exists(Path.Combine(_paths.LogsDirectory, "indexer-" + DateTime.Now.ToString("yyyyMMdd") + ".log")));
    }

    [Fact]
    public void The_configured_level_filters_the_file_sink()
    {
        IndexerLogging.Configure(_paths, "Warning");

        SynapicLog.Info("Test", "information must be filtered");
        SynapicLog.Warning("Test", "warning must survive");
        SynapicLog.Error("Test", "error must survive");

        var text = ReadLog();
        Assert.DoesNotContain("information must be filtered", text);
        Assert.Contains("warning must survive", text);
        Assert.Contains("error must survive", text);
    }

    [Fact]
    public void The_event_log_sink_is_never_configured_for_a_missing_source_and_never_creates_one()
    {
        // Creating a source needs elevation, so the service must not try: it
        // uses the sink only when --install already registered the source. The
        // query itself can throw for an unprivileged account (Security/State
        // logs are unreadable), which must read as "absent", not as a crash.
        var existed = IndexerLogging.EventLogSourceExists();

        var result = IndexerLogging.Configure(_paths, "Information");

        Assert.Equal(existed, result.EventLogSinkActive);
        if (!existed)
            Assert.False(IndexerLogging.EventLogSourceExists());
    }
}
