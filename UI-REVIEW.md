# UI Review — workflow + control spacing (ad-hoc, no phase)

**Audited:** 2026-10-07
**Baseline:** `docs/ui-redesign-proposal.md` §2 (shell, settings dialog) plus the abstract 6-pillar standards. No GSD phase docs exist in this repo, so the scope is the shipped UI: the shell, the four tagging steps, the deduplication step, the upscaling step and the three settings dialogs.
**Screenshots:** Not captured — this is an Avalonia desktop app and no Skia capture path exists in the tree (the previous audit had the same limitation). The replacement is stronger for this question: **a headless layout audit that measures the real visual tree**, see *Verification* below.
**Previous audit:** `UI-REVIEW.md` (2026-10-04, dedup step) — its blocker is fixed; two warnings are carried forward at the end of this file.

---

## What changed in this pass

The request was two things: the controls overlap in places, and the workflow should be laid out systematically as 1 source & model → 2 operation type → 3 settings on a dialog.

| Pillar | Score | Key finding |
|--------|-------|-------------|
| 1. Copywriting | 3/4 | Precise state copy and the new read-back lines; dedup still reports a bare `0 groups, 0 duplicates` with no next action |
| 2. Visuals | 4/4 | One title per page, one heading per section, settings on dialogs, nothing icon-only; hierarchy is now a style set, not per-view guesswork |
| 3. Color | 3/4 | Zero hardcoded colours in any view; the muted-text ladder is still literal opacities (8 distinct values) instead of two named levels |
| 4. Typography | 4/4 | FontSize 22 / 14 / 12 and Bold / SemiBold / normal, applied through `stepTitle` / `sectionTitle` / `hint` / `fieldLabel` |
| 5. Spacing | 4/4 | **The overlap complaint is fixed and now pinned by measurement** — every screen is clean from 900×600 to 1600×900 |
| 6. Experience Design | 3/4 | The workflow is systematic and settings are per-operation dialogs; no busy/cancel affordance during dedup apply and long scans |

**Overall: 21/24**

(The 18/24 in the previous review was a different, narrower scope — the dedup step alone. Scores are only comparable within one baseline.)

---

## Pillar 1: Copywriting (3/4)

- **The workflow now says what it is.** The sidebar reads `1 · Source & model`, `2 · Operation type`, `3 · Settings…`, then the steps the chosen operation runs (`4 · Process`, `5 · Results`; `4 · Deduplication`; `4 · Upscaling`). The wizard's next-button follows: `Next: Settings →`, `Next: Process →`, `Next: Results →`.
- **Pages that lost their settings report them back** rather than going silent: `ModelSummary`, `PromptSummary`, `ProbabilitySummary` on the tagging settings page; `ScanSettingsSummary` on the dedup page (`PHash · threshold 0.90 · keep oldest · server hash: Exact`); `SettingsSummary` on the upscale page (`fast · 4x · precision auto · output keep · overwrite output`).
- **Empty gates became explanatory.** "No tag field selected" and the partial-selection warning are now their own titled cards instead of a red line beside a checkbox.
- **GAP:** a dedup scan that finds nothing still prints only `1500 files scanned — 0 groups, 0 duplicates` (`StepDedupViewModel.ScanAsync`), and the summary area is blank before the first scan. Carried forward from the previous audit.

## Pillar 2: Visuals (4/4)

- **One title per page** (`stepTitle`, 22px bold), **one heading per section** (`sectionTitle`, 14px semibold), everything else body or `hint`. The engine settings form is now five labelled cards (Model, Device, Tag fields, Scoring, Prompts) instead of one undifferentiated column.
- **Settings live on dialogs, one per operation** (proposal §2.2.3 extended): `EngineSettingsDialog` (tagging), `DedupSettingsDialog`, `UpscaleSettingsDialog`. Each hosts the wizard's own view model — one view model, two views — so the summary and the dialog cannot drift.
- **1 · Source & model renders a real model picker** (`EngineModelPicker`: model combo, refresh/download, device) on both the start screen and step 1, over the same engine view model the dialog edits.
- **Nothing icon-only; no text-only controls either** — every control carries a label or a tooltip, and every settings row that could clip now wraps.
- Replaced a hardcoded `Foreground="Green"` on the "✓ downloaded" marker with `SynapicSuccessBrush`, so it follows the palette.

## Pillar 3: Color (3/4)

- **Zero hardcoded colours in any view.** The only hex values in the UI layer are the semantic palette in `App.axaml` (`SynapicAccentBrush`, `SynapicSuccessBrush`, `SynapicDangerBrush`, `SynapicWarningBrush`) and the card shadow.
- **Accent discipline:** one primary action per page in the tagging flow (Scan in the source panel, Start Processing in Process, Run Upscale Batch in Upscaling); the dedup page carries two (Settings, Scan) plus Apply — deliberate, matching the two phases of that page (scan, then act), and worth re-checking when the apply path gains its busy state.
- **GAP:** the muted-text ladder is still 8 literal opacity values (0.45, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8, 0.85). The new `hint` (0.7 / 12px) and `fieldLabel` (0.85) classes cover the rewritten views; the rest is a mechanical sweep that this pass did not want to bundle with the layout change. Recommended: add a `TextBlock.meta` class (0.55 / 12px) and convert.

## Pillar 4: Typography (4/4)

- A single ladder — 22 (page title) / 14 (section) / 12 (hint) / default (body) — declared once in `App.axaml` and used by every page, including the three dialogs.
- Weights: Bold for the page title only, SemiBold for section headings and status, normal for body. No `FontFamily` overrides outside the log views (Consolas, on purpose).
- Long values ellipsize instead of expanding their row: `ModelSummary`, model id, group item names, the header's record count.

## Pillar 5: Spacing (4/4)

**Defects found by the audit, with the measured geometry:**

| Screen | Defect | Measurement |
|---|---|---|
| Step 1 limits row | `Use fixed 200px thumbnail (fast)` clipped off the card | row needed 1216px, card gave 984px at a 1280 window — 232px past the edge, worse at 1024 |
| Dedup rule row | the keep-set hint text clipped | hint ended at 1262px inside a 1012px row at 1280 — 250px past |
| Tagging settings dialog | threshold `NumericUpDown` clipped, whole form 32px wider than its viewport | row needed 970px in a 960px column; form measured 960px, arranged at 928px |
| Shell header | a long folder path or server URL pushed the count and ⚙ Settings toward the edge | strip sat in an `Auto` column with no bound — now a bounded `*` column with trimming |

**Fixes:** the two settings rows became `WrapPanel`s; the dialogs' insets moved from `ScrollViewer.Padding` (measured un-padded, arranged padded) onto the content, with `AllowAutoHide="False"` so the bar is in the layout on both passes; the header, action bar, footer, route cards and every new panel use wrapping rows and a 2/4/8/12/16 rhythm.

**After:** all 13 audit cases pass at 900×600, 1024×700, 1280×800 and 1600×900 — no sibling overlap and no child past its panel.

## Pillar 6: Experience Design (3/4)

- **The systematic workflow is now the navigation.** The sidebar is the numbered pipeline; `2 · Operation type` is the start screen (the operation chooser) and doubles as the way back from inside a flow; `3 · Settings…` opens the dialog for the operation on screen and lights up while the settings page is showing. An operation's own run steps appear only while that operation owns the wizard — the mode headers switch between them.
- **Contextual settings:** the header, the sidebar entry and the action bar all run one `OpenSettingsCommand`, which opens the dialog belonging to the current route (`CreateSettingsDialog()` is public so the choice is testable without a desktop lifetime).
- **Gates preserved:** routes stay disabled until the source is usable; Next from step 1 validates the datasource; the tag-field gate moved onto the settings page and still blocks Start; navigation locks while a batch runs.
- **GAP (carried forward):** dedup `ApplyAsync` still has no `IsApplying` state and the scan's `IAsyncRelayCommand` is never cancelled from the UI, so a multi-minute catalog delete greys the button with no progress and a long scan has no Stop.

---

## Carried-forward findings (open)

1. **WARNING — no busy or cancel affordance during dedup apply and long scans** (`StepDedupViewModel.ApplyAsync` / `ScanAsync`). Add `IsApplying` + an indeterminate bar beside Apply, and a `Stop` button bound to `ScanCommand.Cancel()`.
2. **WARNING — a scan that finds nothing has no guidance copy.** Append "No duplicates found — try a lower threshold or another algorithm" when `Groups.Count == 0`, and a pre-scan hint in `ScanSummary`.
3. **NIT — the muted-text ladder** (8 opacity values) wants one more named class, see pillar 3.
4. **NIT — the help corpus outside `workflow-overview.html`** still numbers the old steps ("Step 2 — Engine" etc. in `setup-guide.html`, `getting-started.html`, `step*-*.html`). The new prose is on the page the shell links from every HelpScope; the rest is a wording sweep, not a functional gap.

---

## Verification

| Check | Result |
|---|---|
| `dotnet test tests/Synapic.Main.Tests` | **422 passed**, 0 failed (416 before this pass; the layout audit contributes 13 cases across 4 window sizes and the workflow tests 6) |
| `dotnet test Synapic.Net.sln` | 6 + 5 + 422 passed, 0 failed |
| `dotnet build Synapic.Net.sln` | 0 warnings, 0 errors |
| `python docs/help/check-help.py` | `help sources OK: 30 topics, 31 files in [FILES], 135 Contents/Index entries, links and anchors resolve` |
| `UiLayoutAuditTests` | boots the real `App` headlessly, asserts the window really is at the audited size, walks every flow panel and fails on overlapping siblings or a child past its panel bounds — the regression harness for "the controls overlap" |
| `WorkflowOrderTests` | pins the sidebar sequence per operation, the chooser round-trip, the contextual settings dialog per route, the model picker over the engine view model, and that the tagging settings page is a summary (no form controls) while the dialog holds them |

## Files audited / changed

- Shell: [MainLayout.axaml](src/Synapic.Main/Views/MainLayout.axaml), [App.axaml](src/Synapic.Main/App.axaml)
- Steps: [Step1Datasource.axaml](src/Synapic.Main/Views/Wizard/Step1Datasource.axaml), [Step2TagSettings.axaml](src/Synapic.Main/Views/Wizard/Step2TagSettings.axaml), [Step2Engine.axaml](src/Synapic.Main/Views/Wizard/Step2Engine.axaml), [Step3Process.axaml](src/Synapic.Main/Views/Wizard/Step3Process.axaml), [Step4Results.axaml](src/Synapic.Main/Views/Wizard/Step4Results.axaml), [StepDedup.axaml](src/Synapic.Main/Views/Wizard/StepDedup.axaml), [StepUpscale.axaml](src/Synapic.Main/Views/Wizard/StepUpscale.axaml)
- Panels and pickers: [EngineModelPicker.axaml](src/Synapic.Main/Views/Wizard/EngineModelPicker.axaml), [DedupSettingsPanel.axaml](src/Synapic.Main/Views/Wizard/DedupSettingsPanel.axaml), [UpscaleSettingsPanel.axaml](src/Synapic.Main/Views/Wizard/UpscaleSettingsPanel.axaml), [DatasourceSourcePanel.axaml](src/Synapic.Main/Views/Wizard/DatasourceSourcePanel.axaml)
- Dialogs: [EngineSettingsDialog.axaml](src/Synapic.Main/Views/Settings/EngineSettingsDialog.axaml), [DedupSettingsDialog.axaml](src/Synapic.Main/Views/Settings/DedupSettingsDialog.axaml), [UpscaleSettingsDialog.axaml](src/Synapic.Main/Views/Settings/UpscaleSettingsDialog.axaml)
- View models: [MainWindowViewModel.cs](src/Synapic.Main/ViewModels/MainWindowViewModel.cs), [WizardViewModel.cs](src/Synapic.Main/ViewModels/WizardViewModel.cs), [Step1DatasourceViewModel.cs](src/Synapic.Main/ViewModels/Steps/Step1DatasourceViewModel.cs), [Step2EngineViewModel.cs](src/Synapic.Main/ViewModels/Steps/Step2EngineViewModel.cs), [StepDedupViewModel.cs](src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs), [StepUpscaleViewModel.cs](src/Synapic.Main/ViewModels/Steps/StepUpscaleViewModel.cs)
- Tests: [UiLayoutAuditTests.cs](tests/Synapic.Main.Tests/UiLayoutAuditTests.cs), [WorkflowOrderTests.cs](tests/Synapic.Main.Tests/WorkflowOrderTests.cs)
- Docs: [workflow-overview.html](docs/help/workflow-overview.html), [csharp-reference.md](docs/csharp-reference.md), [README.md](README.md)
