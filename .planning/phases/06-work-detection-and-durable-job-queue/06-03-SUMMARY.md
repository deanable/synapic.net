# 06-03 SUMMARY — Filesystem watcher, debounce and stability probe

**Status:** Complete (host registration lands in 06-04, with the rest of the DI wiring)
**Requirements:** SCAN-03

## What was built

| File | Role |
|------|------|
| [DebounceBuffer.cs](../../../src/Synapic.Indexer/Detection/DebounceBuffer.cs) | Per-key due time, clock-injected, thread-safe, `DrainReady` oldest-first |
| [FileStabilityProbe.cs](../../../src/Synapic.Indexer/Detection/FileStabilityProbe.cs) | `IFileStabilityProbe` + the `FileShare.None` probe |
| [LocalCandidateFactory.cs](../../../src/Synapic.Indexer/Detection/LocalCandidateFactory.cs) | The shared "is this file queueable?" rule |
| [FolderWatcher.cs](../../../src/Synapic.Indexer/Detection/FolderWatcher.cs) | `BackgroundService`: one watcher per root, 500 ms flush, probe, intake |

`FolderWatcher` raises `NotifyFilter = FileName | LastWrite | Size`, `InternalBufferSize = 64 KB`,
`IncludeSubdirectories = watchRecursive`, deduplicates roots case-insensitively, uses a rename's
**destination** path, re-arms a failed probe after 2 s up to 30 attempts, and turns a watcher `Error` into a
full-scan callback rather than silence. Stopping disables and disposes every watcher before the base stop.

## One addition beyond the plan

The scan and the watcher had grown the same fingerprint policy in two places. Both now call
`LocalCandidateFactory.TryCreateAsync`, so "already queued / unchanged / empty / unreadable / candidate" is
decided once — the scan's counters and the watcher's enqueue cannot disagree about a file.
`InternalsVisibleTo Synapic.Indexer.Tests` was added so the overflow path can be driven without provoking a
real OS buffer overflow.

## Evidence

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo \
  --filter "FullyQualifiedName~DebounceBufferTests|FullyQualifiedName~FileStabilityProbeTests|FullyQualifiedName~FolderWatcherTests"
Passed! - Failed: 0, Passed: 19, Skipped: 0, Total: 19
```

Watcher facts, run twice: a file moved in is enqueued once after the debounce; a file still held
`FileShare.Read` by a writer produces no row, stays in the buffer, and is enqueued only after the handle
closes; a `.txt` is ignored while a `.PNG` is queued; a recursive watcher sees `sub/deep.jpg`; a non-recursive
watcher does not; an overflow callback fires; stopping clears every root. Debounce facts: window extended by a
second event, two paths independent, `ReArm` counts attempts, a new event resets them, case-insensitive keys,
and 500 concurrent adds against 500 drains.

Full indexer suite after the shared-factory refactor: **146 passed, 0 failed**.

## Known limits

- The watcher is real-time best effort. Files created before the OS watch handle is open (a few milliseconds
  after start) can be missed; the interval scan is the designed backstop, not a fallback that hides a bug.
- An event lost to a buffer overflow is recovered by a whole scan, not by replaying the lost paths.
