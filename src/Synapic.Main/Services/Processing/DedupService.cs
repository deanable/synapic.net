using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using MetadataExtractor.Formats.Exif;
using NetVips;

namespace Synapic.Main.Services.Processing;

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

/// <summary>
/// How Daminion server content hashes are compared. Server hashes are only
/// ever compared with other server hashes — never against algorithmic
/// perceptual hashes, which are a different hash function.
/// </summary>
public enum ServerHashMatchMode
{
    /// <summary>
    /// Same hashCode value only. Safe when the server hash is byte-exact:
    /// two different files whose values merely sit close together stay apart.
    /// </summary>
    Exact,

    /// <summary>
    /// Hamming distance inside the threshold, the way algorithmic hashes are
    /// compared. Use it when the server hashCode turns out to be perceptual
    /// (visually similar files share close values).
    /// </summary>
    Hamming,
}

/// <summary>Dedup options (spec §5.3).</summary>
public sealed record DedupOptions(
    HashAlgorithm Algorithm = HashAlgorithm.PHash,
    double Threshold = 0.90,
    int MaxDimension = 512,
    ServerHashMatchMode ServerHashMatch = ServerHashMatchMode.Exact);

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

    /// <summary>Perceptual hash of one image (null when unreadable). Exposed for
    /// callers that hash incrementally — the Daminion dedup scan downloads
    /// originals one at a time and must not keep them all on disk.</summary>
    ulong? ComputeHash(string path, DedupOptions opts);

    /// <summary>
    /// Group already-computed hashes (Daminion scan) instead of files (local
    /// scan). The two key spaces stay apart: <paramref name="exactHashes"/> are
    /// server content hashes, grouped among themselves by
    /// <see cref="DedupOptions.ServerHashMatch"/> (exact equality by default,
    /// hamming distance on request), while <paramref name="perceptualHashes"/> are
    /// algorithmic hashes, always grouped by the threshold's hamming distance.
    /// A server hash is never compared with a perceptual hash — different hash
    /// functions, so their bit patterns are not comparable.
    /// </summary>
    DedupResult GroupFromHashes(
        IReadOnlyDictionary<string, ulong> perceptualHashes,
        IReadOnlyDictionary<string, ulong> exactHashes,
        DedupOptions opts);

    /// <summary>Apply an action to an explicit set of file paths — the unchecked
    /// (duplicate) items the user reviewed, across all groups.</summary>
    Task<bool> ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, CancellationToken ct = default);

    Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct = default);
}

public sealed class DedupService : IDedupService
{
    /// <summary>
    /// Local scan: hash every path, then group. The per-file loop is the
    /// shared <see cref="WorkflowRunner"/> (sequential, like every dedup
    /// scan), so hashing, progress and error isolation behave exactly as they
    /// do on the Daminion scan and the other routes.
    /// </summary>
    public async Task<DedupResult> FindDuplicatesAsync(
        IEnumerable<string> imagePaths,
        DedupOptions opts,
        IProgress<DedupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var paths = imagePaths.ToArray();
        var hashes = new Dictionary<string, ulong>(paths.Length);
        var algo = opts.Algorithm.ToString().ToLowerInvariant();

        var items = paths
            .Select(p => new ProcessWorkItem { LocalPath = p, FileName = Path.GetFileName(p) })
            .ToArray();
        var workflow = new WorkflowDefinition(
            Key: "dedup",
            Title: "Duplicate scan",
            MaxDegreeOfParallelism: 1,
            Description: $"hashing {algo} at threshold {opts.Threshold:0.###}");

        // The runner reports at item start and completion; only the completion
        // count moves the DedupProgress the UI renders (same as the old loop's
        // per-file report — the duplicate start-line values are harmless).
        var runnerProgress = progress is null
            ? null
            : new Progress<ProcessProgress>(p => progress.Report(new DedupProgress(p.Processed, p.Total, 0)));

        await new WorkflowRunner().RunItemsAsync(
            workflow, items, new LocalHashHandler(opts, hashes), runnerProgress, log: null, pause: null, ct).ConfigureAwait(false);

        var result = new DedupResult { TotalFiles = paths.Length, Algorithm = algo, Threshold = opts.Threshold };
        GroupDuplicates(hashes, opts, result.Groups);

        progress?.Report(new DedupProgress(paths.Length, paths.Length, result.Groups.Count));
        return result;
    }

    /// <summary>The local dedup scan's per-item operation: hash one file into the shared map.</summary>
    private sealed class LocalHashHandler : IWorkflowItemHandler
    {
        private readonly DedupOptions _opts;
        private readonly Dictionary<string, ulong> _hashes;

        public LocalHashHandler(DedupOptions opts, Dictionary<string, ulong> hashes)
        {
            _opts = opts;
            _hashes = hashes;
        }

        public Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = item.LocalPath ?? throw new InvalidOperationException("Work item has no image path");
            var hash = ComputeHash(path, _opts); // null + log line = unhashable file, skipped
            if (hash is null)
                return Task.FromResult(WorkflowItemOutcome.Fail("unhashable file"));
            lock (_hashes) _hashes[path] = hash.Value;
            return Task.FromResult(WorkflowItemOutcome.Ok);
        }
    }

    ulong? IDedupService.ComputeHash(string path, DedupOptions opts) => ComputeHash(path, opts);

    /// <summary>Group pre-computed hashes — used by the Daminion dedup scan,
    /// whose images are downloaded to a temp file, hashed, and deleted again
    /// one at a time. Server hashes and algorithmic hashes are grouped by their
    /// own rules and never cross-compared (see <see cref="IDedupService.GroupFromHashes"/>).</summary>
    public DedupResult GroupFromHashes(
        IReadOnlyDictionary<string, ulong> perceptualHashes,
        IReadOnlyDictionary<string, ulong> exactHashes,
        DedupOptions opts)
    {
        var result = new DedupResult
        {
            TotalFiles = perceptualHashes.Count + exactHashes.Count,
            Algorithm = opts.Algorithm.ToString().ToLowerInvariant(),
            Threshold = opts.Threshold,
        };
        GroupDuplicates(perceptualHashes, opts, result.Groups);
        if (opts.ServerHashMatch == ServerHashMatchMode.Hamming)
            GroupDuplicates(exactHashes, opts, result.Groups, hashType: "server-hash");
        else
            GroupExact(exactHashes, result.Groups);
        return result;
    }

    public async Task<bool> ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, CancellationToken ct = default)
    {
        var ok = true;
        foreach (var item in paths)
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
        await Task.CompletedTask.ConfigureAwait(false);
        return ok;
    }

    public async Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct = default)
    {
        var ok = true;
        foreach (var group in result.Groups)
        {
            var targets = group.Items
                .Where(i => !string.Equals(i, group.KeepItem, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ok &= await ApplyToPathsAsync(targets, action, ct).ConfigureAwait(false);
        }
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
        catch (Exception e)
        {
            // Null means "this file took part in no group" — without the reason
            // a scan that silently hashes 20 of 31 items is undiagnosable.
            SynapicLog.Warning(nameof(DedupService), $"Could not hash '{path}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Best-effort capture date for dedup ordering: EXIF DateTimeOriginal
    /// (SubIFD), else IFD0 DateTime, else null (caller falls back to file time).
    /// EXIF carries a bare wall clock with no zone, so it is interpreted as
    /// local time and returned as UTC — that round-trips exactly when the UI
    /// renders it back with <c>ToLocalTime()</c>.
    /// </summary>
    public static DateTime? ReadImageDateUtc(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(stream);

            var ticks = ReadExifDate(directories, ExifDirectoryBase.TagDateTimeOriginal)
                ?? ReadExifDate(directories, ExifDirectoryBase.TagDateTime);
            if (ticks is not { } value) return null;

            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
            };
        }
        catch (Exception)
        {
            return null; // unreadable / not an image / malformed EXIF
        }
    }

    private static DateTime? ReadExifDate(IEnumerable<MetadataExtractor.Directory> directories, int tag)
    {
        // Extension method, invoked statically to keep `using MetadataExtractor`
        // (and its `Directory` type) out of this file — System.IO.Directory wins here.
        foreach (var dir in directories)
            if (MetadataExtractor.DirectoryExtensions.TryGetDateTime(dir, tag, out var value))
                return value;
        return null;
    }

    /// <summary>Small JPEG preview of an image for review UIs (the dedup group
    /// rows): libvips shrink-on-load down to <paramref name="maxSize"/> on the
    /// longest edge, encoded into an in-memory buffer the caller decodes and
    /// discards — nothing is ever written to a thumbnail cache. Returns null
    /// for unreadable images, same contract as <see cref="ComputeHash(string, DedupOptions)"/>.</summary>
    public static byte[]? CreateThumbnail(string path, int maxSize = 128)
    {
        try
        {
            // Both dimensions passed, so a portrait stays inside the box too;
            // size: Down never upscales a source that is already small.
            // Autorotate is on by default, so previews sit upright like the
            // file does in any other viewer.
            using var thumb = Image.Thumbnail(path, maxSize, maxSize, size: Enums.Size.Down);
            if (thumb.HasAlpha())
            {
                // JPEG has no alpha channel — composite onto white first
                // (transparent pixels would otherwise fail the save).
                using var flat = thumb.Flatten();
                return flat.JpegsaveBuffer(q: 85);
            }
            return thumb.JpegsaveBuffer(q: 85);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ulong ComputeAHash(Image gray)
    {
        using var small = gray.Resize(8.0 / gray.Width, vscale: 8.0 / gray.Height);
        var pixels = small.WriteToMemory<byte>();
        var avg = pixels.Average(p => (double)p);
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
            if (pixels[i] > avg) hash |= 1UL << (63 - i);
        return hash;
    }

    private static ulong ComputeDHash(Image gray)
    {
        using var small = gray.Resize(9.0 / gray.Width, vscale: 8.0 / gray.Height);
        var pixels = small.WriteToMemory<byte>();
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
        var pixels = small.WriteToMemory<byte>();
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

    private static void GroupDuplicates(
        IReadOnlyDictionary<string, ulong> hashes,
        DedupOptions opts,
        List<DuplicateGroup> groups,
        string? hashType = null)
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
        var maxDistance = MaxHammingDistance(opts.Threshold);
        // Hoist the hash values into an array: this is the O(n²) hot loop, and
        // two dictionary lookups per pair dominate it on a large scope.
        var values = new ulong[items.Length];
        for (var i = 0; i < items.Length; i++) values[i] = hashes[items[i]];

        for (var i = 0; i < items.Length; i++)
        {
            var value = values[i];
            for (var j = i + 1; j < items.Length; j++)
            {
                if (HammingDistance(value, values[j]) <= maxDistance) Union(items[i], items[j]);
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
                HashType = hashType ?? opts.Algorithm.ToString().ToLowerInvariant(),
                KeepItem = groupItems[0], // keep-first; UI can override (largest/newest)
            });
        }
    }

    /// <summary>
    /// Group server content hashes by exact equality — the only safe rule when
    /// the hash function's semantics are unknown. The Daminion API documents
    /// <c>hashCode</c> as a content hash without saying whether it is
    /// byte-exact, so two values that merely sit close together (inside the
    /// perceptual threshold) must stay separate: they are different files until
    /// the server says otherwise. Singletons are dropped, like the perceptual
    /// path does.
    /// </summary>
    private static void GroupExact(IReadOnlyDictionary<string, ulong> hashes, List<DuplicateGroup> groups)
    {
        foreach (var bucket in hashes.GroupBy(pair => pair.Value).Where(g => g.Count() > 1))
        {
            var items = bucket
                .Select(pair => pair.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();

            groups.Add(new DuplicateGroup
            {
                Items = items,
                // Equal server hashes are identical by the server's own
                // reckoning, so each item scores a perfect match against the
                // group's representative.
                SimilarityScores = Enumerable.Repeat(1.0, items.Length).ToArray(),
                HashType = "server-hash",
                KeepItem = items[0],
            });
        }
    }

    private static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>
    /// The hamming-distance cutoff a threshold maps to: 0.90 of 64 bits means at
    /// most 6 may differ. Public so the Daminion scan's diagnostics can state
    /// the rule the comparison actually used.
    /// </summary>
    public static int MaxHammingDistance(double threshold) => (int)Math.Round(64 * (1.0 - threshold));
}
