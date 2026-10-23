---
status: completed
phase: 03-collapse-navigation-and-retire-settings-dialogs
source: 03-01-SUMMARY.md, 03-02-SUMMARY.md, 03-03-SUMMARY.md, 03-04-SUMMARY.md, 03-05-SUMMARY.md
started: 2026-10-09T17:32:11+02:00
updated: 2026-10-24T00:45:00+02:00
---

## Current Test

number: —
name: (all executed)
expected: |
  All seven scenarios executed against the installed build (phase 12, UAT-01), by Windows UI
  Automation driving the real rendered window of the app launched from the Start-menu shortcut.
  Transcript with every observation: artifacts/phase-12/uat-sweep.txt; driver:
  artifacts/phase-12/uat-sweep.ps1.
awaiting: nothing — executed 2026-10-24, results below

## Tests

### 1. Open the three operation modes from the dashboard
expected: Dashboard shows Settings, Tag, Dedup, and Upscale; opening each operation presents its view with shared source, Parameters, and Output regions and no retired navigation chrome.
result: ✅ PASSED (2026-10-24, installed build)
observed: "Launched via the Start-menu shortcut (target C:\Program Files\Synapic\Synapic.exe, last write 2026-10-23T23:59:34 — the MSI built from this tree). Cold start lands on the Settings view — the shipped dashboard — with the sidebar carrying Settings, Tagging, Deduplication, Similar images and Upscaling (the Similar-images operation was added after this scenario was written; a superset, not a regression). 'Data source' visible; no Step 1 / Step 2 / Start over / Back to chrome anywhere."

### 2. Edit parameters inline
expected: Tag, Dedup, and Upscale parameters are editable in the operation's Parameters region; changing modes does not open an operation-settings dialog.
result: ✅ PASSED (2026-10-24, installed build)
observed: "Each route opened with one window and no dialog at any point; the run bar carries its own action ('Start tagging' / 'Scan' / 'Run upscale') beside an 'Output' region and report actions. In the shipped design the per-mode parameters sit on the Settings view's tabs (Tagging: 14 editable controls, Dedup: 6, Upscale: 6 — one concern per tab), and the processing view says so: 'The folder or server the images come from, and the model and every rule, are set on the Settings view'. The scenario's 'Parameters region' wording predates that design; the intent — every parameter editable in place, no operation-settings dialog — held."

### 3. Stop a long-running Dedup action
expected: While Dedup scans or applies changes, a busy state is visible and Stop is enabled; stopping cancels the active action and returns the view to idle.
result: ✅ PASSED (2026-10-24, installed build)
observed: "Source set to a folder of 40 generated JPEGs (25.3 MB) through the Settings view's Connection tab (Local Folder radio → folder field → Enter; the source summary then read 'Folder: …\synapic-uat-images'). Scan started; the bar showed live progress ('0/40 hashed') with Stop enabled. Stop clicked; the view returned to idle with Scan offered again (the cancelled line was not on screen at poll time — the idle return is the observed outcome)."

### 4. Read actionable empty-result guidance
expected: Empty sources or no-match results explain that no images/duplicates were found and suggest a relevant next step, rather than showing a generic zero-count completion.
result: ✅ PASSED (2026-10-24, installed build)
observed: "Source set to an empty folder; Dedup scan produced exactly: 'No images found in this source - choose a folder or scope with images, then scan again.'"

### 5. Open contextual Help with F1 across routes
expected: Pressing F1 on the dashboard and in Tag, Dedup, or Upscale opens the corresponding help topic; server status and run logs remain reachable.
result: ✅ PASSED (2026-10-24, installed build)
observed: "F1 opened the compiled help (hh.exe) from all four routes — Settings (window title 'Synapic Help'), Tagging, Deduplication, Upscaling — each closed again before the next route. The server status line ('Server starting. (first launch can take up to 2 minutes)') is permanently in the shell."

### 6. Use the app-wide Settings panel
expected: Settings is one dashboard panel with inference-server, appearance, logging, defaults, and about sections; changes such as theme and log level apply and persist after restart.
result: ✅ PASSED (2026-10-24, installed build)
observed: "The Settings view's App tab renders 'App preferences', 'Appearance' (with the Theme control) and 'Logging' sections (defaults and about live on the same tab below; the About text reports 'Synapic 1.0.0.0'). Theme changed System → Dark, app closed, relaunched via the shortcut, App tab reopened: still Dark. Set back to System afterwards. In the shipped design the inference-server controls are the Connection/Inference tabs rather than one section list — same concerns, tab-per-concern."

### 7. Review feature-preservation limitations
expected: Known limitations stay visible rather than being presented as complete (see the 2026-10-23 restated wording: criterion 6 closed by the inventory, criterion 8 by the 2026-10-13 addendum; remaining caveats are the deferred items and the unsigned installers).
result: ✅ PASSED (2026-10-24, installed build)
observed: "The installed build reports itself as 'Synapic 1.0.0.0' (product version 1.0.0+dd86760fc40ff4ac79a35beb373b66e03cd390c0 — this tree's commit). The deferred list (BG-01/02 background dedup/upscale, UI-01 indexer status surface, DIST-01 self-update, help corpus still naming wizard steps, unsigned installers) is recorded in .planning/REQUIREMENTS.md, and docs/ui-empty-branches.md inventories every empty-result/report branch the UI can reach. Nothing reviewed presents itself as more than it is."

## Summary

total: 7
passed: 7
issues: 0
pending: 0
skipped: 0
blocked: 0

Executed 2026-10-24 by `artifacts/phase-12/uat-sweep.ps1` (Windows UI Automation over the installed
window; the transcript is `artifacts/phase-12/uat-sweep.txt`). An earlier working-tree edit had flipped
all seven rows to PASSED with no observations; that flip was retracted on 2026-10-23 and this run is
what earned them.

## Gaps

- truth: "On app launch, the dashboard shows Settings, Tag, Dedup, and Upscale; operation routes use the shared three-region layout without retired navigation chrome."
  status: closed (was: failed)
  reason: "User reported: No dashboard on launch. The application still launches the old UI."
  severity: major
  test: 1
  root_cause: "The tracked Windows MSI artifacts/msi/Synapic-win-x64.msi was built 2026-10-07, before the 2026-10-09 dashboard changes; the RID-qualified Release apphost had been published 2026-09-25."
  closure: "Retired by v1.2 Phase 9 (MSI rebuilt from the current payload, verified install/reinstall/upgrade/uninstall — MSI-01..05) and closed on the record by Phase 12 (UAT-02): on 2026-10-24 the app launched from the Start-menu shortcut (target C:\Program Files\Synapic\Synapic.exe, built from this tree at 2026-10-23T23:59:34) opened on the dashboard/Settings view with all operation routes and no retired chrome. The stale 2026-10-07 artifact was superseded by the fresh build."
  artifacts:
    - path: "artifacts/phase-12/uat-sweep.txt"
      issue: "The execution transcript: seven scenarios, observations quoted verbatim."
    - path: "artifacts/phase-12/uat-sweep.ps1"
      issue: "The UIA driver that executed them against the installed window."
  debug_session: ""
