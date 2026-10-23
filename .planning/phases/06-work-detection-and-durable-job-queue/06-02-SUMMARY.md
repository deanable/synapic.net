# 06-02 SUMMARY — Local interval scan and change detection

**Status:** Complete
**Requirements:** SCAN-01, SCAN-02

## What was built

| File | Role |
|------|------|
| [PathKey.cs](../../../src/Synapic.Indexer/Detection/PathKey.cs) | `local|` / `daminion|` keys; `TryForLocal` returns false instead of throwing |
| [ContentFingerprint.cs](../../../src/Synapic.Indexer/Detection/ContentFingerprint.cs) | `IFileHasher`, `Sha256FileHasher` (share read/write/delete), `srv:` hashes |
| [IWorkIntake.cs](../../../src/Synapic.Indexer/Detection/IWorkIntake.cs) | `IWorkIntake`, `WorkIntake`, `IWorkSource`, `SourceScanResult` |
| [LocalFolderScanSource.cs](../../../src/Synapic.Indexer/Detection/LocalFolderScanSource.cs) | The walk: enumeration options, stat-first fingerprint, per-file error isolation |

The extension set is `WorkflowRunner.IsLocalImage` — the desktop's own filter — rather than a second copy
of the list. Enumeration uses `RecurseSubdirectories = watchRecursive`, `IgnoreInaccessible = true`,
`AttributesToSkip = ReparsePoint`.

## One addition beyond the plan

The scan also skips a file whose row is **Pending or InFlight** without hashing it. The plan only named the
Completed/unchanged stat check, but re-hashing work the queue already owns is pure cost — enqueueing Pending
or InFlight work is a no-op by definition. Rows in TransientFailure and Failed are still hashed, because a
change while transient is exactly what earns a retry.

## Evidence

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo \
  --filter "FullyQualifiedName~LocalFolderScanSourceTests"
Passed! - Failed: 0, Passed: 10, Skipped: 0, Total: 10, Duration: 306 ms
```

Fixture tree: `a.jpg`, `B.JPEG`, `c.png`, `d.tif`, `e.txt`, `sub/f.tiff`, zero-length `empty.jpg`.
Facts: first scan enqueues exactly the five supported non-empty files (5 hash reads); second scan enqueues
nothing and performs **zero** hash reads; touching only the mtime of a completed file costs exactly one hash,
enqueues nothing and refreshes the stored time; appending one byte returns that file to Pending; a completed
file with moved time but equal bytes stays Completed; nested roots yield five rows, not ten; a missing root is
one error while the other root still scans; a pre-cancelled scan throws with an empty queue; a file held
`FileShare.None` is skipped with one error and the other four files still enqueue; non-recursive mode gives
four.

## Known limits

- The per-file lookup is one `SELECT` per changed candidate. A first scan of a large library therefore costs
  one query per file; batching the lookup is a performance change best made with a real library to measure.
- Hard links and different UNC aliases to the same bytes are distinct keys. Case, nesting and separators are
  normalized; that is the extent of identity in v1.1.
