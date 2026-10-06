using System.IO;
using System.Linq;
using Synapic.Avalonia.Services.Daminion;

namespace Synapic.Avalonia.Services.Processing;

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
}

/// <summary>
/// The deduplication workflow's per-item operation for a Daminion source.
/// For items that carry a server-computed hashCode, the item is grouped directly
/// from that hash — no download, no algorithmic hash. For items without a
/// server hash (older server builds, or scoped queries that omit it), the
/// original is downloaded and hashed the old way. One item at a time keeps a
/// large scope from landing on disk whole. Failures are counted + logged rather
/// than thrown, because a missing or unhashable file only shrinks the comparison
/// set; it never fails the scan.
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

    /// <summary>Hashes keyed "daminion:{id}" — input to GroupFromHashes.</summary>
    /// For items with a server hashCode the value is the hashCode widened to ulong;
    /// for items that were downloaded and algorithmically hashed it is the
    /// perceptual hash. Either way the key space is the same and GroupFromHashes
    /// groups by hamming distance across the mixed set.
    public Dictionary<string, ulong> Hashes { get; } = new();

    /// <summary>Row data per hashed item, consumed by the review UI.</summary>
    public Dictionary<string, DedupScanRecord> Records { get; } = new();

    // Counters are bumped from the runner's worker threads, so they are updated
    // with Interlocked rather than a plain ++/read. (Dedup runs one item at a
    // time today, but a public handler must not silently lose counts if its
    // parallelism is ever raised.)
    private int _downloadFailures;
    private int _hashFailures;
    private int _serverHashGrouped;

    /// <summary>Items whose original could not be downloaded (skipped).</summary>
    public int DownloadFailures => Volatile.Read(ref _downloadFailures);

    /// <summary>Items whose downloaded bytes could not be decoded/hashed (skipped).</summary>
    public int HashFailures => Volatile.Read(ref _hashFailures);

    /// <summary>Items grouped purely from the server hashCode (no download).</summary>
    public int ServerHashGrouped => Volatile.Read(ref _serverHashGrouped);

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
        if (item.ServerHashCode is { } serverHash && serverHash != 0)
        {
            var key = $"daminion:{id}";
            lock (Hashes)
            {
                Hashes[key] = (ulong)serverHash;
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

        // No server hash available — fall back to the old path: download the
        // original, compute a perceptual hash, delete the temp file.
        SynapicLog.Debug(nameof(DedupScanHandler),
            $"Dedup: item {id} ({item.FileName}) → no server hash, downloading original");

        var temp = await _downloader.DownloadOriginalAsync(id, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(temp) || !File.Exists(temp))
        {
            Interlocked.Increment(ref _downloadFailures);
            SynapicLog.Warning(nameof(DedupScanHandler),
                $"Dedup: no original downloaded for item {id} ({item.FileName}) — skipped " +
                "(see any 'Failed to download original' line above)");
            return WorkflowItemOutcome.Fail("no original downloaded");
        }

        try
        {
            var info = new FileInfo(temp);
            SynapicLog.Debug(nameof(DedupScanHandler),
                $"Dedup: item {id} ({item.FileName}) → downloaded {info.Length:N0} bytes, computing hash");

            var hash = _dedup.ComputeHash(temp, _options);
            if (hash is null)
            {
                Interlocked.Increment(ref _hashFailures);
                SynapicLog.Warning(nameof(DedupScanHandler),
                    $"Dedup: could not hash item {id} ({item.FileName}, {info.Length:N0} bytes) — skipped; " +
                    "see the 'Could not hash' line for the decode error");
                return WorkflowItemOutcome.Fail("could not hash");
            }

            var key = $"daminion:{id}";
            lock (Hashes)
            {
                Hashes[key] = hash.Value;
                Records[key] = new DedupScanRecord(
                    key,
                    item.FileName ?? $"Item {id}",
                    info.Length,
                    // The temp file's own timestamps are download-time; the EXIF
                    // date inside the original is the only meaningful one.
                    DedupService.ReadImageDateUtc(temp) ?? info.CreationTimeUtc,
                    id);
            }

            // Diagnostic: log the algorithmic hash result for comparison with server hash.
            SynapicLog.Debug(nameof(DedupScanHandler),
                $"Dedup: item {id} ({item.FileName}) → algorithm {_options.Algorithm} hash {hash.Value:X16}");

            return WorkflowItemOutcome.Ok;
        }
        finally
        {
            try { File.Delete(temp); }
            catch { /* temp cleanup is best effort */ }
        }
    }

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
    /// - If server hashes are exact: each unique file will have a unique hashCode,
    ///   and groups will only form when the algorithmic hash finds perceptual
    ///   similarity among items with different server hashes.
    /// - The "Items requiring algorithmic hash" count tells you how many items
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
            $"Downloaded + algorithmically hashed: {Records.Count - ServerHashGrouped}");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Download failures: {DownloadFailures}");
        SynapicLog.Info(nameof(DedupScanHandler),
            $"Hash failures: {HashFailures}");

        // HashCode distribution. Materialize every bucket before slicing: the
        // "unique hashes" count below drives the collision warning, so it must
        // not be limited to the handful of buckets actually printed.
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

            // Diagnostic: if there are fewer unique hashes than items, the server
            // hash is grouping different files together. This could mean it's
            // perceptual (good for dedup) or that different files happen to have
            // the same hash (collision — bad). The group sizes help distinguish:
            // large groups of items that are known to be different files suggest
            // either perceptual hashing or collisions.
            if (uniqueServerHashes < totalServerHashed)
            {
                SynapicLog.Warning(nameof(DedupScanHandler),
                    $"\nWARNING: {totalServerHashed - uniqueServerHashes} items share a server hash with another item.");
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
