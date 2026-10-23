using Synapic.Core.Services;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.State;

namespace Synapic.Indexer.Detection;

/// <summary>
/// The poll-mode detector: walks every configured root and enqueues new and
/// changed images (SCAN-01/02).
/// </summary>
/// <remarks>
/// The cost model is the point. An unchanged file costs one stat; only a file
/// whose size or last-write time moved is hashed, so a 15-minute scan over a NAS
/// does not re-read a library that has not changed. The extension set is
/// <see cref="WorkflowRunner.IsLocalImage"/> — the same filter the desktop's run
/// uses, so the indexer cannot see a different set of images.
/// </remarks>
public sealed class LocalFolderScanSource : IWorkSource
{
    private readonly IndexerConfig _config;
    private readonly IndexingQueueRepository _repository;
    private readonly IFileHasher _hasher;
    private readonly IWorkIntake _intake;

    public LocalFolderScanSource(
        IndexerConfig config,
        IndexingQueueRepository repository,
        IFileHasher hasher,
        IWorkIntake intake)
    {
        _config = config;
        _repository = repository;
        _hasher = hasher;
        _intake = intake;
    }

    public string Name => "local";

    public async Task<SourceScanResult> ScanAsync(CancellationToken ct)
    {
        var roots = _config.Schedule.WatchPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = _config.Schedule.WatchRecursive,
            IgnoreInaccessible = true,
            // A junction or symlink into a parent folder would otherwise walk
            // forever; a reparse point is not a folder this service owns.
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<JobCandidate>();
        var skippedBeforeHash = 0;
        var errors = 0;

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                SynapicLog.Warning(nameof(LocalFolderScanSource), $"Watch folder not found: {root}");
                errors++;
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", options))
                {
                    ct.ThrowIfCancellationRequested();

                    if (!WorkflowRunner.IsLocalImage(path)) continue;
                    if (!PathKey.TryForLocal(path, out var key)) continue;
                    if (!seen.Add(key)) continue;

                    try
                    {
                        var result = await LocalCandidateFactory
                            .TryCreateAsync(path, _repository, _hasher, Name, ct)
                            .ConfigureAwait(false);

                        switch (result.Status)
                        {
                            case LocalCandidateStatus.Candidate:
                                candidates.Add(result.Candidate!);
                                break;

                            case LocalCandidateStatus.AlreadyQueued:
                            case LocalCandidateStatus.Unchanged:
                                skippedBeforeHash++;
                                break;

                            case LocalCandidateStatus.Empty:
                                // A placeholder or an interrupted copy, not an
                                // image yet: a queued empty file can only fail.
                                SynapicLog.Debug(nameof(LocalFolderScanSource), $"Skipping empty file {path}");
                                break;

                            case LocalCandidateStatus.Unreadable:
                                SynapicLog.Warning(nameof(LocalFolderScanSource),
                                    $"Could not read {path}; it will be retried on the next scan.");
                                errors++;
                                break;

                            case LocalCandidateStatus.Missing:
                                break;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                                  or NotSupportedException)
                    {
                        SynapicLog.Warning(nameof(LocalFolderScanSource),
                            $"Skipping {path}: {e.GetType().Name}: {e.Message}");
                        errors++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Enumeration itself failed (a share went away mid-walk). The
                // other roots still scan.
                SynapicLog.Warning(nameof(LocalFolderScanSource),
                    $"Could not enumerate {root}: {e.GetType().Name}: {e.Message}");
                errors++;
            }
        }

        var intake = await _intake.EnqueueAsync(candidates, ct).ConfigureAwait(false);
        return new SourceScanResult
        {
            Found = candidates.Count + skippedBeforeHash,
            Enqueued = intake.Enqueued,
            Skipped = intake.Skipped + skippedBeforeHash,
            Errors = errors,
        };
    }
}
