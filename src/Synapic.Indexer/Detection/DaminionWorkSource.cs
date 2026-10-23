using Synapic.Core.Services;
using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;

namespace Synapic.Indexer.Detection;

/// <summary>
/// The catalog detector (SCAN-04). It deliberately contains no query logic: it
/// builds the same <see cref="DatasourceSelection"/> the desktop builds and calls
/// <see cref="WorkflowRunner.FetchItemsAsync"/>, so "untagged" cannot mean one
/// thing in the app and another in the service (D-11-17).
/// </summary>
public sealed class DaminionWorkSource : IWorkSource
{
    /// <summary>The fetch seam, so tests never contact a server.</summary>
    public delegate Task<IReadOnlyList<ProcessWorkItem>> FetchItems(
        DatasourceSelection selection, CancellationToken ct);

    private readonly DatasourceSelection _selection;
    private readonly IWorkIntake _intake;
    private readonly FetchItems _fetch;

    public DaminionWorkSource(IndexerConfig config, IWorkIntake intake, FetchItems? fetch = null)
        : this(DaminionSelectionBuilder.Build(config), intake, fetch ?? WorkflowRunner.FetchItemsAsync)
    {
    }

    /// <summary>Test seam: a selection and fetch supplied directly, with no DPAPI involved.</summary>
    internal DaminionWorkSource(DatasourceSelection selection, IWorkIntake intake, FetchItems fetch)
    {
        _selection = selection;
        _intake = intake;
        _fetch = fetch;
    }

    public string Name => "daminion";

    /// <summary>The selection this source queries — read by tests and diagnostics.</summary>
    internal DatasourceSelection Selection => _selection;

    public async Task<SourceScanResult> ScanAsync(CancellationToken ct)
    {
        IReadOnlyList<ProcessWorkItem> items;
        try
        {
            items = await _fetch(_selection, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is DaminionNetworkException or DaminionAuthenticationException
                                      or HttpRequestException or TaskCanceledException)
        {
            // An outage pauses detection, it does not touch the queue: rows
            // already queued stay exactly as they were.
            SynapicLog.Warning(nameof(DaminionWorkSource),
                $"Daminion is unavailable ({e.GetType().Name}: {e.Message}); the queue is unchanged " +
                "and the next interval will retry.");
            return SourceScanResult.UnavailableResult();
        }

        var candidates = new List<JobCandidate>(items.Count);
        var skipped = 0;
        foreach (var item in items)
        {
            if (item.DaminionId is not { } id)
            {
                SynapicLog.Warning(nameof(DaminionWorkSource),
                    $"Catalog item '{item.FileName}' has no id; it cannot be queued.");
                skipped++;
                continue;
            }

            candidates.Add(new JobCandidate(
                PathKey.ForDaminion(id),
                Name,
                Path: null,
                DaminionId: id,
                FileName: item.FileName,
                Size: item.SizeBytes > 0 ? item.SizeBytes : null,
                MtimeUtc: null,
                ContentHash: item.ServerHashCode != 0 ? ContentFingerprint.ServerHash(item.ServerHashCode) : null));
        }

        var intake = await _intake.EnqueueAsync(candidates, ct).ConfigureAwait(false);
        return new SourceScanResult
        {
            Found = candidates.Count + skipped,
            Enqueued = intake.Enqueued,
            Skipped = intake.Skipped + skipped,
        };
    }

    /// <summary>Retained for the detection tests; the mapping lives in the builder now.</summary>
    internal static string NormalizeScope(string? scope) => DaminionSelectionBuilder.NormalizeScope(scope);
}
