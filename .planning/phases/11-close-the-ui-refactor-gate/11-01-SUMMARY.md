# 11-01 SUMMARY — Gate re-verification, recorded against the current tree

**Status:** Complete
**Requirements:** GATE-01 ✓, GATE-02 ✓, GATE-03 ✓, GATE-04 ✓

## What this plan did

11-01-PLAN.md set up the gate re-verification framework and the structural checks. Execution landed the
substance the gate needed and then re-ran everything against the current tree:

| File | Change |
|------|--------|
| `docs/ui-empty-branches.md` | New: the finite inventory §10 criterion 6 now rests on — 56 rows (55 `Named`, 1 `Shadowed`, 0 `Partial`, 0 `Gap`), each with the `Where` and the behavioral test that names it |
| `tests/Synapic.Main.Tests/EmptyBranchInventoryTests.cs` | New: the drift check — every citation in **any** table's `Named by` column (the per-mode tables *and* the busy + Stop summary table) must resolve to a real class/method; `Named` rows must name a test; statuses must be known; ids unique; a 40-row floor keeps an emptied document from passing vacuously. One fact runs the check with every `testExists` returning false, so the failure path and the busy-table coverage are pinned, not assumed |
| `tests/Synapic.Main.Tests/` (9 files) | The eleven `Gap` and seven `Partial` rows closed by new tests asserting the copy, not just the state |
| `src/…/Step2EngineViewModel.cs`, `src/…/Step4ResultsViewModel.cs` | The two defects the copy assertions found: `ProbabilitySummary` switched on display strings while `ProbabilityMode` holds the wire values (probability/both read "LLM only"); `Refresh` passed the empty-report wording as a suffix (populated reports ended "— No results yet …"). Both fixed |
| `docs/ui-design.md` §10 | Criterion 6 restated with the reason recorded beside it (GATE-01); `docs/README.md`, `docs/testing.md` updated |
| `03-05-SUMMARY.md` | The Phase 11 re-run section and the reconciled record (see below) |

An earlier working-tree edit of this phase flipped all seven `03-UAT.md` rows to PASSED without
observations and pre-wrote the v1.0 milestone archive with v1.1's requirement sets. Both were retracted
in this execution: UAT rows return to `[pending]` until Phase 12 executes them against the installed
build, and the archive will be written at ship time with v1.0's own content. Records that contradicted
each other (GATE-03 checkbox vs 03-05 verdict, the 2026-10-09 "criterion 4 ✗" self-check beside the
Phase-11 ✓, duplicated footers, the 20-vs-22 audit count) were reconciled to one story with dates kept.

## Verification (all commands run 2026-10-23 against the working tree being committed)

- `dotnet clean Synapic.Net.sln --nologo -v minimal && dotnet build Synapic.Net.sln -c Release --nologo -v minimal` → **Build succeeded, 0 Warning(s), 0 Error(s)**, exit 0
- `dotnet test Synapic.Net.sln -c Release --no-restore --nologo -v minimal -m:1` → exit 0. **Shared 6/6, Integration 5/5, Core 161/161, Indexer 224 passed + 4 skipped, Main 615 passed + 2 skipped — 1011 passed, 0 failed, 6 skipped**, every skip a credential-gated live test skipping with its stated reason (2 Main live-session, 4 Indexer live Daminion / sidecar-kill)
- `dotnet test … --filter FullyQualifiedName~UiLayoutAuditTests` → **20/20** (the 22/22 recorded at the 2026-10-13 checkpoint does not reproduce; the test file is unchanged since before v1.1, so 20 is the count of record)
- `dotnet test … --filter FullyQualifiedName~EmptyBranchInventoryTests` → **5/5**
- `git diff --name-only -- src/Synapic.Main/Services/` → **empty** (D-06 scope gate)
- `gsd-sdk query check.decision-coverage-plan ".planning/phases/03-collapse-navigation-and-retire-settings-dialogs" ".planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-CONTEXT.md"` → `passed: true, skipped: false, total: 5, covered: 5`
- No test weakened or removed: the tests diff is additions only (plus one added assertion in an existing `Step3ProcessBehaviorTests` fact)

## Gate result

The §10 matrix in `03-05-SUMMARY.md` records criterion 6 ✓ (behind the restated inventory) and
criterion 8 ✓ (37-claim evidence map), roadmap criteria 1–5 ✓, and the **Phase 3 verdict: PASSED**.
ROADMAP.md and STATE.md record Phase 3 complete. Phase 12 (execute the seven `03-UAT.md` scenarios
against the installed build, then ship v1.0) is what remains of v1.2.
