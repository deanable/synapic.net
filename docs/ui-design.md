# Synapic UI Design — Single Source of Truth

**Status:** Approved design — **the** authority for UI/UX decisions
**Created:** 2026-10-08
**Supersedes:** [`ui-redesign-proposal.md`](ui-redesign-proposal.md) (v1.0 draft, now historical)
**Authoritative mockups:** [`mock-up/UI Flow - Dash.svg`](mock-up/UI%20Flow%20-%20Dash.svg), [`mock-up/UI Flow - Operation.svg`](mock-up/UI%20Flow%20-%20Operation.svg)
**Audience:** anyone building, reviewing, or documenting the Synapic desktop UI

> **How to use this document.** If a UI question is answered here, this answer
> wins — including over code that ships today, over the help corpus, and over
> older docs (`README.md` spec §5, `plan.md`, `UI-REVIEW.md`). To change a
> decision, edit this file in the same PR that changes the code. A doc that
> disagrees with this one is stale, not an alternative.

---

## 1. Design intent

The app is **functional but confusing**. The shipped UI exposes a numbered
sidebar pipeline (`1 · Source & model` … `5 · Results`), a route chooser, three
modal settings dialogs, a toolbar, and an always-visible log strip — five
navigation concepts for three workflows. The redesign replaces all of it with
two screens:

1. **Dashboard** — the app's starting screen: four panels.
2. **Operation layout** — one shared three-region form that every mode
   (Tag, Dedup, Upscale) uses to do its work.

Everything a user can reach must be reachable from these two screens.

---

## 2. Information architecture

### 2.1 Dashboard (start screen)

From `UI Flow - Dash.svg` — a 2×2 grid of four equal panels:

```
┌─────────────────────────┬─────────────────────────┐
│                         │                         │
│        Settings         │          Tag            │
│                         │                         │
├─────────────────────────┼─────────────────────────┤
│                         │                         │
│         Dedup           │        Upscale          │
│                         │                         │
└─────────────────────────┴─────────────────────────┘
```

- The app **always starts here** (decision D1).
- Four panels: **Settings**, **Tag**, **Dedup**, **Upscale** (decision D2,
  confirmed against the mockup: Settings + 3 modes = 4 panels).
- Each panel is one click → one destination view. There is no wizard, no
  step numbering, and no other start screen.

### 2.2 Operation layout (shared by Tag, Dedup, Upscale)

From `UI Flow - Operation.svg` — every mode renders the **same** form:

```
┌───────────────────────────────────────────────────────────────────┐
│  DATA SOURCE  (full-width strip, top of form)                     │
│  local folder / Daminion · scope & filters · record count         │
├──────────────────────────────┬────────────────────────────────────┤
│                              │                                    │
│  PARAMETERS                  │  OUTPUT / REPORT                   │
│  (bottom-left)               │  (bottom-right)                    │
│                              │                                    │
│  mode-specific configurable  │  run controls, progress, log,      │
│  settings                    │  results, mode-specific actions    │
│                              │                                    │
└──────────────────────────────┴────────────────────────────────────┘
```

Reading order is deliberate: **what to work on → how to work on it → what
happened**. The three regions are the same controls-in-the-same-places in all
three modes; only the *contents* of Parameters and Output change per mode
(§4).

### 2.3 Settings (own view, not an operation layout)

The Settings panel opens a dedicated sectioned view. It does **not** use the
operation layout — it has no data source and no output (§5).

---

## 3. Dashboard specification

| # | Panel | Opens | Enabled when |
|---|-------|-------|--------------|
| 1 | **Settings** | Settings view (§5) | always |
| 2 | **Tag** | Tag mode (operation layout) | always (run action is gated, not entry — D6) |
| 3 | **Dedup** | Dedup mode | always |
| 4 | **Upscale** | Upscale mode | always |

**Panel content** (minimum): panel title, one-line description of the mode,
and a live status line — current record count from the shared data source and
the mode's last activity (e.g. `2,847 in scope · last run: 1,247 tagged`,
`last scan: 12 groups`). Panels are plain clickable cards: no nested menus, no
secondary actions on the dashboard itself.

**Status line data** comes from the session; if there is none yet, show the
neutral hint (`no source configured yet`).

---

## 4. Operation layout specification

### 4.1 Region A — Data source (top, full width)

Shared, single-instance state (D4): configured once, shown identically in
every mode, editable in place. Contents:

| Element | Local folder | Daminion server |
|---|---|---|
| Type selector | Folder path + **Browse…** | Server URL + credentials |
| Connectivity | — | connection status, **Test Connection / Reconnect** |
| Scope | ☐ Recursive scan | catalog, scope, filters (status, untagged fields) |
| Count | record count in scope | record count in scope (**Resync**) |
| Limits | max items / pagination (shared processing limit) | same |

The strip always states whether the source is usable and *why not* when it
isn't (e.g. `Daminion not connected — Test Connection`). It is the single
place source problems are surfaced; no toasts, no toolbar warnings.

### 4.2 Region B — Parameters (bottom-left)

The mode's configurable parameters, inline on the form (D3 — **the modal
settings dialogs are retired**; their content moves here, bound to the same
view models). Structure: labelled groups in a scrollable column, one primary
**Apply/Save is not needed** — values bind live to the session and are
persisted like today's `config.json` writes. Controls follow the standards in
§7.

### 4.3 Region C — Output / Report (bottom-right)

Owns the run lifecycle end-to-end:

1. **Primary action** for the mode (exactly one accent button): `Start
   tagging` / `Scan` / `Run upscale` — plus `Stop` while running.
2. **Progress**: counts (done / pending / failed), ETA, state
   (idle → running → paused → done / failed).
3. **Report**: the mode's result surface (§4.4), scrollable.
4. **Actions**: mode-specific follow-ups (export, retry, apply…).
5. **Run log**: the colorized log for this run lives here, not in a global
   strip (D7).

Mandatory states, all with copy (carried forward from `UI-REVIEW.md`
open findings):

- **Empty/pre-run state**: says what will happen and what is missing
  (`No duplicates found — try a lower threshold or another algorithm`).
- **Busy state**: indeterminate progress + enabled `Stop` for the whole
  duration of any operation over ~1 s, including dedup **Apply** (a
  multi-minute catalog delete must never be a silently greyed button).
- **Failure state**: per-item failures summarised with a **Retry failed**
  action.

### 4.4 Per-mode contents

**Tag**

| Parameters | Output / Report |
|---|---|
| Model picker (model, refresh, download status) · Device (CPU/CUDA/MPS + fallback warning) · Tag fields (keywords / categories / description) · Scoring (confidence threshold, probability mode `llm/prob/both`, probability threshold, candidate labels) · Prompts (presets + custom system prompt) · Processing limits (max items, resize, thumbnail override) | Start / Pause / Stop · progress + ETA · results grid (file, status, tags) · **Export CSV** · **Retry failed** · **Verify Daminion writes** · run log · settings read-back line (`LFM2.5-VL · cuda · keywords+description · threshold 0.30`) |

**Dedup**

| Parameters | Output / Report |
|---|---|
| Algorithm (pHash / dHash / aHash / color-moment / exact server hash match) · Similarity threshold (default 0.90) · Resize before hash · Keep-set auto-select rule (oldest / newest / smallest / largest) · Apply action (tag / move / delete; catalog removal confirmed) | **Scan** + Stop · scan progress · `N files scanned — G groups, D duplicates` summary (with guidance copy when 0) · duplicate-group cards: thumbnails, per-item keep checkboxes, auto-select buttons · **Apply** with busy state + confirmation · run log · settings read-back line (`PHash · threshold 0.90 · keep oldest · server hash: Exact`) |

**Upscale**

| Parameters | Output / Report |
|---|---|
| Preset (quality / realistic / balanced) · Scale factor (2× / 4×) · Precision (fp32 / fp16) · Output format (PNG / JPEG) · Quality / SNR · Sharpen · Output folder & overwrite policy | **Run upscale batch** + Stop · progress + ETA · completed/failed output list · **Open output folder** · **Retry failed** · run log · settings read-back line (`fast · 4x · precision auto · output keep · overwrite output`) |

---

## 5. Settings view

Own layout: a sectioned list (section nav on the left, section content on the
right — or a single scrolling column on narrow windows). Sections:

| Section | Contents |
|---|---|
| **Inference server** | status (stopped / starting / ready / error), Start/Stop Server, auto-launch toggle, Build/Download sidecar variants (CPU/CUDA), device fallback warning, health details |
| **Appearance** | theme (system / light / dark), density notes |
| **Logging** | log level, open log folder, diagnostics drawer toggle |
| **Defaults** | defaults applied to a new session (default device, default thresholds) |
| **About** | version, links (help home, repository) |

App-wide settings live **only** here; per-operation tunables live **only** in
a mode's Parameters region. Nothing is configured in two places.

---

## 6. Shell, navigation, and state

### 6.1 Shell chrome (all routes)

A slim top bar only (D7): app title · **breadcrumb** (`Dashboard / Tag`)
· server status indicator (colour + text) · **Help** (F1, scoped) · Settings
shortcut. No toolbar button cluster, no numbered sidebar, no persistent log
strip — diagnostics open from Settings, run logs live in Output.

### 6.2 Routes

```
dashboard  →  tagging | dedup | upscale | settings
any route  →  dashboard          (breadcrumb / Esc-free, always available)
```

- Route constants today: `HomeRoute`, `TaggingRoute`, `DedupRoute`,
  `UpscaleRoute` — `HomeRoute` becomes the dashboard, and a `SettingsRoute`
  is added.
- **Back to dashboard preserves route state**; returning resumes where the
  user left off (as `GoHome` does today).

### 6.3 Gating rules (D6)

- **Entry is free**: any panel opens any mode regardless of source state.
  The dashboard must never be a dead end with disabled cards.
- **Action is gated**: the Output primary action stays disabled until the
  data strip has a usable source (and, for Tag, a selected model + tag
  field). The gate message appears **in the region that is blocked** —
  missing source → data strip; missing tag field → Parameters.
- **Run lock**: while a batch runs, leaving the mode or switching modes asks
  for confirmation (as today's navigation lock, but explicit).

### 6.4 State model

| State | Owner | Lifetime |
|---|---|---|
| Data source (type, path, Daminion session, scope, filters, count) | shared session | app run + persisted to `config.json` |
| Per-mode parameters | existing step view models (`Step2Engine`, `StepDedup`, `StepUpscale`) | app run + persisted |
| Per-mode output/report | same view models | until replaced by next run |
| App settings | `ConfigService` | persisted |

---

## 7. Visual & interaction standards

Carried forward unchanged from `UI-REVIEW.md` (they passed audit) — this
document adopts them as requirements:

- **Typography ladder:** 22 page title (dashboard/operation heading) / 14
  section heading / 12 hint / default body, via `stepTitle`, `sectionTitle`,
  `hint`, `fieldLabel` classes in `App.axaml`.
- **Colour:** zero hardcoded colours in views; semantic brushes only
  (`SynapicAccentBrush`, `SynapicSuccessBrush`, `SynapicDangerBrush`,
  `SynapicWarningBrush`). One accent action per region; two on Dedup
  (Scan, Apply) is allowed and intentional.
- **Spacing:** 2/4/8/12/16 rhythm, wrapping rows, no child past its panel —
  layout audit must pass 900×600, 1024×700, 1280×800, 1600×900.
- **Responsive:** below ~1000 px the operation layout stacks vertically in
  reading order (Data source → Parameters → Output); the dashboard grid
  becomes 2×1 then 1×4.
- **Keyboard:** Tab cycles with visible focus ring; Esc closes transient
  overlays; Alt+Home → dashboard; F1 context help per route.
- **Copy:** every status is a sentence with a next action; no bare counts
  (`0 groups, 0 duplicates` alone is a defect).

---

## 8. Functional preservation map

Everything in `ui-redesign-proposal.md` §4 remains in scope; this table fixes
*where* it goes:

| Capability | Lands in |
|---|---|
| Local folder + recursive scan | Data source strip (shared) |
| Daminion URL/auth/catalog/scope/filters/Test Connection | Data source strip (shared) |
| Max items / pagination | Data source strip (shared limits row) |
| Model picker, device, thresholds, probability, prompts, tag fields | Tag → Parameters |
| Processing limits (resize, thumbnail) | Tag → Parameters |
| Results grid, CSV export, retry, verify Daminion | Tag → Output |
| Hash algorithm, threshold, server-hash match, auto-select keep rule | Dedup → Parameters |
| Duplicate group review, bulk Tag/Move/Delete, catalog removal | Dedup → Output |
| Upscale presets, scale, precision, format, quality/SNR, sharpen, overwrite | Upscale → Parameters |
| Upscale batch progress + output files | Upscale → Output |
| Start/Stop/Build server, variants, health, fallback warning | Settings → Inference server (+ status in shell) |
| Log view | Output run log per mode; full diagnostics in Settings → Logging |
| Themes, help, config persistence | Settings + shell (as today) |

---

## 9. Implementation mapping

Current → target. View models are **reused, not rewritten**; what changes is
which view hosts them and how navigation works.

| Current (ships today) | Target |
|---|---|
| `MainLayout.axaml` — numbered sidebar, toolbar, log strip | Slim shell: top bar + single content region; log strip removed |
| Start screen route cards (`IsHomeVisible`, gated `CanStartRoute`) | `DashboardView` — 4 panels, always enabled (D6) |
| Wizard step navigation (`Step1…Step4`, Next/Back commands) | Operation layout regions; step VMs host Parameters/Output content |
| `EngineSettingsDialog` / `DedupSettingsDialog` / `UpscaleSettingsDialog` | **Retired** — content moves to the mode's Parameters region, same VM (`OpenSettingsCommand`/`CreateSettingsDialog` go away) |
| `DatasourceSourcePanel` (per step) | One shared `DataSourceStrip` in the operation layout header |
| `MainWindowViewModel.Route` = home/tagging/dedup/upscale | + `SettingsRoute`; home renders the dashboard |
| `Step2TagSettings` summary page, `Step3Process`, `Step4Results` | Collapsed into Tag's Output region (summary line + progress + grid) |
| `StepDedup`, `StepUpscale` pages | Split into `DedupParameters` / `DedupOutput`, `UpscaleParameters` / `UpscaleOutput` |

New views: `Dashboard/DashboardView.axaml`, `Operation/OperationLayout.axaml`
(three regions), six mode region views (Tag/Dedup/Upscale × Parameters/Output).

**Tests to update** (they pin the *old* navigation and will fail first):
`WorkflowOrderTests`, `RouteSplitTests`, `UpscaleRouteTests`,
`UiLayoutAuditTests`, `HelpScopeTests` (help anchors follow the new regions).

---

## 10. Acceptance criteria

The redesign is done when all of these are verifiable:

1. **Cold start lands on the dashboard** with exactly four panels — Settings,
   Tag, Dedup, Upscale — each opening its view.
2. **One layout, three modes:** a headless layout test walks Tag, Dedup and
   Upscale and asserts the same three regions exist in the same order
   (`DataSource` → `Parameters` → `Output`) on every route.
3. **Source is shared:** configure the source inside Tag, switch to Dedup,
   and the strip shows the same source and record count.
4. **No operation settings dialogs remain** in the tree; every operation
   parameter is reachable inline in the Parameters region.
5. **Layout audit green** at 900×600 … 1600×900 for dashboard + all three
   operation routes, including the stacked (narrow) variant.
6. **Every long-running action has busy + Stop**, including dedup Apply;
   every empty result has guidance copy.
7. **Server status and F1 help work from every route**; run logs are in
   Output, diagnostics reachable from Settings.
8. **Feature preservation:** `ui-redesign-proposal.md` §4 checklist still
   passes item-by-item (§8 table above).
9. `dotnet build` 0 warnings/errors; full test suite green.

---

## 11. Decisions

| # | Decision | Status |
|---|---|---|
| D1 | The app always starts on the Dashboard. | Decided (user, 2026-10-08) |
| D2 | Dashboard = 4 panels: Settings + Tag + Dedup + Upscale (Settings + 3 modes). Not 5 tiles; no fourth mode exists or is reserved. | Decided (user, 2026-10-08, confirmed against mockup) |
| D3 | Per-operation parameters are **inline** in the operation layout; the three modal settings dialogs are retired. (Supersedes proposal §2.2.3 and the `UI-REVIEW.md` "settings live on dialogs" pattern.) | Decided (from mockup) |
| D4 | One shared data-source state across all modes — never re-selected per workflow. | Decided (proposal §3.1, retained) |
| D5 | Navigation is Dashboard ⇄ mode; no numbered steps, no wizard chain. | Decided (user) |
| D6 | Entering a mode is free; only *actions* are gated, with the gate shown in the blocked region. Replaces `CanStartRoute` card gating. | Decided (design) |
| D7 | No always-visible global log strip; run logs live in Output, diagnostics in Settings. | Decided (design) — confirm during review |

## 12. Open questions

1. **Dashboard panel summaries** (§3): record count + last activity per mode
   — confirm the exact fields, especially for Settings (server status only?).
2. **Launch route persistence:** always dashboard (D1) vs restoring the last
   mode — D1 currently wins; revisit if beta feedback asks for resume.
3. **Help corpus sweep:** `docs/help` topics still number the old wizard
   steps (`setup-guide.html`, `getting-started.html`, `step*-*.html`); they
   must be re-scoped to Dashboard / Data source / Parameters / Output.
4. **Dedup dual accent** (Scan + Apply) — keep as intentional exception to
   one-accent-per-region, or move Apply to secondary until scan completes.

## 13. Related documents

| Document | Role after this design |
|---|---|
| [`ui-redesign-proposal.md`](ui-redesign-proposal.md) | **Historical** — superseded by this document; kept for rationale and the §4 feature checklist |
| [`UI-REVIEW.md`](../UI-REVIEW.md) | Audit of the pre-redesign shipped UI; its standards are adopted (§7) and its open findings are requirements here (§4.3) |
| [`README.md`](../README.md) (spec) §5 | Migration-era wizard description — historical |
| [`plan.md`](../plan.md) | Phase plan — historical; UI phases reference this doc |
| [`help/`](help/README.md) | User help — must be re-scoped to this design (§12.3) |
