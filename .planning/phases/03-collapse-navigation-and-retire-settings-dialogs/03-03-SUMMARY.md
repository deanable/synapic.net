---
phase: 03-collapse-navigation-and-retire-settings-dialogs
plan: 03
subsystem: ui
tags: [dedup, apply, busy-state, cancellation, ui-review, ui-design-10]

# Dependency graph
requires:
  - phase: 03-collapse-navigation-and-retire-settings-dialogs
    provides: 03-01 (dialogs retired; the dedup page is the mode's Output region content)
provides:
  - StepDedupViewModel.IsApplying, set before the destructive call's first await and cleared in a finally
  - StopApplyCommand (enabled exactly while IsApplying) and StopScanCommand (cancels either scan entry point)
  - ApplyBusyStateTests (5 facts) — D-04 / ui-design §10 criterion 6's dedup clause
  - UI-REVIEW.md carried-forward finding #1 marked RESOLVED
affects: [03-04 final gate (criterion 6), any future dedup apply work]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Busy state spans the operation, not the interaction: the confirmation prompts settle before IsApplying goes true, so the bar never measures a prompt"
    - "One Stop per long-running action, not one per entry point: StopScanCommand cancels the page's Scan command and the shared bar's Start, because the user cannot tell which one they used"

key-files:
  modified:
    - src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs
    - src/Synapic.Main/Views/Wizard/StepDedup.axaml
    - UI-REVIEW.md
  created:
    - tests/Synapic.Main.Tests/ApplyBusyStateTests.cs

key-decisions:
  - "IsApplying is dedup-only (CONTEXT D-04 scope): the tagging and upscale runs already have the shared RunStateViewModel bar; moving it to the base would give two modes a state nothing binds"
  - "The confirmation gate runs before IsApplying is set — a declined prompt must not flash a busy bar, and the multi-minute part is what §4.3 asks to be visible"
  - "StopApply and StopScan exist as view-model commands rather than binding the toolkit's CancelCommand: their CanExecute is exactly IsApplying / IsScanning, which is what the view needs to bind and what a test can assert"

patterns-established:
  - "A busy-state fact drives the operation through a service-seam TaskCompletionSource, so 'in flight' is a state the test owns and both sides of the real await are observable"

requirements-completed: []

# Metrics
duration: 28min
completed: 2026-10-09
---

# Phase 3 Plan 03: Dedup Apply busy state Summary

**Dedup Apply now runs under an indeterminate bar with Apply gated off and Stop live for the whole operation, both long-running dedup actions are cancellable, and UI-REVIEW finding #1 is closed — 5 new facts, suite 449 passed / 2 pre-existing failures / 451**

## Performance

- **Duration:** 28 min
- **Tasks:** 2
- **Files changed:** 3 changed, 1 created
- **Cases:** 446 → 451 in Synapic.Main.Tests (5 new, 0 replaced)

## Accomplishments

- **The busy state wraps the real work.** [StepDedupViewModel.cs](src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs) carries `IsApplying`, set immediately before the destructive call's first await and cleared in a `finally` — so success, cancellation and a thrown error all settle it and the button can never be left greyed. The confirmation prompts run first, so a declined prompt never flashes a bar.
- **Apply is refused while it runs.** `CanApply` gained `!IsApplying` (and `OnIsApplyingChanged` re-evaluates the command), so the gate the view honors is false for the operation's whole duration.
- **Stop exists for both long-running dedup actions.** `StopApplyCommand` (CanExecute = `IsApplying`) cancels the apply through the shared token; `StopScanCommand` (CanExecute = `IsScanning`) cancels whichever entry point started the scan — the page's `ScanCommand` or the shared run bar's `Start`. The page's scan half had no Stop at all before this.
- **[StepDedup.axaml](src/Synapic.Main/Views/Wizard/StepDedup.axaml)** shows the indeterminate bar and the apply Stop while applying, disables Apply, and shows the scan Stop while scanning.
- **The audit item is closed.** [UI-REVIEW.md](UI-REVIEW.md) finding #1 is marked RESOLVED (2026-10-09) with a reference to the new tests, and the stale pillar-6 "GAP" sentence that restated it now points at the closure. Findings 2–4 are untouched.
- **Five facts, all passing** ([ApplyBusyStateTests.cs](tests/Synapic.Main.Tests/ApplyBusyStateTests.cs)): the busy lifecycle across a held operation (`false → true → false`, one service call, the summary written), refused re-entry while in flight, cancel-clears-busy (`dedup.Cancelled` true, `IsApplying` false, Stop disabled), the idle state, and the rendered card (Apply disabled, Stop visible and enabled, exactly one visible indeterminate bar).

## Verification

- `dotnet test ... --filter "FullyQualifiedName~ApplyBusyStateTests"` → **5 passed, 0 failed**
- `dotnet test Synapic.Net.sln -c Release` → Shared **6/6**, Integration **5/5**, Main **449 passed / 2 failed / 451**; the two failures are the pre-existing, machine-dependent `ServerDetectionTests` device facts documented in 03-01-SUMMARY.md (unchanged by this plan, no new regressions)
- `grep -n "RESOLVED" UI-REVIEW.md` → finding #1 resolved line present
- `git diff --name-only -- src/Synapic.Main/Services/` → **empty** (Apply's write semantics untouched)
- Build: 0 warnings / 0 errors (part of the filtered run above)

## Decisions Made

- `IsApplying` stays on `StepDedupViewModel` rather than moving to `RunStateViewModel`: the plan's `interfaces` block says so explicitly, and tagging/upscale already have the shared bar for their own runs.
- One Stop per action rather than per entry point, so the user gets a working Stop regardless of how the run started.
- The apply Stop is a view-model command (not the toolkit's `CancelCommand`) so its enablement is exactly `IsApplying`, which is both the binding the view needs and the assertion a test can make.

## Deviations from Plan

1. **[Rule 1 — the audit item is one item]** The plan's acceptance listed only the apply clause (`IsApplying` + bar + Stop), but UI-REVIEW finding #1 covers the scan too ("no busy or cancel affordance during dedup apply **and long scans**"). Closing half of it would have left the finding legitimately open, so `StopScanCommand` and the scan card's Stop were added as well. Files modified: `StepDedupViewModel.cs`, `StepDedup.axaml`. Verification: `StopScanCommand`'s CanExecute is `IsScanning`; the lifecycle fact and the card fact pass.
2. **[Rule 1 — stale restatement]** The plan said to touch only finding #1's status lines, but pillar 6 restated the same finding as an open GAP; that sentence now points at the closure (scores and other findings unchanged).
3. **[Rule 1 — the toolkit's re-entry guard is not where it looks]** The first draft of the re-entry fact called `ApplyCommand.ExecuteAsync` a second time and deadlocked: `AsyncRelayCommand.ExecuteAsync` has no concurrency guard (only `CanExecute` does, which is what the view honors), so the second call really started a second run and waited on the same gate. The fact now pins the gate the UI honors (`CanExecute` false while in flight, one service call) and the rendered disabled Apply button — the honest, testable statement. No product change: Avalonia disables a button whose command reports `CanExecute` false.

**Total deviations:** 3 auto-fixed (all bug-level, none widening scope beyond the finding being closed). **Impact:** the finding is closed as a whole rather than in half; nothing weakened.

## Issues Encountered

- Two pre-existing `ServerDetectionTests` failures remain (no CUDA driver on this machine makes `TrySelectDevice("cuda")` fall back to CPU, so the device-mismatch expectations flip). Documented with a clean-HEAD reproduction in 03-01-SUMMARY.md. This plan did not touch them; the phase gate in 03-04 needs a decision on whether to make those two facts machine-independent via the `availabilityProbe` seam the constructor already offers.

## Next Phase Readiness

- ui-design §10 criterion 6 now holds for dedup (`Apply` busy + Stop) and its scan; the tagging/upscale clauses were already carried by the shared bar and their own pages.
- 03-02 (chrome collapse) is unaffected by this plan's changes: nothing here binds the sidebar, the action bar or the step chain.
- 03-04's sweep can quote `ApplyBusyStateTests` by name for criterion 6 and cite the UI-REVIEW closure line for the audit item.

---
*Phase: 03-collapse-navigation-and-retire-settings-dialogs*
*Completed: 2026-10-09*

## Self-Check: PASSED

- Key files exist on disk: `ApplyBusyStateTests.cs`, `IsApplying` present in `StepDedupViewModel.cs` and `StepDedup.axaml`.
- All task acceptance criteria re-run and PASS (greps, filtered run 5/5, Services diff empty, RESOLVED line present).
- Plan-level verification re-run: full solution suite 6 + 5 + 449 passed with only the 2 documented pre-existing failures.
