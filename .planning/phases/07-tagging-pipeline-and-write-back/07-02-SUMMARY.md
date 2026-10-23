# 07-02 SUMMARY — Write-back policy and the fingerprint refresh

**Status:** Complete
**Requirements:** TAG-02, TAG-04 (write half)

## What was built

| File | Role |
|------|------|
| [ITagWritePolicy.cs](../../../src/Synapic.Core/Services/Processing/ITagWritePolicy.cs) | Core seam (`AdjustAsync`), plus `NullTagWritePolicy` so the desktop path is byte-for-byte unchanged |
| [ProcessingOrchestrator.cs](../../../src/Synapic.Core/Services/Processing/ProcessingOrchestrator.cs) | Optional policy on the ctor; called once per item, after `/tag`, before any write |
| [PreserveExistingTagsPolicy.cs](../../../src/Synapic.Indexer/Pipeline/PreserveExistingTagsPolicy.cs) | Reads the file once (`InspectAsync`) and reuses that read for the skip decision and the merge decision |
| [BatchExecutor.cs](../../../src/Synapic.Indexer/Pipeline/BatchExecutor.cs) | Pre-check: overwrite off + every selected field populated → `Completed` (`existing tags preserved`), no `/tag`; fingerprint refresh after completion |
| [IndexingQueueRepository.cs](../../../src/Synapic.Indexer/State/IndexingQueueRepository.cs) | `UpdateFingerprintAsync` (only while the row is `Completed`) |
| [WriteBackPolicyTests.cs](../../../tests/Synapic.Indexer.Tests/WriteBackPolicyTests.cs) | Five facts, all read back from the files |

## Decision conflict, resolved in favour of D-11-23

Plan 07-02's task text says a file that has **keywords but no category/description** should get the model's
keywords merged when `overwriteExistingTags:false`. D-11-23 says a *populated selected field* is left out of the
tags handed to the writer. Those are different behaviours for the keyword field, and the implementation follows
the locked decision: with overwrite off, an existing keyword set is untouched, while the empty category and
description are filled in. The test was written to the decision
(`Partially_tagged_file_fills_only_the_empty_selected_fields`), and the merge-only limitation is pinned where it
is observable — overwrite **on**:

```
Assert.Contains("old", tags.Keywords);   // Overwrite_writes_every_selected_field_but_still_merges_keywords
Assert.Contains("forest", tags.Keywords);
```

## Evidence

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~WriteBackPolicyTests"
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5
```

- (a) Fully tagged + overwrite off → 0 sidecar calls, `Completed`, `job_events` detail `existing tags preserved`,
  file unchanged.
- (b) Keywords only + overwrite off → sidecar called; category/description written; keywords still `["old"]`
  (D-11-23, see above).
- (c) Overwrite on → all three written; `old` and `forest` both present (merge-only, stated not hidden).
- (d) `writeKeywords=false` → keywords unchanged even though the model returned some; category/description written.
- (e) After completion the stored size/mtime/hash equal the file on disk, and a real `LocalFolderScanSource`
  rescan finds 1 file and enqueues 0 — the tag → scan → tag loop is closed.
