using System.Numerics;
using Synapic.Main.Services.Processing;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Main.Tests;

/// <summary>
/// The grouped result must not change when the comparison gets faster. These
/// tests pin the shipped grouper against the definition it replaced — a
/// pairwise union-find over every fingerprint pair — and the shipped pHash
/// against the full 32×32 DCT it now derives the same 8×8 block from.
/// </summary>
public class DedupAlgorithmEquivalenceTests
{
    private readonly ITestOutputHelper _output;

    public DedupAlgorithmEquivalenceTests(ITestOutputHelper output) => _output = output;

    // ── Grouping: multi-index candidates vs the pairwise definition ──────────

    /// <summary>
    /// A catalog-shaped hash set: unrelated fingerprints, exact copies of
    /// earlier ones, and near-copies that differ in a handful of bits mostly
    /// within one region of the fingerprint (real pHash bits correlate that way,
    /// which is what makes one pigeonhole bucket lopsided if the partition is
    /// naive).
    /// </summary>
    private static Dictionary<string, ulong> RandomCatalog(int count, int seed)
    {
        var rng = new Random(seed);
        var hashes = new Dictionary<string, ulong>(count);
        var seen = new List<ulong>(count);

        for (var i = 0; i < count; i++)
        {
            ulong value;
            var roll = rng.Next(100);
            if (roll < 12 && seen.Count > 0)
            {
                value = seen[rng.Next(seen.Count)]; // exact copy
            }
            else if (roll < 45 && seen.Count > 0)
            {
                value = seen[rng.Next(seen.Count)];
                var flips = 1 + rng.Next(9);
                for (var f = 0; f < flips; f++)
                {
                    // Half the edits stay in a narrow band, so the set has a
                    // lopsided region as well as uniform noise.
                    var bit = (roll < 30) ? rng.Next(64) : rng.Next(12);
                    value ^= 1UL << bit;
                }
            }
            else
            {
                value = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
            }

            seen.Add(value);
            hashes[$"C:\\catalog\\{i:D5}.jpg"] = value;
        }
        return hashes;
    }

    /// <summary>The definition being preserved: union every pair inside the radius.</summary>
    private static string[][] BruteForceGroups(
        Dictionary<string, ulong> hashes, double threshold, int bits = 64)
    {
        var items = hashes.Keys.ToArray();
        var parent = new Dictionary<string, string>(items.Length);
        foreach (var item in items) parent[item] = item;

        string Find(string x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        var max = DedupService.MaxHammingDistance(threshold, bits);
        for (var i = 0; i < items.Length; i++)
        {
            for (var j = i + 1; j < items.Length; j++)
            {
                if (BitOperations.PopCount(hashes[items[i]] ^ hashes[items[j]]) > max) continue;
                var a = Find(items[i]);
                var b = Find(items[j]);
                if (a != b) parent[a] = b;
            }
        }

        var order = new List<string>();
        var members = new Dictionary<string, List<string>>();
        foreach (var item in items)
        {
            var root = Find(item);
            if (!members.TryGetValue(root, out var list))
            {
                members[root] = list = new List<string>(2);
                order.Add(root);
            }
            list.Add(item);
        }

        return order
            .Select(root => members[root].OrderBy(p => p).ToArray())
            .Where(group => group.Length > 1)
            .ToArray();
    }

    private static void AssertSameGroups(
        Dictionary<string, ulong> hashes,
        DedupResult actual,
        string[][] expected,
        int bits)
    {
        Assert.Equal(expected.Length, actual.Groups.Count);
        for (var g = 0; g < expected.Length; g++)
        {
            Assert.Equal(expected[g], actual.Groups[g].Items);
            // Scores are measured against the group's first item, over the
            // fingerprint's own width.
            var representative = expected[g][0];
            for (var i = 0; i < expected[g].Length; i++)
            {
                var distance = BitOperations.PopCount(hashes[expected[g][i]] ^ hashes[representative]);
                Assert.Equal(1.0 - distance / (double)bits, actual.Groups[g].SimilarityScores[i], 12);
            }
        }
    }

    [Theory]
    [InlineData(0.90, 1, 300)]  // duplicate scan, pHash
    [InlineData(0.85, 2, 300)]  // similarity search, pHash
    [InlineData(0.50, 3, 200)]  // very loose: most fingerprints join something
    [InlineData(0.99, 4, 300)]  // duplicate scan, dHash-ish
    [InlineData(1.00, 5, 300)]  // only identical fingerprints
    [InlineData(0.97, 6, 250)]
    public void Perceptual_grouping_matches_the_pairwise_definition(
        double threshold, int seed, int count)
    {
        var hashes = RandomCatalog(count, seed);
        var opts = new DedupOptions(HashAlgorithm.PHash, threshold);

        var actual = new DedupService().GroupFromHashes(
            hashes, new Dictionary<string, ulong>(), opts);
        var expected = BruteForceGroups(hashes, threshold);

        _output.WriteLine($"{count} fingerprints @ {threshold:0.00}: " +
                          $"{actual.Groups.Count} group(s), {expected.Length} expected");
        AssertSameGroups(hashes, actual, expected, bits: 64);
    }

    [Fact]
    public void Grouping_at_zero_threshold_joins_every_fingerprint()
    {
        // 0.0 maps to a budget of every bit, so no pair can be outside it.
        var hashes = RandomCatalog(120, 42);
        var actual = new DedupService().GroupFromHashes(
            hashes, new Dictionary<string, ulong>(), new DedupOptions(HashAlgorithm.PHash, 0.0));

        var group = Assert.Single(actual.Groups);
        Assert.Equal(hashes.Count, group.Items.Length);
    }

    [Theory]
    [InlineData(0.90, 11, 300)]
    [InlineData(0.85, 12, 300)]
    [InlineData(1.00, 13, 200)]
    public void Server_hash_hamming_grouping_matches_the_pairwise_definition(
        double threshold, int seed, int count)
    {
        // Server hashes are 32 bits wide, so the same threshold is a smaller
        // bit budget and a different partition.
        var full = RandomCatalog(count, seed);
        var hashes = full.ToDictionary(
            pair => pair.Key,
            pair => pair.Value & 0xFFFF_FFFFUL);

        var opts = new DedupOptions(
            HashAlgorithm.PHash, threshold, ServerHashMatch: ServerHashMatchMode.Hamming);
        var actual = new DedupService().GroupFromHashes(
            new Dictionary<string, ulong>(), hashes, opts);
        var expected = BruteForceGroups(hashes, threshold, bits: 32);

        _output.WriteLine($"{count} server hashes @ {threshold:0.00}: " +
                          $"{actual.Groups.Count} group(s), {expected.Length} expected");
        AssertSameGroups(hashes, actual, expected, bits: 32);
    }

    [Fact]
    public void Chained_groups_stay_one_group()
    {
        // A-B and B-C inside the radius, A-C outside it: transitivity is part of
        // the result, and the multi-index search must not split the chain.
        var hashes = new Dictionary<string, ulong>
        {
            ["C:\\chain\\a.jpg"] = 0UL,
            ["C:\\chain\\b.jpg"] = 0b111111UL,          // 6 bits from a
            ["C:\\chain\\c.jpg"] = 0b111111111111UL,    // 6 from b, 12 from a
        };

        var groups = new DedupService()
            .GroupFromHashes(hashes, new Dictionary<string, ulong>(),
                new DedupOptions(HashAlgorithm.PHash, 0.90))
            .Groups;

        Assert.Equal(3, Assert.Single(groups).Items.Length);
    }

    [Fact]
    public void A_lopsided_bit_split_still_finds_every_pair()
    {
        // Every fingerprint shares the same low 16 bits, so a contiguous
        // partition would put the whole set in one bucket. The partition is
        // ordered by how evenly each bit splits the set, which keeps the
        // candidate sets small without changing which pairs are found.
        var rng = new Random(77);
        var hashes = new Dictionary<string, ulong>();
        for (var i = 0; i < 200; i++)
        {
            var high = (ulong)rng.Next(1 << 20) << 16;
            hashes[$"C:\\flat\\{i:D4}.jpg"] = high | 0x0FFFUL;

            // A near-copy in the high bits only.
            if (i % 3 == 0)
                hashes[$"C:\\flat\\{i:D4}b.jpg"] = (high ^ 0b11UL) << 16 | 0x0FFFUL;
        }

        var actual = new DedupService().GroupFromHashes(
            hashes, new Dictionary<string, ulong>(), new DedupOptions(HashAlgorithm.PHash, 0.95));

        AssertSameGroups(hashes, actual, BruteForceGroups(hashes, 0.95), bits: 64);
    }

    // ── pHash: separable 8×8 block vs the full 32×32 DCT ────────────────────

    private static string WriteImage(string dir, string name, Func<int, int, int, byte> sample, int w, int h)
    {
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var value = sample(x, y, i);
                px[i] = value;
                px[i + 1] = value;
                px[i + 2] = value;
            }

        var path = Path.Combine(dir, name);
        using var img = NetVips.Image.NewFromMemory(px, w, h, 3, NetVips.Enums.BandFormat.Uchar);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) img.Pngsave(path);
        else img.Jpegsave(path, q: 92);
        return path;
    }

    /// <summary>
    /// The pre-optimisation implementation, kept here as the reference: a full
    /// 32×32 separable DCT of the same normalised working image, then imagehash's
    /// 8×8 block and median threshold.
    /// </summary>
    private static ulong FullDctPHash(string path)
    {
        using var image = NetVips.Image.NewFromFile(path, access: NetVips.Enums.Access.Sequential);
        using var oriented = image.Autorot();
        using var flattened = oriented.HasAlpha()
            ? oriented.Flatten(background: new double[] { 255 })
            : null;
        var opaque = (NetVips.Image?)flattened ?? oriented;
        using var gray = opaque.Colourspace(NetVips.Enums.Interpretation.Bw).ExtractBand(0);

        const int cap = 512;
        var longest = Math.Max(gray.Width, gray.Height);
        using var capped = longest > cap ? gray.Resize(cap / (double)longest) : null;
        var working = (NetVips.Image?)capped ?? gray;
        using var small = working.Resize(32.0 / working.Width, vscale: 32.0 / working.Height);
        var pixels = small.WriteToMemory<byte>();

        const int n = 32;
        var cos = new double[n, n];
        for (var x = 0; x < n; x++)
            for (var u = 0; u < n; u++)
                cos[x, u] = Math.Cos((2.0 * x + 1.0) * u * Math.PI / (2.0 * n));

        var flats = new double[64];
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var y = 0; y < n; y++)
                    for (var x = 0; x < n; x++)
                        sum += pixels[y * n + x] * cos[x, u] * cos[y, v];
                var cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                var cv = v == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                flats[v * 8 + u] = 0.25 * cu * cv * sum;
            }
        }

        var medianValues = flats.Skip(1).OrderBy(value => value).ToArray();
        var median = medianValues.Length % 2 == 1
            ? medianValues[medianValues.Length / 2]
            : (medianValues[medianValues.Length / 2 - 1] + medianValues[medianValues.Length / 2]) / 2.0;

        ulong hash = 0;
        for (var i = 0; i < 64; i++)
            if (flats[i] > median) hash |= 1UL << (63 - i);
        return hash;
    }

    [Fact]
    public void PHash_matches_the_full_dct_reference()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synapic-phash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var images = new[]
            {
                // Structured scene.
                WriteImage(dir, "scene.jpg",
                    (x, y, _) => (byte)Math.Clamp(60 + 150.0 * x / 400 + 25 * Math.Sin(y / 17.0), 0, 255), 400, 300),
                // Smooth two-axis gradient: low frequencies close enough in
                // magnitude that the median decision is not trivially obvious.
                WriteImage(dir, "gradient.jpg",
                    (x, y, _) => (byte)Math.Clamp(20 + 180.0 * x / 400 + 55.0 * y / 300, 0, 255), 400, 300),
                // Two axes of structure plus a strong edge.
                WriteImage(dir, "edge.jpg",
                    (x, y, _) => (byte)Math.Clamp(10 + 120.0 * x / 400 + 40.0 * y / 300 + ((x / 40 + y / 40) % 2) * 70, 0, 255), 400, 300),
                // Fine detail.
                WriteImage(dir, "detail.jpg",
                    (x, y, _) => (byte)((x * 7 + y * 13) % 256), 400, 300),
                // Portrait orientation.
                WriteImage(dir, "portrait.jpg",
                    (x, y, _) => (byte)Math.Clamp(30 + 90.0 * y / 600 + 20 * Math.Cos(x / 11.0), 0, 255), 300, 600),
                // Small image, no downscale cap involved.
                WriteImage(dir, "small.jpg", (x, y, _) => (byte)(x * y % 251), 160, 120),
            };

            var opts = new DedupOptions(HashAlgorithm.PHash, 0.90, MaxDimension: 512);
            foreach (var path in images)
            {
                var expected = FullDctPHash(path);
                var actual = DedupService.ComputeHash(path, opts);
                Assert.NotNull(actual);
                _output.WriteLine($"{Path.GetFileName(path)}: {actual:X16} (reference {expected:X16})");
                Assert.Equal(expected, actual!.Value);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp cleanup */ }
        }
    }

    /// <summary>
    /// A featureless image carries no fingerprint: every AC coefficient of a flat
    /// field is mathematically zero, so which side of the median each one lands on
    /// is rounding noise, and reordering the summation (which is all the separable
    /// DCT does) can flip those bits. Two things still have to hold: hashing the
    /// same file twice gives the same value — the fingerprint cache and the
    /// threshold slider both depend on that — and an exact copy hashes the same as
    /// its original, so copies of a blank scan are still found. What is *not*
    /// promised is that two different featureless images hash apart; that is a
    /// limit of the fingerprint, not of the grouping.
    /// </summary>
    [Fact]
    public void A_featureless_image_hashes_stably_and_copies_match()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synapic-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = WriteImage(dir, "flat.jpg", (_, _, _) => 128, 400, 300);
            var copy = Path.Combine(dir, "flat-copy.jpg");
            File.Copy(path, copy);
            var opts = new DedupOptions(HashAlgorithm.PHash, 0.90);

            var first = DedupService.ComputeHash(path, opts);
            var second = DedupService.ComputeHash(path, opts);

            Assert.NotNull(first);
            Assert.Equal(first, second);
            Assert.Equal(first, DedupService.ComputeHash(copy, opts));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp cleanup */ }
        }
    }

    // ── Local scan: parallel hashing produces the same scan ─────────────────

    [Fact]
    public async Task Parallel_local_hashing_produces_the_same_scan_as_serial()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synapic-parallel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = DedupService.LocalHashingParallelism;
        try
        {
            // Twelve images: six distinct, each with an exact copy, so the scan
            // has real groups to find whatever order the workers finish in.
            for (var i = 0; i < 6; i++)
            {
                var sample = i;
                var one = WriteImage(dir, $"photo{i}.jpg",
                    (x, y, _) => (byte)Math.Clamp(40 + 30 * sample + 100.0 * x / 400 + 40 * Math.Sin(y / (7.0 + sample)), 0, 255),
                    400, 300);
                var two = Path.Combine(dir, $"photo{i}-copy.jpg");
                File.Copy(one, two);
            }

            var files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToArray();
            var opts = new DedupOptions(HashAlgorithm.PHash, 0.90);

            DedupService.LocalHashingParallelism = 1;
            var serial = await new DedupService().FindDuplicatesAsync(files, opts);

            DedupService.LocalHashingParallelism = 4;
            var parallel = await new DedupService().FindDuplicatesAsync(files, opts);

            Assert.Equal(files.Length, serial.PerceptualHashes.Count);
            Assert.Equal(serial.PerceptualHashes.Count, parallel.PerceptualHashes.Count);
            foreach (var (path, hash) in serial.PerceptualHashes)
                Assert.Equal(hash, parallel.PerceptualHashes[path]);

            Assert.Equal(serial.Groups.Count, parallel.Groups.Count);
            for (var g = 0; g < serial.Groups.Count; g++)
            {
                Assert.Equal(serial.Groups[g].Items, parallel.Groups[g].Items);
                Assert.Equal(serial.Groups[g].SimilarityScores, parallel.Groups[g].SimilarityScores);
            }

            // Every original is grouped with its own copy.
            for (var i = 0; i < 6; i++)
            {
                var original = files.Single(f => f.EndsWith($"photo{i}.jpg", StringComparison.Ordinal));
                var copy = files.Single(f => f.EndsWith($"photo{i}-copy.jpg", StringComparison.Ordinal));
                Assert.Contains(serial.Groups, group =>
                    group.Items.Contains(original) && group.Items.Contains(copy));
            }
        }
        finally
        {
            DedupService.LocalHashingParallelism = previous;
            try { Directory.Delete(dir, true); } catch { /* temp cleanup */ }
        }
    }
}
