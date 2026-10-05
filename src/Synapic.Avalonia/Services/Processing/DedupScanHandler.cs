using Synapic.Avalonia.Services.Daminion;

namespace Synapic.Avalonia.Services.Processing;

/// <summary>What a Daminion dedup scan learned about one item (the row data
/// the review UI needs once grouping finishes).</summary>
/// <param name="Key">"daminion:{id}" — the same identity the groups use.</param>
/// <param name="FileName">Catalog file name for display.</param>
/// <param name="SizeBytes">Downloaded original's size.</param>
/// <param name="DateTakenUtc">EXIF capture date when present, else file creation time.</param>
/// <param name="DaminionId">Catalog item id.</param>
public sealed record DedupScanRecord(
    string Key,
    string FileName,
    long SizeBytes,
    DateTime DateTakenUtc,
    int DaminionId);

/// <summary>
/// The deduplication workflow's per-item operation for a Daminion source
/// (port of the StepDedup scan loop): download the original to a temp file,
/// hash it, record size + EXIF/file date, delete the temp again — one item at
/// a time so a large collection never lands on disk whole. Failures are
/// handled here (counted + logged) rather than thrown, because a missing or
/// unhashable file only shrinks the comparison set; it never fails the scan.
/// </summary>
public sealed class DedupScanHandler : IWorkflowItemHandler
{
    private readonly DaminionApiClient _client;
    private readonly DedupOptions _options;
    private readonly IDedupService _dedup;

    public DedupScanHandler(DaminionApiClient client, DedupOptions options, IDedupService dedup)
    {
        _client = client;
        _options = options;
        _dedup = dedup;
    }

    /// <summary>Hashes keyed "daminion:{id}" — input to GroupFromHashes.</summary>
    public Dictionary<string, ulong> Hashes { get; } = new();

    /// <summary>Row data per hashed item, consumed by the review UI.</summary>
    public Dictionary<string, DedupScanRecord> Records { get; } = new();

    /// <summary>Items whose original could not be downloaded (skipped).</summary>
    public int DownloadFailures { get; private set; }

    /// <summary>Items whose downloaded bytes could not be decoded/hashed (skipped).</summary>
    public int HashFailures { get; private set; }

    public async Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct)
    {
        if (item.DaminionId is not { } id)
            return WorkflowItemOutcome.Fail("not a Daminion item");

        var temp = await _client.DownloadOriginalAsync(id, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(temp) || !File.Exists(temp))
        {
            DownloadFailures++;
            SynapicLog.Warning(nameof(DedupScanHandler),
                $"Dedup: no original downloaded for item {id} ({item.FileName}) — skipped " +
                "(see any 'Failed to download original' line above)");
            return WorkflowItemOutcome.Fail("no original downloaded");
        }

        try
        {
            var info = new FileInfo(temp);
            var hash = _dedup.ComputeHash(temp, _options);
            if (hash is null)
            {
                HashFailures++;
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
            return WorkflowItemOutcome.Ok;
        }
        finally
        {
            try { File.Delete(temp); }
            catch { /* temp cleanup is best effort */ }
        }
    }
}
