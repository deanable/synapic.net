using System.IO;
using System.Linq;
using Synapic.Core.Services.Daminion;

using Synapic.Core.Services;
namespace Synapic.Core.Services.Processing;

/// <summary>What a Daminion dedup scan learned about one item (the row data
/// the review UI needs once grouping finishes).</summary>
/// <param name="Key">"daminion:{id}" — the same identity the groups use.</param>
/// <param name="FileName">Catalog file name for display.</param>
/// <param name="SizeBytes">The original's size: the downloaded file's length on
/// the hash path, the server-reported <c>fileSize</c> on the server-hash path,
/// or 0 when neither is known.</param>
/// <param name="DateTakenUtc">EXIF capture date when present, else file creation time (null when unknown).</param>
/// <param name="DaminionId">Catalog item id.</param>
/// <param name="ServerHashCode">The Daminion server's content hash for this item,
/// when available. When this is non-zero the item was grouped from the server hash
/// without a download; when 0 the item went through the download-and-algorithmic-hash path.</param>
public sealed record DedupScanRecord(
    string Key,
    string FileName,
    long SizeBytes,
    DateTime? DateTakenUtc,
    int DaminionId,
    long ServerHashCode = 0);

/// <summary>
/// Abstraction over the Daminion download capability needed by the dedup scan.
/// This exists so the scan handler can be tested without a real server connection
/// — the production code passes the real <see cref="DaminionApiClient"/>, tests
/// pass a fake.
/// </summary>
public interface IDaminionDownloader
{
    /// <summary>Download the original file for an item to a temp location.</summary>
    /// <returns>The temp file path, or null when the download failed.</returns>
    Task<string?> DownloadOriginalAsync(int itemId, CancellationToken ct = default);

    /// <summary>
    /// Download a scaled rendering of the item (the server's preview cache) to a
    /// temp location, or null when the server has none.
    /// </summary>
    /// <remarks>
    /// Hashing does not need the original. Every fingerprint here is a 32×32
    /// (pHash) / 9×8 (dHash) / 8×8 (aHash) grid, so anything above ~64 px is
    /// already more pixels than the comparison ever sees. Measured on a live
    /// catalog of 30 originals (2.4–6.6 MP, 0.9–6.5 MB), hashing the server's
    /// ~2000 px preview produced a pHash identical to hashing the original on 29
    /// of 30 items and 2 bits apart on the other, at 107 ms/item instead of
    /// 413 ms and 748 KB instead of 3341 KB.
    /// </remarks>
    Task<string?> DownloadPreviewAsync(int itemId, int width, int height, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IDaminionDownloader"/> implementation that delegates to a real
/// <see cref="DaminionApiClient"/>.
/// </summary>
internal sealed class DaminionApiClientDownloader : IDaminionDownloader
{
    private readonly DaminionApiClient _client;

    public DaminionApiClientDownloader(DaminionApiClient client) => _client = client;

    public async Task<string?> DownloadOriginalAsync(int itemId, CancellationToken ct = default)
        => await _client.DownloadOriginalAsync(itemId, ct).ConfigureAwait(false);

    public async Task<string?> DownloadPreviewAsync(int itemId, int width, int height, CancellationToken ct = default)
        => await _client.DownloadPreviewAsync(itemId, width, height, ct).ConfigureAwait(false);
}

/// <summary>
/// The deduplication workflow's per-item operation for a Daminion source.
/// For items that carry a server-computed hashCode, the item is grouped directly
/// from that hash — no transfer at all. For items without a server hash (older
/// server builds, scoped queries that omit it, or the similarity profile, which
/// always hashes pixels), the item is fingerprinted from the server's *preview*
/// rather than its original: a fingerprint is a 32×32 grid, so the preview holds
/// far more pixels than the comparison ever reads, and on a live catalog of 30
/// stills it produced exactly the same pHash as the original on 29 of them and a
/// 2-bit difference on the last — at a third of the time and a quarter of the
/// bytes. The original remains the fallback for a server that renders no preview,
/// an item whose dimensions the catalog did not report, and a preview that cannot
/// be decoded. One item at a time keeps a large scope from landing on disk whole.
/// Failures are counted + logged rather than thrown, because a missing or
/// unhashable file only shrinks the comparison set; it never fails the scan.
///
/// Server hashes and algorithmic hashes are kept in separate dictionaries
/// (<see cref="ExactHashes"/> / <see cref="PerceptualHashes"/>): they are
/// different hash functions, so each set is grouped by its own rule and the two
/// are never compared with each other.
/// </summary>
public sealed class DedupScanHandler : IWorkflowItemHandler
{
    private readonly IDaminionDownloader _downloader;
    private readonly DedupOptions _options;
    private readonly IDedupService _dedup;

    /// <summary>
    /// Create a scan handler. In production, pass a <see cref="DaminionApiClient"/>
    /// (wrapped in <see cref="DaminionApiClientDownloader"/>); in tests, pass a fake.
    /// </summary>
    public DedupScanHandler(IDaminionDownloader downloader, DedupOptions options, IDedupService dedup)
    {
        _downloader = downloader;
        _options = options;
        _dedup = dedup;
    }

    /// <summary>
    /// Convenience overload that wraps a <see cref="DaminionApiClient"/> for
    /// production use (the StepDedupViewModel path).
    /// </summary>
    public DedupScanHandler(DaminionApiClient client, DedupOptions options, IDedupService dedup)
        : this(new DaminionApiClientDownloader(client), options, dedup)
    {
    }

    // One lock for every mutable map below: the runner's worker threads call
    // ProcessAsync concurrently (dedup currently runs one item at a time, but
    // the handler must stay correct if that bound is ever raised).
    private readonly object _hashesLock = new();

    /// <summary>
    /// Algorithmic (perceptual) hashes for items that were downloaded and
    /// hashed, keyed "daminion:{id}" — grouped by the threshold's hamming
    /// distance, like a local scan.
    /// </summary>
    public Dictionary<string, ulong> PerceptualHashes { get; } = new();

    /// <summary>
    /// Server content hashes for items the catalog already hashed, keyed
    /// "daminion:{id}" — grouped among themselves by
    /// <see cref="DedupOptions.ServerHashMatch"/>. Deliberately a separate map:
    /// these values are not comparable with the perceptual hashes above.
    /// </summary>
    public Dictionary<string, ulong> ExactHashes { get; } = new();

    /// <summary>Row data per hashed item, consumed by the review UI.</summary>
    public Dictionary<string, DedupScanRecord> Records { get; } = new();

    // Counters are bumped from the runner's worker threads, so they are updated
    // with Interlocked rather than a plain ++/read. (Dedup runs one item at a
    // time today, but a public handler must not silently lose counts if its
    // parallelism is ever raised.)
    private int _downloadFailures;
    private int _hashFailures;
    private int _serverHashGrouped;
    private int _previewHashed;
    private int _previewFailures;

    /// <summary>Items whose original could not be downloaded (skipped).</summary>
    public int DownloadFailures => Volatile.Read(ref _downloadFailures);

    /// <summary>Items whose downloaded bytes could not be decoded/hashed (skipped).</summary>
    public int HashFailures => Volatile.Read(ref _hashFailures);

    /// <summary>Items grouped purely from the server hashCode (no download).</summary>
    public int ServerHashGrouped => Volatile.Read(ref _serverHashGrouped);

    /// <summary>Items fingerprinted from the server's preview (no original).</summary>
    public int PreviewHashed => Volatile.Read(ref _previewHashed);

    /// <summary>Items whose preview could not be fetched, so the original was read.</summary>
    public int PreviewFailures => Volatile.Read(ref _previewFailures);

    public async Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct)
    {
        if (item.DaminionId is not { } id)
            return WorkflowItemOutcome.Fail("not a Daminion item");

        // When the server already computed a content hash for this item, use it
        // directly. Two files with the same hashCode are identical at ingest time,
        // so they are duplicates without any download or algorithmic hashing.
        // This is the cheap path the user asked for: it removes the
        // download + perceptual-hash cost for every item whose server hash is
        // present (the common case on Daminion 11.x).
        //
        // The similarity profile turns the shortcut off (UseServerHashes=false):
        // a content hash answers "is this the same file", not "does this look
        // alike", and reusing it there silently replaced the perceptual
        // comparison with exact matching — so every catalog scan reported almost
        // no similar images whatever the algorithm and threshold said.
        if (item.ServerHashCode is { } serverHash && serverHash != 0 && _options.UseServerHashes)
        {
            var key = $"daminion:{id}";
            // The server's hash is a signed 32-bit number (every observed value
            // fits int, and roughly half are negative). Zero-extend it: casting
            // long → ulong sign-extends, which puts 32 guaranteed-equal high bits
            // on every negative value and 32 guaranteed-different high bits
            // between a negative and a positive. Hamming mode then compared the
            // sign padding instead of the hash, which is how a page of unrelated
            // items produced three "similar" groups.
            var contentHash = unchecked((ulong)(uint)serverHash);
            lock (_hashesLock)
            {
                ExactHashes[key] = contentHash;
                Records[key] = new DedupScanRecord(
                    key,
                    item.FileName ?? $"Item {id}",
                    item.SizeBytes, // server-reported size; 0 when the server omitted it
                    item.DateTakenUtc,
                    id,
                    serverHash);
            }
            Interlocked.Increment(ref _serverHashGrouped);

            // Diagnostic: log each item that was grouped from server hash.
            // This is verbose but essential for the live-server verification —
            // it lets us see the hashCode distribution and spot patterns.
            SynapicLog.Debug(nameof(DedupScanHandler),
                $"Dedup: item {id} ({item.FileName}) → server hash {serverHash} (grouped without download)");

            return WorkflowItemOutcome.Ok;
        }

        // No usable server hash — fingerprint the item. The original is not
        // needed for that: every fingerprint is a ≤32×32 grid, so the server's
        // own preview (~2000 px) carries far more detail than the comparison
        // looks at, at a quarter of the bytes and roughly a quarter of the time.
        // The original is still the fallback — for a server that renders no
        // preview, and for an item whose dimensions the catalog did not report
        // (a preview request needs a box to fit the picture into).
        SynapicLog.Debug(nameof(DedupScanHandler),
            item.ServerHashCode is { } skipped && skipped != 0
                ? $"Dedup: item {id} ({item.FileName}) → server hash {skipped} ignored for this scan, hashing a preview"
                : $"Dedup: item {id} ({item.FileName}) → no server hash, hashing a preview");

        var box = PreviewBoxFor(item);
        string? previewPath = null;
        string? originalPath = null;
        ulong? hash = null;
        try
        {
            if (box.W > 0)
            {
                previewPath = await _downloader
                    .DownloadPreviewAsync(id, box.W, box.H, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath))
                {
                    Interlocked.Increment(ref _previewFailures);
                    SynapicLog.Warning(nameof(DedupScanHandler),
                        $"Dedup: no preview for item {id} ({item.FileName}) — hashing the original instead");
                    previewPath = null;
                }
                else
                {
                    hash = _dedup.ComputeHash(previewPath, _options);
                    if (hash is null)
                    {
                        // A preview that cannot be decoded is this item's problem,
                        // not a reason to lose it from the scan: the original is
                        // still there and is what the old path would have used.
                        SynapicLog.Warning(nameof(DedupScanHandler),
                            $"Dedup: could not hash the preview of item {id} ({item.FileName}) — trying the original");
                        try { File.Delete(previewPath); } catch { /* best effort */ }
                        previewPath = null;
                    }
                }
            }

            if (hash is null)
            {
                originalPath = await _downloader.DownloadOriginalAsync(id, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath))
                {
                    Interlocked.Increment(ref _downloadFailures);
                    SynapicLog.Warning(nameof(DedupScanHandler),
                        $"Dedup: no original downloaded for item {id} ({item.FileName}) — skipped " +
                        "(see any 'Failed to download' line above)");
                    return WorkflowItemOutcome.Fail("no original downloaded");
                }

                hash = _dedup.ComputeHash(originalPath, _options);
                if (hash is null)
                {
                    Interlocked.Increment(ref _hashFailures);
                    SynapicLog.Warning(nameof(DedupScanHandler),
                        $"Dedup: could not hash item {id} ({item.FileName}) — skipped; " +
                        "see the 'Could not hash' line for the decode error");
                    return WorkflowItemOutcome.Fail("could not hash");
                }
            }

            var usedPath = previewPath ?? originalPath!;
            var info = new FileInfo(usedPath);
            var fromPreview = previewPath is not null;
            if (fromPreview) Interlocked.Increment(ref _previewHashed);

            var key = $"daminion:{id}";
            lock (_hashesLock)
            {
                PerceptualHashes[key] = hash.Value;
                Records[key] = new DedupScanRecord(
                    key,
                    item.FileName ?? $"Item {id}",
                    // A preview's own length says nothing about the file the user
                    // is keeping, so the catalog's fileSize is the honest number.
                    fromPreview ? item.SizeBytes : info.Length,
                    // A preview carries no EXIF, so the capture date is not in it;
                    // the review list fills it in from the item's metadata for the
                    // rows that end up in a group. An original still supplies it
                    // here, as before.
                    fromPreview
                        ? item.DateTakenUtc
                        : DedupService.ReadImageDateUtc(usedPath) ?? info.CreationTimeUtc,
                    id);
            }

            SynapicLog.Debug(nameof(DedupScanHandler),
                $"Dedup: item {id} ({item.FileName}) → {_options.Algorithm} hash {hash.Value:X16} " +
                $"from {(fromPreview ? $"preview {info.Length:N0} bytes" : $"original {info.Length:N0} bytes")}");

            return WorkflowItemOutcome.Ok;
        }
        finally
        {
            foreach (var temp in new[] { previewPath, originalPath })
            {
                if (string.IsNullOrEmpty(temp)) continue;
                try { File.Delete(temp); }
                catch { /* temp cleanup is best effort */ }
            }
        }
    }

    /// <summary>
    /// The box to request a preview in: the catalog's own aspect ratio scaled to
    /// <see cref="PreviewLongEdge"/>. Zero dimensions mean "unknown" and the
    /// caller falls back to the original — asking for a preview without them
    /// would make <c>DownloadPreviewAsync</c> look the size up in a second round
    /// trip, which for a whole catalog is more calls than the scan is trying to
    /// avoid.
    /// </summary>
    private static (int W, int H) PreviewBoxFor(ProcessWorkItem item)
    {
        var w = item.Width;
        var h = item.Height;
        if (w <= 0 || h <= 0) return (0, 0);
        return w >= h
            ? (PreviewLongEdge, Math.Max(1, (int)Math.Round(PreviewLongEdge * (double)h / w)))
            : (Math.Max(1, (int)Math.Round(PreviewLongEdge * (double)w / h)), PreviewLongEdge);
    }

    /// <summary>
    /// Long edge of the preview requested for hashing. 512 is what
    /// <see cref="DedupOptions.MaxDimension"/> already caps the working copy at,
    /// so nothing above it could reach the fingerprint anyway; the server's own
    /// preview cache is ~2000 px and is returned as-is for smaller requests.
    /// </summary>
    private const int PreviewLongEdge = 512;

    /// <summary>
    /// Log a detailed diagnostic summary of the scan results. Call this after
    /// the scan completes to understand what happened under the hood: how many
    /// items were grouped from server hash vs downloaded, the hashCode distribution,
    /// and statistics to help determine if the server hashCode is perceptual or exact.
    ///
    /// Key things to look for in the log:
    /// - If server hashes are perceptual: items that are visually similar but not
    ///   byte-identical will share the same hashCode, and you'll see large groups
    ///   with high item counts per hashCode.
    /// - If server hashes are exact: nearly every file has its own hashCode, so
    ///   the exact-match groups are small and mostly pairs of byte-identical
    ///   files.
    /// - The "Downstream + algorithmically hashed" count tells you how many items
    ///   still needed the expensive download+hash path — if this is 0, the server
    ///   hash covered everything.
    /// </summary>
    public void LogDiagnosticSummary(string scopeDescription)
    {
        SynapicLog.Info(nameof(DedupScanHandler),
            $"=== Dedup Diagnostic Summary ===");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Scope: {scopeDescription}");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Total items processed: {Records.Count}");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Server-hash grouped (no download): {ServerHashGrouped}");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Downloaded + algorithmically hashed: {PerceptualHashes.Count} " +
            $"({PreviewHashed} from the server's preview, " +
            $"{PerceptualHashes.Count - PreviewHashed} from the original, " +
            $"{PreviewFailures} preview failure(s))");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Server-hash comparison: {_options.ServerHashMatch} " +
            (ServerHashGrouped == 0
                ? _options.UseServerHashes
                    ? "(no server hashes in this scope)"
                    : "(not used by this scan — the similarity search hashes pixels)"
                : _options.ServerHashMatch == ServerHashMatchMode.Exact
                    ? "(identical hash values only)"
                    : $"(hamming distance ≤ {DedupService.MaxHammingDistance(_options.Threshold, bits: 32)} of 32 bits)"));
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Download failures: {DownloadFailures}");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Hash failures: {HashFailures}");

        // HashCode distribution. Materialize every bucket before slicing: the
        // "unique hashes" count below drives the sharing line, so it must not be
        // limited to the handful of buckets actually printed.
        var serverHashCounts = Records
            .Where(r => r.Value.ServerHashCode != 0)
            .GroupBy(r => r.Value.ServerHashCode)
            .OrderByDescending(g => g.Count())
            .ToArray();

        if (serverHashCounts.Length > 0)
        {
            SynapicLog.Info(nameof(DedupScanHandler),
                $"\nServer hashCode distribution (top 10 by frequency):");
            foreach (var group in serverHashCounts.Take(10))
            {
                SynapicLog.Info(nameof(DedupScanHandler),
                    $"  hashCode={group.Key}: {group.Count()} item(s)");
            }

            var uniqueServerHashes = serverHashCounts.Length;
            var totalServerHashed = serverHashCounts.Sum(g => g.Count());
            var examplesPerHash = serverHashCounts
                .Where(g => g.Count() > 1)
                .Take(5)
                .Select(g => $"hashCode={g.Key}: [{string.Join(", ", g.Select(r => r.Value.DaminionId))}]");
            if (examplesPerHash.Any())
            {
                SynapicLog.Info(nameof(DedupScanHandler),
                    $"\nExamples of items sharing a server hash:");
                foreach (var example in examplesPerHash)
                    SynapicLog.Info(nameof(DedupScanHandler), $"  {example}");
            }
            SynapicLog.Info(nameof(DedupScanHandler),
                $"\nUnique server hashes: {uniqueServerHashes} across {totalServerHashed} items");

            // Sharing a hash is how groups form, so this is informational, not a
            // warning: the bucket sizes above are what tell us whether the server
            // hash behaves like an exact one (mostly single-item buckets) or a
            // perceptual one (large buckets of visually similar files).
            var shared = totalServerHashed - uniqueServerHashes;
            if (shared > 0)
            {
                SynapicLog.Info(nameof(DedupScanHandler),
                    $"\n{shared} item(s) share a server hash with another item — " +
                    (_options.ServerHashMatch == ServerHashMatchMode.Exact
                        ? "exact matching offers those as groups."
                        : "the hamming rule may also bridge nearby hash values."));
            }
        }

        // Algorithmic hash distribution for downloaded items.
        var algoHashes = Records
            .Where(r => r.Value.ServerHashCode == 0 && r.Value.SizeBytes > 0)
            .Select(r => r.Value)
            .ToList();

        if (algoHashes.Count > 0)
        {
            SynapicLog.Info(nameof(DedupScanHandler),
                $"\nItems requiring algorithmic hash: {algoHashes.Count}");
        }

        // File size stats for downloaded items.
        var downloadedSizes = Records
            .Where(r => r.Value.ServerHashCode == 0 && r.Value.SizeBytes > 0)
            .Select(r => r.Value.SizeBytes)
            .ToArray();

        if (downloadedSizes.Length > 0)
        {
            Array.Sort(downloadedSizes);
            SynapicLog.Info(nameof(DedupScanHandler),
                $"\nDownloaded file sizes: min={downloadedSizes[0]:N0} bytes, " +
                $"max={downloadedSizes[^1]:N0} bytes, " +
                $"median={downloadedSizes[downloadedSizes.Length / 2]:N0} bytes, " +
                $"total={downloadedSizes.Sum():N0} bytes downloaded");
        }

        SynapicLog.Info(nameof(DedupScanHandler),
            $"=== End Diagnostic Summary ===");
    }
}
