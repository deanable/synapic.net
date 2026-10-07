using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>Sidecar fake returning all three fields in one multimodal response.</summary>
internal sealed class FixedTagSidecar : IInferenceSidecar
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

    public Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
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
