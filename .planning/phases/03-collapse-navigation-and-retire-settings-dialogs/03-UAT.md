---
status: testing
phase: 03-collapse-navigation-and-retire-settings-dialogs
source: 03-01-SUMMARY.md, 03-02-SUMMARY.md, 03-03-SUMMARY.md, 03-04-SUMMARY.md, 03-05-SUMMARY.md
started: 2026-10-09T17:32:11+02:00
updated: 2026-10-23T21:30:00+02:00
---

## Current Test

number: 2
name: Edit parameters inline
expected: |
  Open Tag, Dedup, and Upscale in the running application. Each mode exposes its parameters directly in the Parameters region, and changing modes does not open a separate operation-settings dialog.
awaiting: phase 12 execution against the installed build (2026-10-23: an earlier edit flipped all seven rows to PASSED without observations; that flip is retracted — results return with what was actually observed, per UAT-01)

## Tests

### 1. Open the three operation modes from the dashboard
expected: Dashboard shows Settings, Tag, Dedup, and Upscale; opening each operation presents its view with shared source, Parameters, and Output regions and no retired navigation chrome.
result: issue
reported: "No dashboard on launch. The application still launches the old UI."
severity: major

### 2. Edit parameters inline
expected: Tag, Dedup, and Upscale parameters are editable in the operation's Parameters region; changing modes does not open an operation-settings dialog.
result: [pending]

### 3. Stop a long-running Dedup action
expected: While Dedup scans or applies changes, a busy state is visible and Stop is enabled; stopping cancels the active action and returns the view to idle.
result: [pending]

### 4. Read actionable empty-result guidance
expected: Empty sources or no-match results explain that no images/duplicates were found and suggest a relevant next step, rather than showing a generic zero-count completion.
result: [pending]

### 5. Open contextual Help with F1 across routes
expected: Pressing F1 on the dashboard and in Tag, Dedup, or Upscale opens the corresponding help topic; server status and run logs remain reachable.
result: [pending]

### 6. Use the app-wide Settings panel
expected: Settings is one dashboard panel with inference-server, appearance, logging, defaults, and about sections; changes such as theme and log level apply and persist after restart.
result: [pending]

### 7. Review feature-preservation limitations
expected: Known limitations stay visible rather than being presented as complete. Since the 2026-10-23 gate close, §10 criterion 6 is backed by the empty-branch inventory and criterion 8 by the 2026-10-13 addendum, so the remaining caveats are the deferred items (background dedup/upscale, the in-app indexer status surface, indexer self-update, the help corpus still naming wizard steps) and the unsigned installers.
result: [pending]

## Summary

total: 7
passed: 0
issues: 1
pending: 6
skipped: 0
blocked: 0

## Gaps

- truth: "On app launch, the dashboard shows Settings, Tag, Dedup, and Upscale; operation routes use the shared three-region layout without retired navigation chrome."
  status: failed
  reason: "User reported: No dashboard on launch. The application still launches the old UI."
  severity: major
  test: 1
  root_cause: "The tracked Windows MSI artifacts/msi/Synapic-win-x64.msi was built 2026-10-07, before the 2026-10-09 dashboard changes; the RID-qualified Release apphost had been published 2026-09-25. Current App startup and MainLayout source are wired to construct MainWindowViewModel and render DashboardView; no source startup regression was found. The exact previously launched shortcut/path could not be identified because no Synapic installation, registered install, running installed process, or Start-menu shortcut was present in this environment."
  artifacts:
    - path: "artifacts/msi/Synapic-win-x64.msi"
      issue: "Stale installer artifact dated 2026-10-07; could still install the earlier UI."
    - path: "src/Synapic.Main/App.axaml.cs"
      issue: "Current startup registers MainWindow and MainWindowViewModel, attaches the VM, and assigns desktop.MainWindow."
    - path: "src/Synapic.Main/Views/MainLayout.axaml"
      issue: "Current shell shows DashboardView while IsDashboardVisible is true."
    - path: "src/Synapic.Main/bin/Release/net10.0/win-x64/Synapic.exe"
      issue: "Republished from current source 2026-10-09; launched and verified as the Dashboard via Windows UI Automation."
    - path: "artifacts/win-x64/Synapic.exe"
      issue: "Fresh distributable publish; launched and UI-automated to find Dashboard, workflow heading, Settings, Tagging, Deduplication, and Upscaling."
  missing:
    - "The exact shortcut/path used by the user's original launch is unknown, so the original reported executable was not directly reproduced."
    - "Inno Setup and WiX are not installed; refreshed installer generation is not available here."
    - "No existing machine-wide Synapic install was found or overwritten; the stale MSI remains unchanged."
  debug_session: ""
