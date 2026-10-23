# Repository review and follow-ups — 2026-10-22

**Scope:** repository-wide review of the desktop app (`src/Synapic.Main`, 108 C#
files / ~21.4k lines), the AXAML views, the Python sidecar (`src/Synapic.Inference`,
~4.3k lines), the shared contracts and the test suite. This pass focused on the
deduplication / similar-image search path and on application state — which
controls are enabled at each point of the workflow.

This document records what was changed, how it was verified, and the issues that
are **not** readily fixable in a single pass (product decisions, larger
refactors, missing environments, or risks that need measurement first). It
complements, and does not replace, [docs/code-review-2026-10-12.md](docs/code-review-2026-10-12.md)
and [docs/similar-audit-2026-10-18.md](docs/similar-audit-2026-10-18.md); items
those documents raised are referenced by their IDs. Everything fixed after the
first draft of this document is marked **closed** with the change that closed it.

---

## 1. What this pass changed

### 1.1 Deduplication and similar-search algorithms

| Area | Change | File |
|---|---|---|
| pHash average | Replaced `pixels.Average(...)` (LINQ + enumerator over 64 bytes per image) with a fixed 64-iteration sum. | [DedupService.cs](src/Synapic.Main/Services/Processing/DedupService.cs) |
| DCT | The two-pass 32×32 → 8×8 DCT now uses `stackalloc` scratch buffers instead of two heap arrays per image, and hoists `1/√2` out of the inner loops. | same |
| Median (pHash DC exclusion) | `Median` takes a `ReadOnlySpan<double>` and sorts a stack buffer; the previous version allocated a LINQ `OrderBy(...).ToArray()` *and* a `Skip(1).ToArray()` per image. | same |
| Near-duplicate grouping | The bucket-head `Dictionary` is allocated once per group and cleared per mask pass (it was one allocation per mask), the hash of the pivot is read once, and union-find is consulted **before** computing the Hamming distance — pairs already in one component are skipped entirely. | same |
| Scan scheduling | [WorkflowRunner](src/Synapic.Main/Services/Processing/WorkflowRunner.cs) now uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism` instead of creating one `Task.Run` per item up front and throttling them with a semaphore. Memory per batch no longer grows with the item count, and a cancelled scan stops scheduling new work instead of letting queued tasks drain. | same |
| Preview decoding | Local previews are decoded in parallel, bounded by `DedupService.LocalHashingParallelism`, into a `ConcurrentDictionary`; a duplicate key can no longer overwrite (and leak) a decoded bitmap. | [StepDedupViewModel.cs](src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs) |
| Local scan path | File enumeration moved off the UI thread and now honours the shared **LocalRecursive** setting (it previously always walked subdirectories, silently ignoring the toggle). The extension filter reuses `WorkflowRunner.IsLocalImage`, so the scan and the batch fetch cannot disagree about what an image is. | same |
| Scan identity | The retained-scan cache key now includes the recursion flag, so a retained fingerprint set cannot be restored or re-filtered as if it described a differently scoped folder. Fast re-use of a retained scan is decided by that identity alone (`HasReusableScan`); the threshold slider additionally requires perceptual fingerprints, so a catalog scan grouped from server hashes still re-groups instantly without offering a slider that cannot change anything. | same |

The algorithm **behaviour** is unchanged: same fingerprints, same connected
components, same threshold semantics. Existing equivalence, scale and
similarity tests continue to pass without edits, which is the evidence that the
optimisations are mechanical rather than semantic.

### 1.2 Threshold semantics and defaults — **closed (A1, A2)**

`DedupService.MaxHammingDistance` now **floors** `bits × (1 − threshold)` and
clamps to `[0, bits]` instead of rounding. A 0.99 threshold promises that at most
0.64 of 64 bits differ, and rounding up admitted a differing bit the label did
not promise.

Changing the rounding made several shipped defaults mean one bit less than they
were calibrated for, so the per-algorithm defaults were rewritten as the
percentages that keep the intended budgets (comment in
[AppSettingsRegistryStore.cs](src/Synapic.Main/Services/AppSettingsRegistryStore.cs)
records the measurement behind each):

| Algorithm | Duplicates | Similar |
|---|---|---|
| pHash | 0.90 (6 bits) | 0.84 (10 bits) |
| DHash | 0.96 (2 bits) | 0.90 (6 bits) |
| AHash | 0.98 (1 bit) | 0.93 (4 bits) |

The Similar defaults also moved off the "coarser fingerprint needs a tighter
threshold" mistake: they are now loose enough to find the re-encodes and light
edits the feature exists for. The UI hint, the help topics
([dedup.html](docs/help/dedup.html), [similar.html](docs/help/similar.html)) and
the reference docs quote the same numbers.

### 1.3 Dedup Apply actions — **closed (A4)**

The local action list no longer offers **Tag**. It wrote one log line and
changed nothing, which review R14 called worse than offering nothing. Local
apply now offers exactly two actions:

- **Move to folder…** (index 0, the default) — reversible, needs a destination
  outside the scanned folder;
- **Delete from disk** (index 1) — permanent, fail-closed confirmation.

The persisted encoding was not reused blindly: older builds stored 0 = Tag,
1 = Move, 2 = Delete, so local actions now persist as 10/11 and a restored 0/1/2
folds into Move/Delete without ever turning a saved *Move* into a *Delete*.

The mirror problem is closed too: the **retune slider is hidden** for a result
grouped only from the catalog's content hashes (**A3**). Those hashes are
compared exactly, so the slider had nothing to move;
`IsExactHashOnlyScan` / `ExactHashScanHint` say why in the results page instead.

### 1.4 Application state — controls enabled only when their prerequisites are met

The start gates are expressed **once** per operation as
`RunStateViewModel.StartBlockedReason`; the command's `CanExecute` is literally
"the reason is null", so the disabled button and the explanation cannot drift
apart. (That notification method is now `internal`: it is a coordination detail
for the shell, not public API — **closed (E3)**.)

| Operation | Gate now requires |
|---|---|
| Tagging | a usable source (existing folder, or a connected Daminion session), a model id, at least one tag field selected, a running inference server, and no batch already running |
| Similar / Dedup scan | a source (existing folder or connected Daminion), no scan running, no apply in progress, no live re-group |
| Upscaling | a usable source, a running inference server, and no run in flight |
| Dedup Apply | groups, checked targets, a valid move destination, **and** a retained scan that still matches the current source |
| Results export | only with at least one result row |

Supporting wiring:

- `RunStateBar` shows the blocked reason under the action row
  ([RunStateBar.axaml](src/Synapic.Main/Views/Operation/RunStateBar.axaml)); a
  greyed button always says which setting is missing, and the hint disappears
  when the run starts.
- The tagging run re-evaluates when the **source**, the **server** status, the
  **model** or a **tag field** changes, not only on an unrelated notification.
- The upscale run re-evaluates on server status as well as source changes.
- `OperationShellViewModel.IsRunning` now includes dedup/similar scans **and**
  applies **and** the results report's Retry/Verify work, and the shell
  subscribes to those view models, so navigation is locked during any pass
  (**closed B2/D4**, R04). The operation adapters report `RunDisabledReason`
  from that same property instead of re-deriving it.
- The scope controls inside the operation view are disabled while any run is in
  flight (`OperationLayout.IsSourceEditable`, driven by
  `MainWindowViewModel.IsNavigationLocked`): the pass works on the selection it
  started with, and moving the controls would describe a different source.
- Apply refuses to run against rows whose scan no longer matches the current
  source (`ScanMatchesCurrentSource`), closing the "apply to ids from another
  catalog" half of R03 for every case the source identity can see.

### 1.5 Catalog writes re-check the scan generation — **closed (B1)**

Apply acted on catalog ids a scan had captured. A catalog rebuilt (or swapped)
behind the same URL reuses those ids, so the same id can name a different image
by the time the user presses Apply. The write now re-reads the catalog guid from
the server immediately before the first mutation and refuses when it is not the
one the review list came from:

- the scan (and a restored scan) records the guid it read;
- the check runs after the user's confirmation and before any
  `MediaItems/Remove` or `BatchChange`, for delete **and** reject;
- the refusal names both guids, says nothing was changed, and asks for a rescan;
- the check never invents an identity — a scan that could not read a guid, or a
  server that cannot report one now, leaves the existing source-key gate as the
  only guard rather than blocking writes on old servers.

Covered by `DaminionSelectionBehaviorTests.Catalog_apply_refuses_when_the_server_now_reports_another_catalog`:
a scanned, connected stub flips its guid and the Apply sends no delete.

### 1.6 Live catalog tests — **closed (A5)**

`DaminionLiveSessionTests` used ordinary `[Fact]`s whose opt-in early return was
reported as a pass, and A5's remaining half was that a machine with saved
credentials but no route to the server failed the whole suite. Both tests are now
`[SkippableFact]` (dependency: `Xunit.SkippableFact`) and:

- skip with a reason when the required credentials are absent;
- skip with the exception message when the configured server cannot be reached
  (a handshake/auth failure is not a product regression);
- skip when the saved catalog does not hold the two fixture images the hash
  comparison is about — a different database or account cannot answer it;
- still assert normally when a server answers with the fixtures.

The default run is therefore honestly green on a machine with no catalog, and
still exercises the live flow where one is configured. Both carry
`[Trait("Category", "Live")]`, so a hermetic run is
`dotnet test Synapic.Net.sln -c Release --filter "Category!=Live"`.

### 1.7 Tests added or updated

- `OperationGatingTests`: source / server gating for tagging; a scan waits for
  an existing folder and for an apply to finish; every in-flight operation locks
  the others out; a headless render test asserts the blocked reason is visible
  next to the disabled action and disappears when the gate opens.
- `OperationLockWiringTests`: a dedup scan locks navigation, the report's
  Retry/Verify work locks it too, and the scope controls inside the operation
  view are disabled for the duration of a run and re-enabled after it.
- `StepDedupViewModelTests`: a local scan honours `LocalRecursive` (top-level
  only vs. subfolders); the local action list offers no no-op Tag; persisted
  action values migrate without turning Move into Delete.
- `SimilarSearchTests`, `DedupFingerprintRestoreTests`, `DedupScanHandlerTests`:
  updated for the exact-hash scan hint, the re-use rule and the threshold
  defaults.
- `StepUpscaleViewModelTests`, `UpscaleRouteTests`, `RouteSplitTests`: updated
  for the prerequisite wording and the running-server requirement.
- `DaminionSelectionBehaviorTests`: the exact-hash result (no slider, notice
  shown, instant re-group) and the catalog-generation refusal before any delete.
- `PlatformFacts.cs` (`[WindowsFact]`): the 24 Windows-registry tests report a
  skip off Windows instead of passing without asserting. It derives from
  `FactAttribute`, not `SkippableFactAttribute`: the skippable discoverer gives
  every test its own collection, which ran registry tests concurrently with the
  headless Avalonia collections and made unrelated UI tests fail intermittently.
- `EndToEndRoundTripTests`: an absent, or stale, sidecar bundle is a skip with
  the reason (the staleness guard has its own tests) rather than an early return
  reported as a pass.
- `PlatformFactAttributeTests`: the `[WindowsFact]` mechanism itself — no skip on
  Windows, a reason everywhere else.

---

## 2. Verification

```
dotnet build Synapic.Net.sln -c Release --nologo      -> 0 warnings, 0 errors
dotnet test  Synapic.Net.sln -c Release --no-build    -> 748 passed, 3 skipped, 0 failed
build/_python/win-x64/python/python.exe -m pytest -q -> 266 passed
uv build (src/Synapic.Inference)                     -> sdist + wheel
```

- `Synapic.Shared.Tests`: 6/6 passed.
- `Synapic.Integration.Tests`: 5/5 passed (includes the real local sidecar
  round trip).
- `Synapic.Main.Tests`: 738 passed, 3 skipped.
- The three skips say why: two opt-in live-catalog probes
  (`DaminionLiveSessionTests` — one needs `SYNAPIC_TEST_DAMINION_*`, the other
  found the saved server but not its two fixture items) and the local sidecar
  round trip, whose bundled executable is older than the sidecar source after
  this pass's `pyproject.toml` fix — the app refuses a stale bundle by design,
  and the test now reports that refusal as a skip instead of failing or
  pretending to have run.
- The full suite was run repeatedly: the first two runs after the
  test-infrastructure changes were green, a later one exposed the live probe's
  fixture assumption (fixed above), and the final run is green. The intermittent
  dispatcher failures seen in between came from the skippable discoverer giving
  each registry test its own collection (see `PlatformFacts.cs`).

The UI behaviour was exercised through the headless Avalonia tests: the blocked
reason renders beside a disabled action and clears when the gate opens, the
review list hides the threshold slider for an exact-hash result, the "compared
catalog" refusal leaves the review list and sends no delete, and the scope panel
follows the navigation lock. No screenshot was taken.

Not run: a live Daminion write round trip (needs a test catalog). It is reported
as unverified, not as a pass.

---

## 3. Issues that remain open

### A. Product decisions needed

None from the original list: A1–A5 are closed above.

### B. Correctness / robustness work larger than this pass

**B1. Closed** — see §1.5. Apply re-reads the server's catalog guid before the
first mutation and refuses when it is not the catalog the review list came from.
A server that reports no guid at all still relies on the source-key gate; that
residual case is old server builds, not a rebuilt catalog.

**B3. PNG/TIFF metadata lifecycle (R16) is unchanged.**
Repeated PNG writes still append another XMP chunk; TIFF writes a sidecar that
the app's own reader does not consult; dedup move/delete leaves a sidecar
orphaned. This needs a shared metadata-patch contract per format, not a one-line
fix.

**B4. Lifecycle and disposal inconsistencies (R18) are unchanged.**
`DaminionApiClient` owns an `HttpClient` but is not disposable; disconnect starts
logout without awaiting; sidecar start/stop have a reserve-after-check race;
several fire-and-forget tasks are untracked. An explicit single owner for
infrastructure lifecycle is required.

**B5. The sidecar trust boundary (R12) is unchanged.**
The local HTTP sidecar still has no caller authentication or origin check, and
exposes shutdown. This is a security design decision (token in a per-user file
vs. loopback-only assumptions), not a mechanical patch.

### C. Performance at catalog scale

**C1. Retained fingerprints are still two string-keyed dictionaries.**
Re-grouping in place is what makes the threshold slider cheap, and that requires
keeping perceptual and exact hashes in memory. At 100k+ catalog items the string
keys and `Dictionary` overhead dominate the pairwise work we removed. Interning
paths/ids to `int` keys (or a compact struct array) is the next step, but it
should follow a measured memory profile — the audit's numbers (≈0.6 s to group
50k) say the grouping CPU cost is not the problem.

**C2. Catalog scans without server hashes still download every original serially.**
This is deliberate (one temporary file at a time, so a large scope never lands on
disk whole), and it is now the dominant cost for old server builds. Raising
download concurrency needs a disk-budget decision and a measured run against a
real catalog.

**C3. Preview decoding and hashing share one parallelism constant.**
`LocalHashingParallelism` bounds both; previews can load while a scan is running,
so the two pools can oversubscribe. A separate, smaller semaphore for preview
decoding is a reasonable next step; it was left alone because the current bound
is conservative (cores/2, max 8) and changing it without measurement would be
guesswork.

**C4. No 100k benchmark was run in this pass.**
The grouping tests cover correctness at scale; the throughput/memory figures
quoted in the audits were not re-measured. Any further optimisation claim should
start with `dotnet test --filter DedupGroupingScale` plus a release-mode profile.

### D. Test and CI gaps

**D1. Closed** — the live probes carry `[Trait("Category", "Live")]` as well as
their skip, so a hermetic run is one filter away:
`dotnet test Synapic.Net.sln -c Release --filter "Category!=Live"`.

**D2. Mostly closed.** The registry tests are `[WindowsFact]` (24 of them), so
off Windows they report as skipped with the reason instead of a pass that
asserted nothing. Five `[AvaloniaFact]` tests still return early: Avalonia's fact
attribute is sealed and its framework has no runtime skip, so an honest skip
there would need a fork or a different test host.

**D3. Closed.** The Python suite passes on the bundled interpreter
(`build/_python/win-x64/python/python.exe -m pytest`, 266 passed). R21 was real:
`uv build` failed with "package directory 'synapic_inference' does not exist".
The package now maps onto the source directory (`[tool.setuptools.package-dir]`),
and the built wheel carries `synapic_inference/*.py` with the
`synapic-inference = synapic_inference.service:main` entry point intact.

**D4. Closed** — navigation-lock and busy-state coverage now includes dedup
scan/apply and the report's Retry/Verify work.

### E. Code hygiene

**E1. `StepDedupViewModel` is 2,500+ lines** and owns scan orchestration, review
state, keep-set rules, previews, persistence and apply. Splitting it (scan /
review / apply collaborators) is worthwhile but is exactly the kind of refactor
that should not ride along with behaviour changes.

**E2. Closed** — the statement sharing its line with the following
`SynapicLog.Info(` call is split and re-indented.

**E3. Closed** — `NotifyStartGateChanged` is `internal`, so the shell can still
re-evaluate a gate it does not own but the method is no longer public API. A
narrow `IRunGate` event would remove the call entirely; that is a refactor, not a
hygiene fix.

---

## 4. Suggested order for the next pass

1. B4 (one owner for client/sidecar lifecycle) — the root of several smaller
   symptoms, and the largest of the remaining items.
2. C1 + C3 with a measured 100k profile (C4) before either change.
3. B3 (a per-format metadata-patch contract) and B5 (sidecar caller auth) — both
   need a format/security decision before code.
4. D2's last five `[AvaloniaFact]` tests, if a test host with a runtime skip is
   ever adopted for the headless suite.
