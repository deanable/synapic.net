# UI Review — Deduplication step (ad-hoc, no phase)

**Audited:** 2026-10-04
**Baseline:** Abstract 6-pillar standards (no UI-SPEC.md exists; no GSD phase docs in this repo — audit scope set to the shipped dedup UI per user decision)
**Screenshots:** Not captured — no dev server (Avalonia desktop app; Playwright-MCP unavailable), code-only audit
**Registry audit:** Skipped — `components.json` absent (no shadcn)

---

## Pillar Scores

| Pillar | Score | Key Finding |
|--------|-------|-------------|
| 1. Copywriting | 3/4 | Precise progress/error/scope strings; no empty-state guidance when a scan finds 0 groups |
| 2. Visuals | 3/4 | Hierarchy and control vocabulary match Step 1 exactly; rendered layout unverified (no screenshot path) |
| 3. Color | 3/4 | Theme-clean — zero hardcoded colors, accent on 3 elements; but four distinct Opacity levels form an ad-hoc text scale |
| 4. Typography | 4/4 | Byte-for-byte parity with the reference step: FontSize {22, 12, default}, FontWeight {Bold, SemiBold, normal} |
| 5. Spacing | 3/4 | `Spacing="14"` is an outlier against the app's 6/8/10/12/16 set; group cards pad 10 vs 14 on same-family source cards |
| 6. Experience Design | 2/4 | **BLOCKER:** local Delete applies permanently across all groups with no confirmation; Apply has no busy/cancel affordance |

**Overall: 18/24**

---

## Top 3 Priority Fixes

1. **BLOCKER — Local `Delete` has no confirmation while catalog delete does** — a single Apply permanently destroys every unchecked file across all groups (help docs state it is not even a Recycle Bin delete), yet only the Daminion branch calls `ConfirmAction`. — In `StepDedupViewModel.ApplyAsync`, call `ConfirmAction` for the local branch too when `action == DedupAction.Delete` (message like `"Permanently delete {n} file(s)? This cannot be undone."`); null hook already fails closed.
2. **WARNING — No busy or cancel affordance during Apply and long scans** — `ApplyAsync` sets no `IsApplying` state, so a multi-minute catalog delete greys the button with no progress or feedback; the Daminion download loop honors a `CancellationToken` but no UI ever cancels it. — Add `IsApplying` + an indeterminate bar next to Apply, and a `Stop` button bound to `ScanCommand.Cancel()` (`IAsyncRelayCommand`) for in-flight scans.
3. **WARNING — Empty result has no guidance copy** — a scan that finds nothing reports only `"1500 files scanned — 0 groups, 0 duplicates"`; before any scan the summary area is blank. — Append a second line when `Groups.Count == 0`: "No duplicates found — try a lower threshold or another algorithm", and a pre-scan hint in `ScanSummary`.

---

## Detailed Findings

### Pillar 1: Copywriting (3/4)

- **No generic labels.** Grep for `Submit|OK|Click here|No data` across the new views: only `Cancel` in `ConfirmDialogWindow.axaml:15`, which is the correct standard label for a modal's safe exit.
- **Strong state copy** (`StepDedupViewModel.cs`): `Scanning…` / `Fetching items from Daminion…` (348), `Hashed {x}/{y}…` (399), `Downloading {index}/{count}: {file}` (429), `Scan failed: {msg}` (375), `Scan cancelled` (371), `Deleted {n} item(s) from the Daminion catalog` (534), `Catalog delete failed — see log` (535). Every state names its own outcome.
- **Destructive prompt names count + irreversibility** (524): "Delete N item(s) from the Daminion catalog? This cannot be undone."
- **Scope read-back** (203–206) tells the user exactly which Step 1 collection feeds the scan.
- **GAP:** no empty-state guidance. `SelectionSummary` collapses to `""` at zero groups (246) and `ScanSummary` reports a bare `0 groups, 0 duplicates` (362). A first-time user gets no next action. — justifies 3, not 4.

### Pillar 2: Visuals (3/4)

- **Hierarchy:** `FontSize="22" FontWeight="Bold"` title → `FontWeight="SemiBold"` section labels → default-weight body → `FontSize="12"` metadata — identical ladder to `Step1Datasource.axaml`.
- **No icon-only controls:** Browse…, Scan, Apply, Cancel, radios and checkboxes all carry text labels; the filename `TextBlock` has `ToolTip.Tip` for truncation (`StepDedup.axaml` item template).
- **Group cards** use `DockPanel` to right-align the keep/acted counts against the header — count is visible where the decision is made.
- **`needs_human_review: true`** — cannot be verified without a rendered window: `Border.card` (defined `MainWindow.axaml:174`) nested inside a `ListBoxItem` may double-pad; the item row grid `Auto,*,150,90` may clip at the wizard's window width; confirm the checkbox column aligns across cards. Playwright/CLI screenshot path does not exist for this desktop app.

### Pillar 3: Color (3/4)

- **Zero hardcoded colors** in `StepDedup.axaml` / `ConfirmDialogWindow.axaml` — the hex grep hits were `#dedup-*` help anchors, not brushes. Everything is theme classes (`Border.card`, `Classes="accent"`).
- **Accent discipline:** exactly 2 `accent` buttons in the step (Scan, Apply) + 1 in the dialog — 3 total, well under the >10 overuse threshold, and all are the screen's primary actions.
- **GAP:** four distinct opacity values in one view — `0.65` (auto-select hint), `0.7` (Daminion note), `0.75` (dates/sizes/group summary), `0.8` (scan summary). That is a de-facto four-step text hierarchy encoded as magic numbers rather than theme resources.
- **`needs_human_review: true`** — contrast of the `0.65` hint against dark theme is unverifiable statically; if the theme's muted brush exists, replace the opacity ladder with `ThemeForeground*` resources.

### Pillar 4: Typography (4/4)

- Dedup step distribution: `FontSize="12"` ×5, `FontSize="22"` ×1; `FontWeight="Bold"` ×1, `FontWeight="SemiBold"` ×4 — **exactly matches** `Step1Datasource.axaml` (`12` ×1, `22` ×1, Bold ×1, SemiBold ×4, its lone `12` being the catalog tooltip).
- Two weights plus default is the app's established system; the abstract >2-weights threshold is technically exceeded (Bold/SemiBold/normal) but adopting a third style here would *diverge* from the design system — no action.
- No `FontFamily` overrides anywhere in the step; the app default carries through.

### Pillar 5: Spacing (3/4)

- `Spacing` values in `StepDedup.axaml`: `12` ×4, `14` ×1, `4` ×2. The app's working set (per Step 1) is 6/8/10/12/16 — **`Spacing="14"` on the auto-select row is an arbitrary value** with no precedent.
- **Card padding inconsistency:** group cards use `Padding="10"` while the source cards in the same view use `Padding="14"` — visually one family, two rhythms.
- Margins are scattered micro-values (`4,2` row inset, `8,0`, `12,0`, `0,0,0,8`, `12,0,0,0`) — individually harmless, collectively no stated scale.
- Arbitrary-value check: no fractional/px-in-brackets values (not applicable to AXAML), no out-of-family numbers besides `14`.

### Pillar 6: Experience Design (2/4)

- **BLOCKER — destructive asymmetry.** `ConfirmAction` is consulted only in the Daminion branch (`StepDedupViewModel.cs:523`); the local branch (540) runs `ApplyToPathsAsync` with `DedupAction.Delete` unchecked, no prompt. `CanApply` (500) gates on targets > 0 only. Permanent, multi-group deletion is one mis-click away while the *catalog* copy of the same action asks first. This contradicts the care taken elsewhere and is the single score-suppressing defect.
- **Loading:** scan states covered — `IsScanning` disables Scan/Apply, indeterminate bar bound in the view, live progress strings, cancel outcome reported (`Scan cancelled`).
- **Error:** `Scan failed: {msg}` + log; `Catalog delete failed — see log`; per-file apply failures flip the result to "completed with errors".
- **Disabled states:** `CanScan` (folder/connection required), `CanApply` (needs targets + valid source); source switch clamps `SelectedAction` and refreshes both commands.
- **Confirmation:** catalog delete fails closed when no hook is wired (test-pinned) — the pattern exists; it is simply not applied to local delete (see blocker) and there is no undo either way.
- **GAP:** no `IsApplying` state — Apply provides no progress feedback, and the scan loop, though token-fed, has no Stop button.

---

## Files Audited

- `src/Synapic.Avalonia/Views/Wizard/StepDedup.axaml` (+ `.axaml.cs`)
- `src/Synapic.Avalonia/Views/ConfirmDialogWindow.axaml` (+ `.axaml.cs`)
- `src/Synapic.Avalonia/ViewModels/Steps/StepDedupViewModel.cs`
- `src/Synapic.Avalonia/Views/Wizard/Step1Datasource.axaml` (reference for app conventions)
- `src/Synapic.Avalonia/Views/MainWindow.axaml` (card style, step hosting)
- `src/Synapic.Avalonia/Services/Processing/DedupService.cs` (apply semantics behind the action list)
- Help contract: `docs/help/dedup.html`, `docs/help/settings-reference.html` (copy consistency with UI)
