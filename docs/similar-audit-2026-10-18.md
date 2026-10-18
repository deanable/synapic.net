# Similar images — audit (2026-10-18)

**Reported symptom.** Daminion's own *similar images* saved search returns many
results in the Daminion UI. Running the same saved search as the source of
Synapic's **Similar images** operation returns almost nothing — three groups, or
none.

**Method.** Read the whole similarity path (`StepDedupViewModel` → `WorkflowRunner`
→ `DaminionApiClient` → `DedupScanHandler` → `DedupService`), then checked it
against the two live runs recorded in
`src/Synapic.Main/bin/Debug/net10.0/logs/synapic.log` (2026-10-17 16:04 and
16:05, saved search #8, scope count 6,704). No live server was available for the
fix verification; every claim below is either a code path or a line from that log.

## What the runs actually did

```
16:04:36.075 Similar images fetch: 500 item(s) in scope (saved search #8) —
             500 carry a server hash (grouped without download), 0 need a download to hash
16:04:37.310 Similar images scan done: 500 of 500 item(s) hashed
             (500 from server hash, 0 download failure(s), 0 unhashable file(s))
16:04:37.315 Unique server hashes: 500 across 500 items
16:04:37.317 Hashing finished — 500 file(s) hashed, 3 similar group(s) at threshold 0,94
16:05:37.754 Hashing finished — 500 file(s) hashed, 0 duplicate group(s) at threshold 0,9
```

Two independent defects are visible in those lines, and neither is a setting
being "too strict".

## F1 — the similarity search never hashed an image (root cause)

`DedupScanHandler.ProcessAsync` grouped every catalog item straight from
Daminion's `hashCode` and never called `ComputeHash`
([DedupScanHandler.cs:152](../src/Synapic.Main/Services/Processing/DedupScanHandler.cs#L152)).
On Daminion 11.x every item carries that field, so a catalog **Similar images**
scan became an exact-content-hash scan:

* the `Algorithm` (PHash/DHash/AHash) applied to nothing;
* the 0.85 similarity threshold applied to nothing;
* the only rule that touched those items was the *Server hash* mode, which was
  never intended as the similarity metric.

That is why the server's own search finds many matches and Synapic's finds
three: Synapic was not comparing pictures at all. The help page's promise ("at
most ten bits may differ … crops and light edits land in the same group") was
unreachable for a catalog source.

**Fixed.** `DedupOptions.UseServerHashes` (default `true`) now gates the
shortcut, and `StepDedupViewModel.BuildOptions()` sets `UseServerHashes:
!IsSimilar` ([StepDedupViewModel.cs:1117](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs#L1117)).
The Similar profile downloads and fingerprints each item, so its algorithm and
threshold apply to all of them. The Duplication profile keeps the shortcut — for
a content hash, identical values really do mean identical files. The *Server
hash* picker is no longer offered on the Similar tab (`ShowsServerHashRule`), and
the run page's settings summary no longer claims a rule that cannot apply.

A stale cache does not survive the fix either: a saved Similar result that holds
only catalog content hashes (an older build's output) is discarded on restore
(`IsStaleSimilarCache`) instead of being re-grouped and shown again, which would
have made the change look ineffective on the next launch.

**Cost, stated plainly.** A catalog similarity scan now downloads every item in
the scope (originals, one at a time, deleted after hashing) where it previously
downloaded nothing. For a 6,704-item scope that is minutes-to-hours, not
seconds. Hashing a downloaded *preview* instead of the original is the obvious
optimization — pHash is insensitive to the re-encode — and is the natural next
step; it was not done here because it changes which bytes the fingerprint
describes and deserves its own decision.

## F2 — the three "similar" groups were arithmetic noise

Daminion's `hashCode` is a **signed 32-bit** value: in the log, 1,008 samples,
522 of them negative, every one inside `int` range. `DedupScanHandler` cast
`long` → `ulong`
([DedupScanHandler.cs:162](../src/Synapic.Main/Services/Processing/DedupScanHandler.cs#L162)),
which sign-extends: a negative hash became `0xFFFF_FFFF_………`, so every negative
value shared 32 leading 1 bits and any negative/positive pair differed in at
least 32 of the 64 compared bits. Hamming mode was therefore comparing the sign
padding, not the hash — and with 500 near-uniform 32-bit values, three pairs
inside 4 bits is about what chance predicts (≈0.7 expected). The first run's
"3 similar groups" is a coincidence, not a find.

The budget was wrong in the same way: `MaxHammingDistance` derived the cutoff
from 64 bits for a 32-bit hash, doubling the share of the hash a threshold
promised.

**Fixed.** Server hashes are stored zero-extended
(`unchecked((ulong)(uint)serverHash)`), and both the cutoff and the similarity
scores for the server-hash path are measured over 32 bits
(`MaxHammingDistance(threshold, bits)`,
[DedupService.cs:647](../src/Synapic.Main/Services/Processing/DedupService.cs#L647)).
Consequence to know about: at 0.90 the server-hash Hamming budget is now 3 bits,
not 6, so a Hamming run finds *fewer* coincidences than before — which is the
point. Two tests that pinned the old 64-bit arithmetic were updated to the
32-bit numbers.

Whether Daminion's `hashCode` is perceptual at all remains unverified; the
diagnostic summary still reports the distribution so a live run can settle it.
Nothing depends on that answer now that the similarity search does not use it.

## F3 — only 500 of 6,704 items were ever compared

```
16:04:34.656 Daminion scope count: 6704 (diagnostic only; the walk follows the page totalCount)
16:04:35.714 Page MediaItems/Get: index=0   size=500 … raw=500 eligible=500 … totalCount=200 ids=65204..92997
16:04:36.073 Page MediaItems/Get: index=500 size=500 … raw=0   eligible=0   … totalCount=0
16:04:36.073 Fetched 500 Daminion items (scope=saved_search, total=0, pages=1)
```

The walk ended at the first empty page. Two code problems made that invisible:

* the short-read check compared the kept items against `scopeTotal`, which for a
  saved search had just been overwritten with the page's own total (`0`) — so
  `500 vs 0` never warned;
* the diagnostics block explained any saved-search/collection difference as
  "filtered locally", which was false here: `raw=500 eligible=500` on every page.
  The user-facing line was "500 files scanned", with the scope's 6,704 only in a
  block that excused the gap.

**Fixed (visibility, not retrieval).** An empty page before the scope's own
count now logs a warning naming the gap and the scope
([WorkflowRunner.cs:361](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs#L361));
the short-read check uses the scope count and distinguishes locally-dropped
records (`Scanned > Kept`) from a walk that simply stopped; `ScanSummary` carries
"The scope holds N; the walk read M."; and the diagnostics sentence says "short
read, not a local filter" instead of excusing it.

**Fixed — the walk now uses the search's record count and adapts its paging.**
Daminion's REST reference describes `index` as *"the serial number of the
requested page"*, while this walk advanced by item offset — and the two agree
only for page 0, which is exactly what the log shows (page 0 answers with 500
records, the offset request at 500 answers with nothing). Rather than gamble on
one meaning, the walk now keeps the search's own count as its target and, when a
page comes back empty while that count still has records left, continues with
**page serials**, resuming at the page the raw records already cover, and stops
once the count is collected (a page budget backstops it). Offset stepping stays
the default, so every catalog walk that already works is untouched; the switch,
the cursor each page used and any shortfall are logged, and `Scanned` in the
diagnostics is the raw records the server actually returned.

A test drives a page-serial-only stub (page 0 answers, then only `index` as a
page number does) and asserts the whole 1,500-record scope arrives through the
switch — `0, 500, 1, 2` requests, no trailing empty page, because the count said
where the end was. If a live catalog still stops early, the two requests worth
making are `index=1&size=500` (does a page serial answer?) and the same with
`maxItemsCount` dropped (an undocumented parameter this client always sends);
both outcomes are now visible in the log rather than silently reported as a
complete scan.

## Secondary findings (reported, not changed)

* **Threshold rounding is one bit loose.** `MaxHammingDistance` rounds, so 0.99
  admits one differing bit — a pair at 0.984 similarity, below the stated
  threshold, still groups; 0.85 admits ten bits (0.844), not nine. The help text
  and the tests both inherit the rounded numbers. `Math.Floor` would make the
  number mean what it says, at the cost of 0.99 meaning "exact only".
* **The per-algorithm defaults for Similar are self-defeating.** DHash starts at
  0.97 (two bits) and AHash at 0.99 (one bit) *because* they are documented as
  coarser — but a coarser fingerprint needs a *looser* budget to show visual
  similarity, not a tighter one. A dHash similarity search at one to two bits
  is effectively an exact-match search. The run in the log used DHash at 0.94
  (four bits), which is why it looked as if the threshold was merely "strict".
* **The live threshold slider cannot affect a catalog result built from server
  hashes.** With F1 fixed this only matters for the duplicate scan, where the
  fingerprints are content hashes: moving the threshold there re-groups the
  perceptual set only. Worth a disabled state or a hint when `ExactHashes` is
  non-empty and `PerceptualHashes` is empty.
* **Row dates come back null on the no-download path** (`DateTakenUtc` is not in
  the `/MediaItems/Get` response), so Oldest/Newest keep-set rules never fire for
  catalog duplicates grouped from the server hash. Unchanged by this audit.

## Verification

`dotnet build Synapic.Net.sln` — 0 warnings, 0 errors. Tests:
`Synapic.Main.Tests` **678 passed** (the full suite was rerun after the last
change), plus 5 integration and 6 shared. New coverage: the Similar profile
hashing instead of trusting the server hash; a negative hash zero-extended to 32
bits; the 32-bit hamming cutoff and score; the profiles' differing rules; the
Similar settings tab no longer offering the rule; a short saved-search walk
reported as a short read; a content-hash-only Similar cache being discarded
rather than restored; and a page-serial-only catalog stub still yielding the
whole scope through the offset-then-serial switch.
No live Daminion server was available, so F1's download cost and F3's paging
question are unverified against the real catalog.
