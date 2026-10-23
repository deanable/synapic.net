using Synapic.Indexer.Detection;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class FileStabilityProbeTests : IDisposable
{
    private readonly string _directory;
    private readonly FileStabilityProbe _probe = new();

    public FileStabilityProbeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "synapic-indexer-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_file_held_by_a_writer_is_not_stable()
    {
        var path = Path.Combine(_directory, "locked.jpg");
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write(new byte[] { 1, 2, 3 });
            writer.Flush();

            Assert.False(_probe.TryOpenExclusive(path, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        Assert.True(_probe.TryOpenExclusive(path, out var afterReason));
        Assert.Null(afterReason);
    }

    [Fact]
    public void A_file_held_for_reading_and_writing_is_not_stable()
    {
        var path = Path.Combine(_directory, "open.jpg");
        using var reader = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.False(_probe.TryOpenExclusive(path, out _));
    }

    [Fact]
    public void A_missing_file_returns_false_instead_of_throwing()
    {
        Assert.False(_probe.TryOpenExclusive(Path.Combine(_directory, "gone.jpg"), out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void A_deleted_file_returns_false_after_a_successful_probe()
    {
        var path = Path.Combine(_directory, "vanishing.jpg");
        File.WriteAllBytes(path, new byte[] { 1 });
        Assert.True(_probe.TryOpenExclusive(path, out _));

        File.Delete(path);

        Assert.False(_probe.TryOpenExclusive(path, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void An_empty_and_a_weird_path_are_answers_not_exceptions()
    {
        Assert.False(_probe.TryOpenExclusive("", out _));
        Assert.False(_probe.TryOpenExclusive("\0bad", out _));
    }
}
