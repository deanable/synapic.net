using System.Diagnostics;
using Synapic.Main.Services.Processing;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Main.Tests;

/// <summary>
/// Scale behaviour of the fingerprint grouping. The comparison is a hamming
/// radius search: pair every fingerprint with every other one and union the
/// pairs inside the threshold. Written as the obvious double loop that is
/// n²/2 popcounts — 125 million at 20,000 items, 1.25 billion at 50,000 — and it
/// runs again on every move of the results page's threshold slider. These tests
/// pin the cost down without pinning a wall-clock number.
/// </summary>
public class DedupGroupingScaleTests
{
    private readonly ITestOutputHelper _output;

    public DedupGroupingScaleTests(ITestOutputHelper output) => _output = output;

    /// <summary>Deterministic 64-bit fingerprints that stand in for pHashes:
    /// uniform bits, plus a few planted near-copies so the grouping has real
    /// work to find rather than only misses.</summary>
    private static Dictionary<string, ulong> Fingerprints(int count, int seed = 1234, int plantedPairs = 40)
    {
        var rng = new Random(seed);
        var hashes = new Dictionary<string, ulong>(count);
        for (var i = 0; i < count; i++)
        {
            ulong value = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
            hashes[$"local|C:\\set\\{i:D6}.jpg"] = value;
        }

        var keys = hashes.Keys.ToArray();
        for (var p = 0; p < plantedPairs; p++)
        {
            var a = rng.Next(keys.Length);
            int b;
            do { b = rng.Next(keys.Length); } while (b == a);
            // A near-copy inside the duplicate threshold (≤ 6 bits at 0.90).
            var flipped = hashes[keys[a]];
            for (var bit = 0; bit < 3; bit++)
                flipped ^= 1UL << rng.Next(64);
            hashes[keys[b]] = flipped;
        }
        return hashes;
    }

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public void Grouping_a_large_catalog_finishes_quickly(int count)
    {
        var hashes = Fingerprints(count);
        var opts = new DedupOptions(HashAlgorithm.PHash, 0.90);
        var service = new DedupService();

        var sw = Stopwatch.StartNew();
        var result = service.GroupFromHashes(hashes, new Dictionary<string, ulong>(), opts);
        sw.Stop();

        _output.WriteLine($"{count:N0} fingerprints @ 0.90: {sw.ElapsedMilliseconds:N0} ms, " +
                          $"{result.Groups.Count:N0} group(s)");

        Assert.NotEmpty(result.Groups);
        // A deliberately generous ceiling: the serial double loop this replaces
        // took whole seconds here on a desktop CPU. What the test is really
        // guarding is the order of growth, not the machine's speed.
        Assert.True(sw.Elapsed.TotalSeconds < 5,
            $"grouping {count:N0} fingerprints took {sw.Elapsed.TotalSeconds:0.00}s");
    }

    [Fact]
    public void Grouping_an_identical_set_of_copies_is_cheap()
    {
        // A folder of byte-identical copies shares one fingerprint: the exact
        // duplicates are one group, and nothing should be compared pairwise.
        const int count = 20_000;
        var hashes = new Dictionary<string, ulong>(count);
        for (var i = 0; i < count; i++)
            hashes[$"local|C:\\copies\\{i:D6}.jpg"] = 0xDEADBEEFCAFEF00DUL;

        var sw = Stopwatch.StartNew();
        var result = new DedupService().GroupFromHashes(
            hashes, new Dictionary<string, ulong>(), new DedupOptions(HashAlgorithm.PHash, 0.90));
        sw.Stop();

        _output.WriteLine($"{count:N0} identical copies: {sw.ElapsedMilliseconds:N0} ms");
        var group = Assert.Single(result.Groups);
        Assert.Equal(count, group.Items.Length);
        Assert.True(sw.Elapsed.TotalSeconds < 2, $"took {sw.Elapsed.TotalSeconds:0.00}s");
    }
}
