using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;
using Xunit;

namespace Synapic.Avalonia.Tests;

public class DedupServiceTests
{
    /// <summary>The committed sample image, found by walking up from the test
    /// bin directory (same approach as EndToEndRoundTripTests).</summary>
    private static string? FindSampleImage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Synapic.Avalonia.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ── Preview thumbnails ────────────────────────────────────────────

    [Fact]
    public void CreateThumbnail_ScalesToTheBoxAndReturnsAJpeg()
    {
        var sample = FindSampleImage();
        if (sample is null) return; // repo checkout not reachable from the bin

        var bytes = DedupService.CreateThumbnail(sample, maxSize: 128);

        Assert.NotNull(bytes);
        Assert.True(bytes!.Length > 0);
        // JPEG magic — the buffer is what the row decodes straight into a bitmap.
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        using var decoded = NetVips.Image.NewFromBuffer(bytes, "")!;
        Assert.True(decoded.Width <= 128, $"thumbnail is {decoded.Width}px wide");
        Assert.True(decoded.Height <= 128, $"thumbnail is {decoded.Height}px tall");
    }

    [Fact]
    public void CreateThumbnail_UnreadableFile_ReturnsNull()
    {
        // Not an image at all: the row just loses its preview, the scan continues.
        Assert.Null(DedupService.CreateThumbnail(Path.Combine(Path.GetTempPath(), "synapic-no-such-file.jpg")));
    }

    [Fact]
    public void HammingDistance_IdenticalHashes_IsZero()
    {
        // Exposed via grouping behavior: identical hashes always group together.
        var result = DedupTestHarness.Group(new Dictionary<string, ulong>
        {
            ["a.png"] = 0b_1010_1010,
            ["b.png"] = 0b_1010_1010,
        }, 0.90);

        Assert.Single(result);
        Assert.Equal(2, result[0].Items.Length);
    }

    [Fact]
    public void Grouping_RespectsThreshold()
    {
        // 64-bit hashes differing by 6 bits → similarity 0.906 ≥ 0.90 → same group.
        var close = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_111111UL, // 6 bits
        };
        Assert.Single(DedupTestHarness.Group(close, 0.90));

        // Differing by 7 bits → similarity 0.891 < 0.90 → separate.
        var far = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_1111111UL, // 7 bits
        };
        Assert.Empty(DedupTestHarness.Group(far, 0.90));
    }

    [Fact]
    public void Grouping_TransitiveChains_JoinViaUnionFind()
    {
        // a≈b and b≈c but a!~c → still one group (connected components).
        var hashes = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_111111UL,        // 6 bits from a
            ["c.png"] = 0b_111111_111111UL, // 6 bits from b, 12 from a
        };
        var groups = DedupTestHarness.Group(hashes, 0.90);
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Items.Length);
    }

    [Fact]
    public void Grouping_Singletons_AreExcluded()
    {
        var hashes = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = ulong.MaxValue,
        };
        Assert.Empty(DedupTestHarness.Group(hashes, 0.90));
    }
}
