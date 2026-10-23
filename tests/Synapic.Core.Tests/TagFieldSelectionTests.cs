using Synapic.Core.Services;
using Synapic.Core.Services.Processing;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Core.Tests;

/// <summary>Sidecar fake returning all three fields in one multimodal response.</summary>
internal class FixedTagSidecar : IInferenceSidecar
{
    public SidecarStatus CurrentStatus => SidecarStatus.Ready;
    public int SidecarPort => 0;
    public bool IsRunning => true;

#pragma warning disable CS0067 // events unused by this fake
    public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    public event Action<string>? LogReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;

    public virtual Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        => Task.FromResult(new TagResponse
        {
            Category = "Nature",
            Keywords = ["tree", "forest"],
            Description = "A forest at dawn",
        });

    public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<ModelInfo>());
    public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new HealthResponse());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A reply whose keywords member is JSON null, which bypasses the default.</summary>
internal sealed class NullKeywordsTagSidecar : FixedTagSidecar
{
    public override Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        => Task.FromResult(new TagResponse { Category = "Nature", Keywords = null!, Description = "A forest" });
}

/// <summary>Metadata-writer stub that records what it was asked to write.</summary>
internal sealed class CapturingMetadataWriter : IMetadataWriter
{
    private readonly List<TagResult> _writes = new();
    public IReadOnlyList<TagResult> Writes { get { lock (_writes) return _writes.ToArray(); } }

    public Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct)
    {
        lock (_writes) _writes.Add(tags);
        return Task.FromResult(true);
    }

    public Task<TagResult?> ReadAsync(string filePath, CancellationToken ct) => Task.FromResult<TagResult?>(null);
}

public class TagFieldSelectionTests
{
    private static DatasourceSelection LocalSelection(string dir) => new()
    {
        IsDaminion = false,
        LocalPath = dir,
        LocalRecursive = false,
    };

    private static string MakeImageDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"synapic-fields-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "img1.jpg"), new byte[] { 1 });
        return dir;
    }

    private static async Task<ProcessItemResult> RunOneAsync(TagFieldSelection fields, CapturingMetadataWriter writer)
    {
        var dir = MakeImageDir();
        try
        {
            var orchestrator = new ProcessingOrchestrator(new FixedTagSidecar(), writer, maxDegreeOfParallelism: 1);
            var results = new List<ProcessItemResult>();
            var template = new TagRequest { ImagePath = "", ModelId = "LiquidAI/LFM2.5-VL-450M", Task = "image-text-to-text" };

            await orchestrator.RunAsync(
                LocalSelection(dir), template, new Progress<ProcessProgress>(), _ => Task.CompletedTask,
                CancellationToken.None, results, pause: null, tagFields: fields);

            return Assert.Single(results);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A reply with "keywords": null used to throw while the run summary read
    /// the count, so the item was lost as a failure. It must be recorded with no
    /// keywords instead.
    /// </summary>
    [Fact]
    public async Task A_reply_with_null_keywords_is_recorded_with_no_keywords()
    {
        var dir = MakeImageDir();
        try
        {
            var writer = new CapturingMetadataWriter();
            var orchestrator = new ProcessingOrchestrator(new NullKeywordsTagSidecar(), writer, maxDegreeOfParallelism: 1);
            var results = new List<ProcessItemResult>();
            var template = new TagRequest { ImagePath = "", ModelId = "LiquidAI/LFM2.5-VL-450M", Task = "image-text-to-text" };

            await orchestrator.RunAsync(
                LocalSelection(dir), template, new Progress<ProcessProgress>(), _ => Task.CompletedTask,
                CancellationToken.None, results, pause: null, tagFields: TagFieldSelection.All);

            var result = Assert.Single(results);
            Assert.Empty(result.Keywords);
            Assert.Equal("Nature", result.Category);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AllFields_Default_WritesEverything()
    {
        var writer = new CapturingMetadataWriter();
        var result = await RunOneAsync(TagFieldSelection.All, writer);

        var written = Assert.Single(writer.Writes);
        Assert.Equal("Nature", written.Category);
        Assert.Equal(new[] { "tree", "forest" }, written.Keywords);
        Assert.Equal("A forest at dawn", written.Description);

        Assert.Equal("Nature", result.Category);
        Assert.Equal("A forest at dawn", result.Description);
    }

    [Fact]
    public void Summary_NamesTheFieldsARunWillWrite()
    {
        // What the batch line and the Step 2 warning are built from, so the
        // words have to stay in step with the flags.
        Assert.True(TagFieldSelection.All.IsAll);
        Assert.Equal("keywords, categories and description", TagFieldSelection.All.Summary);
        Assert.Equal("keywords only",
            new TagFieldSelection(Category: false, Keywords: true, Description: false).Summary);
        Assert.Equal("categories and description",
            new TagFieldSelection(Category: true, Keywords: false, Description: true).Summary);

        var none = new TagFieldSelection(Category: false, Keywords: false, Description: false);
        Assert.False(none.IsAll);
        Assert.Equal("nothing (no tag field is selected)", none.Summary);
    }

    [Fact]
    public async Task KeywordsOnly_DropsCategoryAndDescription()
    {
        var writer = new CapturingMetadataWriter();
        var result = await RunOneAsync(new TagFieldSelection(Category: false, Keywords: true, Description: false), writer);

        var written = Assert.Single(writer.Writes);
        Assert.Null(written.Category);
        Assert.Equal(new[] { "tree", "forest" }, written.Keywords);
        Assert.Null(written.Description);

        // The recorded result must match what was actually written (Step 4 shows it).
        Assert.Null(result.Category);
        Assert.Empty(result.Description ?? "");
        Assert.Equal(new[] { "tree", "forest" }, result.Keywords);
    }

    public static IEnumerable<object[]> RealWriterSelections()
    {
        foreach (var extension in new[] { ".jpg", ".png", ".tif" })
        for (var mask = 0; mask < 8; mask++) yield return [extension, mask];
    }

    [Theory]
    [MemberData(nameof(RealWriterSelections))]
    public async Task Real_tagging_path_preserves_unselected_and_unowned_metadata(string extension, int mask)
    {
        using var fixture = new MetadataWriterTests.MetadataFixture(extension);
        fixture.SeedXmp();
        var fields = new TagFieldSelection((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        var writer = new MetadataWriterService();
        var orchestrator = new ProcessingOrchestrator(new FixedTagSidecar(), writer, maxDegreeOfParallelism: 1);
        var results = new List<ProcessItemResult>();
        await orchestrator.RunAsync(
            LocalSelection(fixture.DirectoryPath), new TagRequest { ImagePath = "", ModelId = "test", Task = "image-text-to-text" },
            new Progress<ProcessProgress>(), _ => Task.CompletedTask, CancellationToken.None, results, tagFields: fields);

        Assert.Equal("Success", Assert.Single(results).Status);
        var read = Assert.IsType<TagResult>(await writer.ReadAsync(fixture.Path));
        Assert.Equal(fields.Category ? "Nature" : "Existing", read.Category);
        Assert.Equal(fields.Description ? "A forest at dawn" : "Original description", read.Description);
        Assert.Equal(fields.Keywords ? new[] { "old", "tree", "forest" } : new[] { "old" }, read.Keywords);
        var xmp = fixture.ReadXmp();
        Assert.Equal("Copyright holder", xmp.Descendants(MetadataWriterTests.MetadataFixture.Dc + "rights").Single().Value);
        Assert.Equal("Creator", xmp.Descendants(MetadataWriterTests.MetadataFixture.Dc + "creator").Single().Value);
        fixture.AssertPixelsDecode();
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task CategoryAndDescription_WithoutKeywords()
    {
        var writer = new CapturingMetadataWriter();
        await RunOneAsync(new TagFieldSelection(Category: true, Keywords: false, Description: true), writer);

        var written = Assert.Single(writer.Writes);
        Assert.Equal("Nature", written.Category);
        Assert.Empty(written.Keywords);
        Assert.Equal("A forest at dawn", written.Description);
    }
}
