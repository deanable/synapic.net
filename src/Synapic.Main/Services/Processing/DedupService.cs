using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using MetadataExtractor.Formats.Exif;
using NetVips;

namespace Synapic.Main.Services.Processing;

/// <summary>
/// Hash algorithms (spec §5.3 DedupService). Every option is a real 64-bit
/// perceptual fingerprint that the hamming-distance grouping can score:
/// pHash (32×32 DCT), dHash (horizontal gradient), aHash (mean). A
/// colour-moment option was removed rather than shipped — it was silently
/// dispatching to aHash, and colour statistics are not a hamming-comparable
/// fingerprint anyway, so it could only ever duplicate aHash or lie.
/// </summary>
public enum HashAlgorithm
{
    PHash,
    DHash,
    AHash,
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

/// <summary>
/// Dedup options (spec §5.3).
/// </summary>
/// <remarks>
/// <see cref="UseServerHashes"/> is the catalog shortcut: a Daminion item's own
/// <c>hashCode</c> is a content hash, so identical files share it and need no
/// download. It is only meaningful for the duplicate scan — a content hash says
/// nothing about visual similarity, and comparing those values by hamming
/// distance groups coincidences rather than lookalikes. The similarity profile
/// therefore turns it off and hashes pixels.
/// </remarks>
public sealed record DedupOptions(
    HashAlgorithm Algorithm = HashAlgorithm.PHash,
    double Threshold = 0.90,
    int MaxDimension = 512,
    ServerHashMatchMode ServerHashMatch = ServerHashMatchMode.Exact,
    bool UseServerHashes = true);

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

    /// <summary>
    /// The algorithmic fingerprints this result was grouped from, kept so the
    /// result can be re-grouped at another threshold without reading or
    /// downloading anything again (the results page's live sensitivity slider).
    /// Empty only for results built by hand.<br/>
    /// Retention is the price of the slider: a 50,000-item catalog keeps its
    /// 64-bit hashes (a few MB) until the next scan replaces them.
    /// </summary>
    public IReadOnlyDictionary<string, ulong> PerceptualHashes { get; init; } = EmptyHashes;

    /// <summary>The server content hashes the result was grouped from, same
    /// retention rule as <see cref="PerceptualHashes"/>. These are never
    /// compared with algorithmic hashes.</summary>
    public IReadOnlyDictionary<string, ulong> ExactHashes { get; init; } = EmptyHashes;

    private static readonly IReadOnlyDictionary<string, ulong> EmptyHashes =
        new Dictionary<string, ulong>();
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
    /// (duplicate) items the user reviewed, across all groups. <paramref
    /// name="moveDestination"/> is the target folder for
    /// <see cref="DedupAction.Move"/> (created when missing); other actions
    /// ignore it.</summary>
    Task<bool> ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, string? moveDestination = null, CancellationToken ct = default);

    Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, string? moveDestination = null, CancellationToken ct = default);
}

public sealed class DedupService : IDedupService
{
    /// <summary>
    /// How many files the local scan hashes at once. A local scan writes
    /// nothing, so the serial walk that the catalog path needs (one download on
    /// disk at a time) has no reason to apply here: each file is an independent
    /// decode, and leaving a modern machine's cores idle was most of a folder
    /// scan's wall clock. Capped below the core count because libvips already
    /// threads a single large decode internally — oversubscribing only adds
    /// cache and memory pressure. Internal and settable so the throughput test
    /// measures the trade-off instead of assuming it.
    /// </summary>
    internal static int LocalHashingParallelism { get; set; } =
        Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>
    /// Local scan: hash every path, then group. The per-file loop is the
    /// shared <see cref="WorkflowRunner"/>, so progress and error isolation
    /// behave exactly as they do on the Daminion scan and the other routes —
    /// but with <see cref="LocalHashingParallelism"/> workers, because a local
    /// scan has nothing to serialise (see that property for why the catalog
    /// scan does).
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
            MaxDegreeOfParallelism: LocalHashingParallelism,
            Description: $"hashing {algo} at threshold {opts.Threshold:0.###}");

        // The runner reports at item start and completion; only the completion
        // count moves the DedupProgress the UI renders (same as the old loop's
        // per-file report — the duplicate start-line values are harmless).
        var runnerProgress = progress is null
            ? null
            : new Progress<ProcessProgress>(p => progress.Report(new DedupProgress(p.Processed, p.Total, 0)));

        await new WorkflowRunner().RunItemsAsync(
            workflow, items, new LocalHashHandler(opts, hashes), runnerProgress, log: null, pause: null, ct).ConfigureAwait(false);

        // The workers finish in whatever order the decoder gets to them, so the
        // hash map's order is not the scan's order. Group order follows that
        // order, and a review list whose cards move between two scans of the same
        // folder is a bug — so hand the grouper the file order it was given, which
        // is exactly what a serial walk produced.
        var ordered = new Dictionary<string, ulong>(hashes.Count);
        foreach (var path in paths)
            if (hashes.TryGetValue(path, out var hash)) ordered[path] = hash;

        var result = new DedupResult
        {
            TotalFiles = paths.Length,
            Algorithm = algo,
            Threshold = opts.Threshold,
            // Kept after grouping so the results page can re-group at another
            // threshold without re-reading a single file.
            PerceptualHashes = ordered,
        };
        GroupDuplicates(ordered, opts, result.Groups);

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
            PerceptualHashes = perceptualHashes,
            ExactHashes = exactHashes,
        };
        GroupDuplicates(perceptualHashes, opts, result.Groups);
        if (opts.ServerHashMatch == ServerHashMatchMode.Hamming)
            // The server hash is 32 bits wide, so its budget and its scores are
            // measured against 32, not the perceptual fingerprint's 64.
            GroupDuplicates(exactHashes, opts, result.Groups, hashType: "server-hash", bits: 32);
        else
            GroupExact(exactHashes, result.Groups);
        return result;
    }

    public async Task<bool> ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, string? moveDestination = null, CancellationToken ct = default)
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
                        var dupDir = moveDestination;
                        if (string.IsNullOrWhiteSpace(dupDir))
                        {
                            // Backwards-compatible default: a "duplicates"
                            // folder next to the file, as before a destination
                            // picker existed. Now only a fallback.
                            dupDir = Path.Combine(Path.GetDirectoryName(item) ?? ".", "duplicates");
                        }
                        Directory.CreateDirectory(dupDir);
                        var dest = Path.Combine(dupDir, Path.GetFileName(item));
                        // Name collisions inside the destination get a numeric
                        // suffix (photo.jpg, photo (2).jpg): a later duplicate
                        // with the same name must never silently overwrite one
                        // this run already moved.
                        var unique = dest;
                        var stem = Path.GetFileNameWithoutExtension(dest);
                        var ext = Path.GetExtension(dest);
                        for (var n = 2; File.Exists(unique); n++)
                            unique = Path.Combine(dupDir!, $"{stem} ({n}){ext}");
                        File.Move(item, unique, overwrite: false);
                        SynapicLog.Info(nameof(DedupService), $"Moved duplicate: {item} → {unique}");
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

    public async Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, string? moveDestination = null, CancellationToken ct = default)
    {
        var ok = true;
        foreach (var group in result.Groups)
        {
            var targets = group.Items
                .Where(i => !string.Equals(i, group.KeepItem, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ok &= await ApplyToPathsAsync(targets, action, moveDestination, ct).ConfigureAwait(false);
        }
        return ok;
    }

    // ── Hash computation (imagehash-compatible) ─────────────────────────────

    /// <summary>Compute a 64-bit perceptual hash with libvips acceleration.</summary>
    /// <remarks>
    /// The fingerprint has to describe the picture a person sees, so the pixel
    /// layout is normalised before anything is measured:
    /// <list type="bullet">
    /// <item><b>Orientation.</b> EXIF orientation is applied. Without it every
    /// phone portrait photo is fingerprinted from its sideways sensor buffer, so
    /// a copy whose pixels were baked upright (an export, or the same shot
    /// imported twice) scores ~0.5 similarity — indistinguishable from an
    /// unrelated image — even though the two files look identical.</item>
    /// <item><b>Alpha.</b> Transparency is composited onto white. libvips keeps
    /// the alpha band through <c>Colourspace(Bw)</c> (a 4-band PNG becomes 2
    /// bands), and the readers below index the buffer as one byte per pixel:
    /// they sampled an interleaved grey/alpha buffer, scoring the same picture
    /// 32 bits apart, and pHash ran off the end of its 32×32 buffer and returned
    /// no hash at all. Transparency is not visual content, and hidden pixels
    /// under a transparent area are often garbage, so compositing is both
    /// faithful and stable.</item>
    /// <item><b>Size.</b> The working copy is capped at
    /// <see cref="DedupOptions.MaxDimension"/> — the documented "resize before
    /// hash" — and each algorithm then scales down to its own 8×8 / 32×32 grid.
    /// An image already inside the cap is left alone rather than upscaled.</item>
    /// </list>
    /// </remarks>
    public static ulong? ComputeHash(string path, DedupOptions opts)
    {
        try
        {
            using var image = Image.NewFromFile(path, access: Enums.Access.Sequential);
            using var oriented = image.Autorot();
            using var opaque = oriented.HasAlpha()
                ? oriented.Flatten(background: new double[] { 255 })
                : oriented;

            // Even after flattening, take the luminance band explicitly: a
            // greyscale conversion can still leave more than one band, and the
            // readers below assume one byte per pixel.
            using var gray = opaque.Colourspace(Enums.Interpretation.Bw).ExtractBand(0);

            var cap = Math.Clamp(opts.MaxDimension, 64, 512);
            var longest = Math.Max(gray.Width, gray.Height);
            using var working = longest > cap ? gray.Resize(cap / (double)longest) : gray;

            return opts.Algorithm switch
            {
                HashAlgorithm.PHash => ComputePHash(working),
                HashAlgorithm.DHash => ComputeDHash(working),
                HashAlgorithm.AHash => ComputeAHash(working),
                _ => ComputePHash(working),
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

    /// <summary>
    /// Cosine table for the 32-point DCT axis, only the eight low-frequency
    /// terms pHash reads. The transform keeps its top-left 8×8 block, so the
    /// other 24 frequencies of each axis are never sampled.
    /// </summary>
    private static readonly double[,] PhashCos = BuildPhashCos();

    private static double[,] BuildPhashCos()
    {
        const int n = 32, kept = 8;
        var table = new double[n, kept];
        for (var x = 0; x < n; x++)
        {
            for (var u = 0; u < kept; u++)
                table[x, u] = Math.Cos((2.0 * x + 1.0) * u * Math.PI / (2.0 * n));
        }
        return table;
    }

    private static ulong ComputePHash(Image gray)
    {
        // 32x32 grayscale, 2D DCT, top-left 8x8 → median threshold (imagehash phash).
        //
        // The DCT is separable and only 64 of its 1024 coefficients are used, so
        // it is computed as 32 row transforms of 8 terms followed by 8 column
        // transforms of 8 terms — about 10,000 multiply-adds, where transforming
        // the full 32x32 block costs about 1,000,000 for coefficients the hash
        // then throws away. Same coefficients, same threshold; measured at 7x
        // less time for this stage (0.096 → 0.013 ms per hash), which is a small
        // share of a scan next to decoding the file.
        using var small = gray.Resize(32.0 / gray.Width, vscale: 32.0 / gray.Height);
        var pixels = small.WriteToMemory<byte>();

        // Pass 1: for every row, the 8 low-frequency horizontal coefficients.
        var rows = new double[32 * 8];
        for (var y = 0; y < 32; y++)
        {
            var offset = y * 32;
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var x = 0; x < 32; x++)
                    sum += pixels[offset + x] * PhashCos[x, u];
                rows[y * 8 + u] = sum;
            }
        }

        // Pass 2: combine those rows vertically into the 8x8 block.
        var flats = new double[64];
        for (var v = 0; v < 8; v++)
        {
            var cv = v == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var y = 0; y < 32; y++)
                    sum += rows[y * 8 + u] * PhashCos[y, v];
                var cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                flats[v * 8 + u] = 0.25 * cu * cv * sum;
            }
        }

        // Exclude the DC term like imagehash does (flatten()[1:] after median
        // over the 8x8 block minus first element).
        //
        // Known limit, deliberately not papered over: in a picture with almost no
        // structure (a solid colour, a blank scan) most of these coefficients are
        // mathematically zero, so which side of the median each lands on is
        // rounding noise and the fingerprint is meaningless. Snapping those ties
        // to one side was tried and rejected — it gives every uniform image the
        // same fingerprint, which offers unrelated blank files to the user as
        // duplicates. Rounding noise is the lesser evil: identical pixels still
        // produce an identical hash, so real copies are still found, while two
        // different featureless images stay apart.
        var median = Median(flats.Skip(1).ToArray());
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
            if (flats[i] > median) hash |= 1UL << (63 - i);
        return hash;
    }

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // ── Grouping (Union-Find port of dedup_engine.py) ───────────────────────

    /// <summary>
    /// Group by hamming radius: two fingerprints inside the threshold join the
    /// same component, and membership is transitive (A–B and B–C is one group
    /// even when A and C are further apart than the threshold).
    /// </summary>
    /// <remarks>
    /// The comparison is expressed as a graph, not as a pairwise sweep. Written
    /// as the obvious double loop it is n²/2 popcounts — 125 million at 20,000
    /// fingerprints, 1.25 billion at 50,000 — and it is re-run on every move of
    /// the results page's threshold slider. Two structural properties make that
    /// unnecessary without changing a single group:<br/>
    /// <b>Identical fingerprints</b> are one component by inspection, so they are
    /// bucketed by value and never compared. This is also what makes a folder of
    /// exact copies cheap; pairwise unioning made every equal pair a union, and
    /// the parent chain grew one link per pair.<br/>
    /// <b>Near-duplicates</b> are found by pigeonhole: split the fingerprint into
    /// threshold+1 disjoint bit groups. Two values at distance d differ in at
    /// least one bit of at least one group, so if they are within d at least one
    /// group's bits must match exactly. Bucketing on each group in turn and
    /// checking only the pairs that share a bucket cannot miss a pair inside the
    /// radius — the candidates are a superset of the true pairs, and every
    /// candidate is still measured with the real distance. On a catalog-scale set
    /// that is a few thousand comparisons instead of billions.
    /// </remarks>
    private static void GroupDuplicates(
        IReadOnlyDictionary<string, ulong> hashes,
        DedupOptions opts,
        List<DuplicateGroup> groups,
        string? hashType = null,
        int bits = 64)
    {
        var items = hashes.Keys.ToArray();
        if (items.Length < 2) return;

        // Normalize hamming distance to a 0-1 similarity; threshold 0.90 → ≤6 bits differ.
        var maxDistance = MaxHammingDistance(opts.Threshold, bits);

        // Hoist the hash values into an array: every inner loop below indexes it.
        var values = new ulong[items.Length];
        for (var i = 0; i < items.Length; i++) values[i] = hashes[items[i]];

        var components = new UnionFind(items.Length);

        // 1. Identical fingerprints — one component, no distance work at all.
        //    `distinct` holds one representative item index per distinct value;
        //    only those representatives take part in the near-duplicate search.
        var distinct = new List<int>(items.Length);
        var firstWithValue = new Dictionary<ulong, int>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            if (firstWithValue.TryGetValue(values[i], out var first))
            {
                components.Union(i, first);
            }
            else
            {
                firstWithValue[values[i]] = i;
                distinct.Add(i);
            }
        }

        // 2. Near-duplicates — pigeonhole candidates, then the real distance.
        if (maxDistance > 0 && distinct.Count > 1)
        {
            if (maxDistance >= bits)
            {
                // Every fingerprint is inside the radius of every other one.
                for (var i = 1; i < distinct.Count; i++) components.Union(distinct[0], distinct[i]);
            }
            else
            {
                var distinctValues = new ulong[distinct.Count];
                for (var i = 0; i < distinct.Count; i++) distinctValues[i] = values[distinct[i]];
                var nextInBucket = new int[distinct.Count];

                foreach (var mask in BitGroupMasks(distinctValues, bits, maxDistance + 1))
                {
                    // Bucket headers + an intrusive chain, one group at a time: a
                    // List per bucket would allocate one list per distinct
                    // fingerprint per group (140,000 of them at catalog scale)
                    // and dominate the grouping cost it exists to avoid.
                    var head = new Dictionary<ulong, int>(distinctValues.Length);
                    Array.Fill(nextInBucket, -1);
                    for (var i = 0; i < distinctValues.Length; i++)
                    {
                        var key = distinctValues[i] & mask;
                        if (head.TryGetValue(key, out var first))
                        {
                            nextInBucket[i] = first;
                            head[key] = i;
                        }
                        else
                        {
                            head[key] = i;
                        }
                    }

                    foreach (var start in head.Values)
                    {
                        if (nextInBucket[start] < 0) continue; // single value in the bucket
                        for (var x = start; x >= 0; x = nextInBucket[x])
                        {
                            var a = distinct[x];
                            for (var y = nextInBucket[x]; y >= 0; y = nextInBucket[y])
                            {
                                var b = distinct[y];
                                if (HammingDistance(values[a], values[b]) <= maxDistance)
                                    components.Union(a, b);
                            }
                        }
                    }
                }
            }
        }

        // 3. Components → groups, in the order each component first appears in
        //    the item list (what the previous GroupBy produced), with members
        //    ordered by path.
        var memberLists = new List<List<int>>();
        var listByRoot = new Dictionary<int, int>();
        for (var i = 0; i < items.Length; i++)
        {
            var root = components.Find(i);
            if (!listByRoot.TryGetValue(root, out var listIndex))
            {
                listByRoot[root] = listIndex = memberLists.Count;
                memberLists.Add(new List<int>(2));
            }
            memberLists[listIndex].Add(i);
        }

        foreach (var members in memberLists)
        {
            if (members.Count < 2) continue;

            var groupItems = members
                .Select(i => items[i])
                .OrderBy(p => p)
                .ToArray();
            var representative = groupItems[0];
            var scores = groupItems
                .Select(p => 1.0 - HammingDistance(hashes[p], hashes[representative]) / (double)bits)
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
    /// The pigeonhole partition as bit masks: <paramref name="groups"/> disjoint
    /// groups of bit positions. Bit positions are dealt round-robin in order of
    /// how evenly each bit splits this set, so every group carries a similar
    /// amount of information. A partition of arbitrary (contiguous) bit ranges
    /// would work too, but on real fingerprints some ranges are far more uniform
    /// than others — a group whose bits are almost always the same puts a large
    /// share of the catalog in one bucket and hands that share back to pairwise
    /// comparison. Balancing the groups keeps the candidate sets small.
    /// </summary>
    private static ulong[] BitGroupMasks(ulong[] values, int bits, int groups)
    {
        var ones = new int[bits];
        foreach (var value in values)
        {
            for (var bit = 0; bit < bits; bit++)
                if ((value & (1UL << bit)) != 0) ones[bit]++;
        }

        var count = values.Length;
        var order = Enumerable.Range(0, bits)
            .OrderBy(bit => Math.Abs(2L * ones[bit] - count)) // most evenly split first
            .ThenBy(bit => bit)
            .ToArray();

        var masks = new ulong[groups];
        for (var i = 0; i < order.Length; i++)
            masks[i % groups] |= 1UL << order[i];
        return masks;
    }

    /// <summary>
    /// Disjoint-set forest with path halving and union by size. Union by size is
    /// not cosmetic: the previous implementation always attached one root to the
    /// other, so a set that grew one pair at a time became a linked list and
    /// every Find walked it — quadratic in a set that is really one component.
    /// </summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int count)
        {
            _parent = new int[count];
            _size = new int[count];
            for (var i = 0; i < count; i++)
            {
                _parent[i] = i;
                _size[i] = 1;
            }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b) return;
            if (_size[a] < _size[b]) (a, b) = (b, a);
            _parent[b] = a;
            _size[a] += _size[b];
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
    ///
    /// <paramref name="bits"/> is the fingerprint's width, not always 64: the
    /// Daminion server hash is a signed 32-bit value (every observed value fits
    /// <c>int</c>), so a budget derived from 64 bits would allow twice the share
    /// of the hash a threshold promises.
    /// </summary>
    public static int MaxHammingDistance(double threshold, int bits = 64) =>
        (int)Math.Round(bits * (1.0 - threshold));
}
