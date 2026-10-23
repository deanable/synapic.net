using Microsoft.Data.Sqlite;
using Synapic.Core.Services;
using Synapic.Indexer.Detection;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// <c>tagging.overwriteExistingTags</c> and <c>tagging.write*</c>, read back from
/// the files rather than from in-memory results. Also pins the deliberate
/// limitation that keyword writes merge (they never delete an existing keyword),
/// so "overwrite" cannot later be mistaken for replacement.
/// </summary>
public sealed class WriteBackPolicyTests
{
    [Fact]
    public async Task Fully_tagged_file_is_completed_without_calling_the_model()
    {
        using var harness = await PipelineHarness.CreateAsync(overwrite: false);
        var path = harness.AddImage("already-tagged.jpg");
        await harness.Writer.WriteAsync(path, new TagResult("Existing", new[] { "old" }, "Old description"));
        await harness.EnqueueAsync(path);

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var row = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
        Assert.Equal(JobState.Completed, row.State);
        Assert.Equal(0, harness.Sidecar.TagCalls);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("existing tags preserved", ReadLastDetail(harness, row.Id));

        var tags = (await harness.Writer.ReadAsync(path))!;
        Assert.Equal("Existing", tags.Category);
        Assert.Equal("Old description", tags.Description);
        Assert.Equal(new[] { "old" }, tags.Keywords);
    }

    [Fact]
    public async Task Partially_tagged_file_fills_only_the_empty_selected_fields()
    {
        // D-11-23: a populated selected field is left out of the tags handed to
        // the writer. Keywords already present therefore stay exactly as they are,
        // while the empty category and description are filled in.
        using var harness = await PipelineHarness.CreateAsync(overwrite: false);
        var path = harness.AddImage("keywords-only.jpg");
        await harness.Writer.WriteAsync(path, new TagResult(null, new[] { "old" }, null));
        await harness.EnqueueAsync(path);

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, harness.Sidecar.TagCalls);
        var row = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
        Assert.Equal(JobState.Completed, row.State);

        var tags = (await harness.Writer.ReadAsync(path))!;
        Assert.Equal("Nature", tags.Category);
        Assert.Equal("A forest at dawn", tags.Description);
        Assert.Equal(new[] { "old" }, tags.Keywords);
    }

    [Fact]
    public async Task Overwrite_writes_every_selected_field_but_still_merges_keywords()
    {
        using var harness = await PipelineHarness.CreateAsync(overwrite: true);
        var path = harness.AddImage("overwrite.jpg");
        await harness.Writer.WriteAsync(path, new TagResult("Existing", new[] { "old" }, "Old description"));
        await harness.EnqueueAsync(path);

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, harness.Sidecar.TagCalls);
        var tags = (await harness.Writer.ReadAsync(path))!;
        Assert.Equal("Nature", tags.Category);
        Assert.Equal("A forest at dawn", tags.Description);
        // Known limitation, pinned on purpose: a keyword write merges, it does
        // not remove. "Overwrite" means "write the model's values", not "delete".
        Assert.Contains("old", tags.Keywords);
        Assert.Contains("forest", tags.Keywords);
    }

    [Fact]
    public async Task A_disabled_field_is_never_written_even_when_the_model_returns_it()
    {
        using var harness = await PipelineHarness.CreateAsync(
            overwrite: true,
            configure: config => config.Tagging.WriteKeywords = false);
        var path = harness.AddImage("no-keywords.jpg");
        await harness.Writer.WriteAsync(path, new TagResult(null, new[] { "old" }, null));
        await harness.EnqueueAsync(path);

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var tags = (await harness.Writer.ReadAsync(path))!;
        Assert.Equal("Nature", tags.Category);
        Assert.Equal("A forest at dawn", tags.Description);
        Assert.Equal(new[] { "old" }, tags.Keywords);
    }

    [Fact]
    public async Task The_write_refreshes_the_fingerprint_so_a_rescan_queues_nothing()
    {
        using var harness = await PipelineHarness.CreateAsync(overwrite: true);
        var path = harness.AddImage("scan/img.jpg");
        var before = harness.CandidateFor(path);
        await harness.EnqueueAsync(path);

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var row = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
        Assert.Equal(JobState.Completed, row.State);
        // The write changed the file; the row must carry the post-write values or
        // the next scan sees "changed" forever (D-11-26).
        Assert.Equal(new FileInfo(path).Length, row.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(path), row.MtimeUtc);
        Assert.NotEqual(before.ContentHash, row.ContentHash);

        harness.Config.Schedule.WatchPaths.Add(Path.GetDirectoryName(path)!);
        var scanner = new LocalFolderScanSource(
            harness.Config, harness.Repository, harness.Hasher, new WorkIntake(harness.Repository));

        var rescan = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(0, rescan.Enqueued);
        Assert.Equal(1, rescan.Found);
        Assert.Equal(JobState.Completed, (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!.State);
    }

    private static string? ReadLastDetail(PipelineHarness harness, long jobId)
    {
        using var connection = new SqliteConnection($"Data Source={harness.Repository.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM job_events WHERE job_id = $id ORDER BY rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", jobId);
        return command.ExecuteScalar() as string;
    }
}
