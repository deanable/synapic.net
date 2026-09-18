using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;
using Xunit;

namespace Synapic.Avalonia.Tests;

public class DedupServiceTests
{
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
