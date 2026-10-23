using Microsoft.Extensions.Hosting;
using Synapic.Core.Services;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.State;

namespace Synapic.Indexer.Detection;

/// <summary>
/// Real-time detection (SCAN-03): one <see cref="FileSystemWatcher"/> per root,
/// a debounce window per path, and an exclusive-read probe before anything is
/// queued. A file that a writer still holds is retried, never tagged mid-write.
/// </summary>
/// <remarks>
/// Watch mode answers file events directly; the interval scan remains the
/// backstop for everything the watcher cannot see (a buffer overflow, files
/// created while the service was down, a path that never became stable). Both
/// feed the same <see cref="IWorkIntake"/>, so idempotency has one
/// implementation.
/// </remarks>
public sealed class FolderWatcher : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxProbeAttempts = 30;

    private readonly IndexerConfig _config;
    private readonly IWorkIntake _intake;
    private readonly IFileStabilityProbe _probe;
    private readonly IFileHasher _hasher;
    private readonly IndexingQueueRepository _repository;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task>? _onOverflow;
    private readonly TimeSpan _debounceWindow;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _roots = new();

    public FolderWatcher(
        IndexerConfig config,
        IWorkIntake intake,
        IFileStabilityProbe probe,
        IFileHasher hasher,
        IndexingQueueRepository repository,
        TimeProvider? clock = null,
        TimeSpan? debounceWindow = null,
        Func<CancellationToken, Task>? onOverflow = null)
    {
        _config = config;
        _intake = intake;
        _probe = probe;
        _hasher = hasher;
        _repository = repository;
        _clock = clock ?? TimeProvider.System;
        _onOverflow = onOverflow;

        _debounceWindow = debounceWindow ?? DefaultDebounceWindow;
        if (_debounceWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounceWindow));
        Buffer = new DebounceBuffer(_debounceWindow, _clock);
    }

    /// <summary>The debounce buffer, exposed so a test can assert what is pending.</summary>
    internal DebounceBuffer Buffer { get; }

    /// <summary>Roots the watcher is actually raising events for (missing roots are skipped).</summary>
    public IReadOnlyList<string> Roots => _roots;

    public int BufferedPaths => Buffer.Count;

    public static bool ModeIncludesWatch(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() is "watch" or "intervalandwatch";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ModeIncludesWatch(_config.Schedule.Mode))
        {
            SynapicLog.Debug(nameof(FolderWatcher),
                $"Schedule mode '{_config.Schedule.Mode}' has no watcher; interval scans only.");
            return;
        }

        StartWatchers();
        if (_watchers.Count == 0)
        {
            SynapicLog.Warning(nameof(FolderWatcher),
                "No watch folder could be watched; detection falls back to the interval scan.");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(FlushInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The stop signal, not a failure.
        }
        finally
        {
            StopWatchers();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop raising events first: a callback arriving during base.StopAsync
        // would write into a buffer nobody drains.
        StopWatchers();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases every path whose window elapsed: probe, build the candidate with
    /// the same fingerprint rules the scan uses, enqueue, or re-arm the probe.
    /// Public so tests drive the exact step the loop performs.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        foreach (var ready in Buffer.DrainReady())
        {
            ct.ThrowIfCancellationRequested();

            if (!ready.Key.StartsWith(PathKey.LocalPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var path = ready.Key[PathKey.LocalPrefix.Length..];

            if (!_probe.TryOpenExclusive(path, out var reason))
            {
                if (ready.Attempts + 1 >= MaxProbeAttempts)
                {
                    SynapicLog.Warning(nameof(FolderWatcher),
                        $"Giving up on {path} after {ready.Attempts + 1} stability probes ({reason}); " +
                        "the next interval scan will pick it up.");
                    continue;
                }

                Buffer.ReArm(ready.Key, RetryDelay);
                SynapicLog.Debug(nameof(FolderWatcher),
                    $"Waiting for {path} to become stable ({reason}); probe {ready.Attempts + 1}.");
                continue;
            }

            try
            {
                var result = await LocalCandidateFactory
                    .TryCreateAsync(path, _repository, _hasher, "local", ct)
                    .ConfigureAwait(false);

                switch (result.Status)
                {
                    case LocalCandidateStatus.Candidate:
                        await _intake.EnqueueAsync(new[] { result.Candidate! }, ct).ConfigureAwait(false);
                        break;

                    case LocalCandidateStatus.Unreadable:
                        SynapicLog.Warning(nameof(FolderWatcher),
                            $"Could not read {path}; the next interval scan will retry it.");
                        break;

                    default:
                        SynapicLog.Debug(nameof(FolderWatcher),
                            $"Ignoring {path} ({result.Status}); nothing to enqueue.");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                SynapicLog.Warning(nameof(FolderWatcher),
                    $"Could not examine {path}: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void StartWatchers()
    {
        foreach (var root in _config.Schedule.WatchPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => path.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                SynapicLog.Warning(nameof(FolderWatcher), $"Watch folder not found: {root}");
                continue;
            }

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = _config.Schedule.WatchRecursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
                _roots.Add(root);
                SynapicLog.Info(nameof(FolderWatcher),
                    $"Watching {root} (recursive: {_config.Schedule.WatchRecursive}, " +
                    $"debounce: {BufferWindowForLog()} s).");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                SynapicLog.Warning(nameof(FolderWatcher),
                    $"Could not watch {root}: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Disposed twice by shutdown and the finally block; harmless.
            }
        }

        _watchers.Clear();
        _roots.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => QueueIfSupported(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => QueueIfSupported(e.FullPath);

    private void QueueIfSupported(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!WorkflowRunner.IsLocalImage(path)) return;
        if (!PathKey.TryForLocal(path, out var key)) return;
        Buffer.Add(key);
    }

    private void OnError(object sender, ErrorEventArgs e) => NotifyBufferOverflow(e.GetException());

    /// <summary>
    /// A watcher error is usually a buffer overflow. Events were lost, so the
    /// truthful response is a full scan, not silence.
    /// </summary>
    internal void NotifyBufferOverflow(Exception? error)
    {
        SynapicLog.Warning(nameof(FolderWatcher),
            $"FileSystemWatcher reported an error ({(error?.GetType().Name ?? "unknown")}: {error?.Message}); " +
            "running a full scan to catch anything the events missed.");

        if (_onOverflow is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _onOverflow(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                SynapicLog.Error(nameof(FolderWatcher), "The overflow recovery scan failed.", e);
            }
        });
    }

    private string BufferWindowForLog() =>
        _debounceWindow.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
