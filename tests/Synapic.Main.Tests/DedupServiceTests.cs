using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Xunit;

namespace Synapic.Main.Tests;

public class DedupServiceTests
{
    /// <summary>The committed sample image, found by walking up from the test
    /// bin directory (same approach as EndToEndRoundTripTests).</summary>
    private static string? FindSampleImage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Synapic.Main.Tests", "TestData", "sample.jpg");
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

    // ── Server-hash grouping: selectable rule, spaces never mixed ────

    [Fact]
    public void ServerHashes_ExactRule_MergesOnlyIdenticalValues()
    {
        var exact = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 100UL,
            ["daminion:2"] = 100UL,
            ["daminion:3"] = 200UL,
            ["daminion:4"] = 200UL,
        };

        var groups = DedupTestHarness.GroupExact(exact);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal("server-hash", g.HashType));
        Assert.All(groups, g => Assert.All(g.SimilarityScores, s => Assert.Equal(1.0, s, 10)));
    }

    [Fact]
    public void ServerHashes_ExactRule_DoesNotBridgeValuesInsideThePerceptualThreshold()
    {
        // 0 and 0b111111 differ by 6 bits → similarity 0.906 ≥ 0.90, so the
        // perceptual path groups them. Exact must not: different content hashes
        // are different files.
        var close = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 0UL,
            ["daminion:2"] = 0b_111111UL,
        };

        Assert.Empty(DedupTestHarness.GroupExact(close));
        Assert.Single(DedupTestHarness.Group(close, 0.90)); // the perceptual path still groups
    }

    [Fact]
    public void ServerHashes_HammingRule_GroupsLikeTheAlgorithmicPath()
    {
        var close = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 0UL,
            ["daminion:2"] = 0b_111111UL,  // 6 bits from #1 → inside the threshold
            ["daminion:3"] = 0b_1111111UL, // 7 from #1, but 1 from #2 → joins the chain
        };

        var groups = DedupTestHarness.GroupExactHamming(close);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Items.Length);
        Assert.Equal("server-hash", groups[0].HashType);
    }

    [Fact]
    public void ServerHashes_HammingRule_StillExcludesSingletons()
    {
        var hashes = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 0UL,
            ["daminion:2"] = ulong.MaxValue,
        };

        Assert.Empty(DedupTestHarness.GroupExactHamming(hashes));
    }

    [Fact]
    public void ServerHashes_AreNeverComparedWithAlgorithmicHashes()
    {
        // Identical values on purpose: if the two spaces were ever compared,
        // these two items would merge at distance 0 under either rule.
        var perceptual = new Dictionary<string, ulong> { ["file.png"] = 0UL };
        var exact = new Dictionary<string, ulong> { ["daminion:1"] = 0UL };
        var options = new DedupOptions(Threshold: 0.90);

        Assert.Empty(new DedupService().GroupFromHashes(perceptual, exact, options).Groups);
        Assert.Empty(
            new DedupService()
                .GroupFromHashes(perceptual, exact, options with { ServerHashMatch = ServerHashMatchMode.Hamming })
                .Groups);
    }

    [Fact]
    public void ServerHashes_TotalFilesCountsBothSpaces()
    {
        var result = new DedupService().GroupFromHashes(
            new Dictionary<string, ulong> { ["a.png"] = 1UL, ["b.png"] = 2UL },
            new Dictionary<string, ulong> { ["daminion:1"] = 3UL },
            new DedupOptions(Threshold: 0.90));

        Assert.Equal(3, result.TotalFiles);
    }
}
