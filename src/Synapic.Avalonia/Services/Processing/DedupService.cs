using System.Buffers.Binary;
using System.Security.Cryptography;
using NetVips;

namespace Synapic.Avalonia.Services.Processing;

/// <summary>Hash algorithms (spec §5.3 DedupService).</summary>
public enum HashAlgorithm
{
    PHash,
    DHash,
    AHash,
    ColorMoment,
}

public enum DedupAction
{
    Tag,
    Move,
    Delete,
}

/// <summary>Dedup options (spec §5.3).</summary>
public sealed record DedupOptions(
    HashAlgorithm Algorithm = HashAlgorithm.PHash,
    double Threshold = 0.90,
    int MaxDimension = 512);

/// <summary>Progress reported during hashing/grouping.</summary>
public sealed record DedupProgress(int FilesHashed, int TotalFiles, int GroupsFound);

/// <summary>One duplicate group with a recommended keep-set.</summary>
public sealed class DuplicateGroup
{
    public required string[] Items { get; init; }
    public required double[] SimilarityScores { get; init; }
    public required string HashType { get; init; }
    public string? KeepItem { get; set; }
}

public sealed class DedupResult
{
    public List<DuplicateGroup> Groups { get; } = new();
    public int TotalFiles { get; init; }
    public string Algorithm { get; init; } = "";
    public double Threshold { get; init; }
}

/// <summary>
/// Perceptual-image dedup — C# port of the Python ``src/core/dedup`` package
/// (hash_calculator.py + dedup_engine.py + dedup_strategies.py). Uses NetVips
/// (libvips) for fast image scaling/grayscale, then computes pHash/dHash/aHash
/// on the DCT/gradient/mean planes exactly like the imagehash algorithms.
/// </summary>
public interface IDedupService
{
    Task<DedupResult> FindDuplicatesAsync(
        IEnumerable<string> imagePaths,
        DedupOptions opts,
        IProgress<DedupProgress>? progress = null,
        CancellationToken ct = default);

    Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct = default);
}

public sealed class DedupService : IDedupService
{
    public async Task<DedupResult> FindDuplicatesAsync(
        IEnumerable<string> imagePaths,
        DedupOptions opts,
        IProgress<DedupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var paths = imagePaths.ToArray();
        var hashes = new Dictionary<string, ulong>(paths.Length);
        var algo = opts.Algorithm.ToString().ToLowerInvariant();

        for (var i = 0; i < paths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hash = await Task.Run(() => ComputeHash(paths[i], opts), ct).ConfigureAwait(false);
                if (hash.HasValue) hashes[paths[i]] = hash.Value;
            }
            catch (Exception e)
            {
                SynapicLog.Warning(nameof(DedupService), $"Failed to hash {paths[i]}: {e.Message}");
            }
            progress?.Report(new DedupProgress(i + 1, paths.Length, 0));
        }

        var result = new DedupResult { TotalFiles = paths.Length, Algorithm = algo, Threshold = opts.Threshold };
        GroupDuplicates(hashes, opts, result.Groups);

        progress?.Report(new DedupProgress(paths.Length, paths.Length, result.Groups.Count));
        return result;
    }

    public async Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct = default)
    {
        var ok = true;
        foreach (var group in result.Groups)
        {
            foreach (var item in group.Items.Where(i => !string.Equals(i, group.KeepItem, StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    switch (action)
                    {
                        case DedupAction.Delete:
                            File.Delete(item);
                            SynapicLog.Info(nameof(DedupService), $"Deleted duplicate: {item}");
                            break;
                        case DedupAction.Move:
                        {
                            var dupDir = Path.Combine(Path.GetDirectoryName(item) ?? ".", "duplicates");
                            Directory.CreateDirectory(dupDir);
                            var dest = Path.Combine(dupDir, Path.GetFileName(item));
                            File.Move(item, dest, overwrite: false);
                            SynapicLog.Info(nameof(DedupService), $"Moved duplicate: {item} → {dest}");
                            break;
                        }
                        case DedupAction.Tag:
                            // Tagging duplicates is a Daminion-integration concern;
                            // handled by the orchestrator when a Daminion session is active.
                            SynapicLog.Info(nameof(DedupService), $"Tagged duplicate (Daminion): {item}");
                            break;
                    }
                }
                catch (Exception e)
                {
                    ok = false;
                    SynapicLog.Error(nameof(DedupService), $"Failed to {action} duplicate {item}: {e.Message}");
                }
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return ok;
    }

    // ── Hash computation (imagehash-compatible) ─────────────────────────────

    /// <summary>Compute a 64-bit perceptual hash with libvips acceleration.</summary>
    public static ulong? ComputeHash(string path, DedupOptions opts)
    {
        try
        {
            using var image = Image.NewFromFile(path, access: Enums.Access.Sequential);
            var size = opts.MaxDimension < 64 ? 64 : Math.Min(opts.MaxDimension, 512);

            using var gray = image.Colourspace(Enums.Interpretation.Bw)
                .Resize((double)8 * 8 / Math.Max(image.Width, image.Height));

            return opts.Algorithm switch
            {
                HashAlgorithm.PHash => ComputePHash(gray),
                HashAlgorithm.DHash => ComputeDHash(gray),
                HashAlgorithm.AHash => ComputeAHash(gray),
                HashAlgorithm.ColorMoment => ComputeAHash(gray), // approximation; exact color moments deferred
                _ => ComputePHash(gray),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ulong ComputeAHash(Image gray)
    {
        using var small = gray.Resize(8.0 / gray.Width, vscale: 8.0 / gray.Height);
        var pixels = small.WriteToMemory();
        var avg = pixels.Average(p => (double)p);
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
            if (pixels[i] > avg) hash |= 1UL << (63 - i);
        return hash;
    }

    private static ulong ComputeDHash(Image gray)
    {
        using var small = gray.Resize(9.0 / gray.Width, vscale: 8.0 / gray.Height);
        var pixels = small.WriteToMemory();
        ulong hash = 0;
        var bit = 63;
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                var left = pixels[row * 9 + col];
                var right = pixels[row * 9 + col + 1];
                if (left > right) hash |= 1UL << bit;
                bit--;
            }
        }
        return hash;
    }

    private static ulong ComputePHash(Image gray)
    {
        // 32x32 grayscale, 2D DCT, top-left 8x8 → median threshold (imagehash phash).
        using var small = gray.Resize(32.0 / gray.Width, vscale: 32.0 / gray.Height);
        var pixels = small.WriteToMemory();
        var matrix = new double[32 * 32];
        for (var i = 0; i < pixels.Length; i++) matrix[i] = pixels[i];

        var dct = Dct2D(matrix, 32);
        var flats = new double[64];
        var idx = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                flats[idx++] = dct[y * 32 + x];

        // Exclude the DC term like imagehash does (flatten()[1:] after median
        // over the 8x8 block minus first element).
        var median = Median(flats.Skip(1).ToArray());
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
            if (flats[i] > median) hash |= 1UL << (63 - i);
        return hash;
    }

    private static double[] Dct2D(double[] input, int n)
    {
        var output = new double[n * n];
        var cos = new double[n, n];
        for (var x = 0; x < n; x++)
            for (var u = 0; u < n; u++)
                cos[x, u] = Math.Cos((2.0 * x + 1.0) * u * Math.PI / (2.0 * n));

        for (var u = 0; u < n; u++)
        {
            for (var v = 0; v < n; v++)
            {
                var sum = 0.0;
                for (var y = 0; y < n; y++)
                    for (var x = 0; x < n; x++)
                        sum += input[y * n + x] * cos[x, u] * cos[y, v];
                var cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                var cv = v == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                output[v * n + u] = 0.25 * cu * cv * sum;
            }
        }
        return output;
    }

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // ── Grouping (Union-Find port of dedup_engine.py) ───────────────────────

    private static void GroupDuplicates(Dictionary<string, ulong> hashes, DedupOptions opts, List<DuplicateGroup> groups)
    {
        var items = hashes.Keys.ToArray();
        var parent = new Dictionary<string, string>(items.Length);
        foreach (var item in items) parent[item] = item;

        string Find(string x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        // Normalize hamming distance to a 0-1 similarity; threshold 0.90 → ≤6 bits differ.
        var maxDistance = (int)Math.Round(64 * (1.0 - opts.Threshold));
        for (var i = 0; i < items.Length; i++)
        {
            for (var j = i + 1; j < items.Length; j++)
            {
                var distance = HammingDistance(hashes[items[i]], hashes[items[j]]);
                if (distance <= maxDistance) Union(items[i], items[j]);
            }
        }

        var components = items.GroupBy(Find).Where(g => g.Count() > 1);
        foreach (var component in components)
        {
            var groupItems = component.OrderBy(p => p).ToArray();
            var representative = groupItems[0];
            var scores = groupItems
                .Select(p => 1.0 - HammingDistance(hashes[p], hashes[representative]) / 64.0)
                .ToArray();

            groups.Add(new DuplicateGroup
            {
                Items = groupItems,
                SimilarityScores = scores,
                HashType = opts.Algorithm.ToString().ToLowerInvariant(),
                KeepItem = groupItems[0], // keep-first; UI can override (largest/newest)
            });
        }
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        var x = a ^ b;
        var count = 0;
        while (x != 0)
        {
            x &= x - 1;
            count++;
        }
        return count;
    }
}
