---
phase: 2
slug: add-operation-template-and-dashboard
status: ready
tool: avalonia-xaml
created: 2026-10-08
---

# Phase 2 — UI Design Contract

> Visual and interaction contract for the shared surfaces: `OperationLayout.axaml` (three regions)
> and `DashboardView` (four panels). Authority for anything unspecified: `docs/ui-design.md`.
> Avalonia adaptation of the GSD UI-SPEC template — shadcn-specific dimensions marked N/A.

---

## Design System

| Property | Value |
|----------|-------|
| Tool | none — Avalonia XAML (desktop) |
| Preset | not applicable |
| Component library | Avalonia built-in controls + existing project styles (`Classes="card"`, existing button/textbox themes) — **reuse, no new control library** |
| Icon library | existing app assets (no new icon library) |
| Font | app default; `Consolas,monospace` for the run log (existing) |

---

## Spacing Scale

From `docs/ui-design.md` §3 — 2/4/8/12/16 rhythm (multiples of 4 for everything ≥ 4px):

| Token | Value | Usage |
|-------|-------|-------|
| xs | 4px | icon gaps, inline padding |
| sm | 8px | compact element spacing, card padding increments |
| md | 16px | default element spacing, panel padding |
| lg | 24px | section padding |
| xl | 32px | region gaps |

Exceptions: none.

---

## Typography

| Role | Size | Weight | Line Height |
|------|------|--------|-------------|
| Body | app default | regular | default |
| Label | app default | regular/semibold for emphasis | default |
| Heading | app default +1 step | semibold | default |
| Log | 12px Consolas | regular | default (existing: 11px in MainLayout list) |

No new fonts or type scale — the template inherits the app's existing text styles.

---

## Color

| Role | Value | Usage |
|------|-------|-------|
| Dominant (60%) | theme background/surface resources (light+dark) | page + panel backgrounds |
| Secondary (30%) | theme card/sidebar resources | Region panels, dashboard cards |
| Accent (10%) | `SynapicAccentBrush` #2563EB | **one accent action per region** (`ui-design.md` §3); exception: Dedup Scan + Apply (§9) |
| Success | `SynapicSuccessBrush` #16A34A | completed/kept states |
| Destructive | `SynapicDangerBrush` #DC2626 | destructive actions only (Remove), Stop styling |
| Warning | `SynapicWarningBrush` #EA580C | warnings only |

Accent reserved for: exactly one primary action per region — Start (Tag), Scan (Dedup),
Upscale (Upscale), Open-panel actions on the dashboard. Everything else secondary.

---

## Layout Contract (this phase's deliverable)

| Surface | Contract |
|---------|----------|
| `OperationLayout` | exactly three regions in order **Data source (A) → Parameters (B) → Output (C)**; zero mode knowledge in the layout itself; stacked variant at narrow widths keeps the same order |
| `DashboardView` | exactly four panels — Settings, Tag, Dedup, Upscale; each opens its view; app cold-starts here (`ShellViewModel.Current == null`) |
| Region A | single shared `DataSourceStrip`, one shell-owned source instance visible identically across modes; dedup duplicate source card deleted |
| Region B | existing settings panels, moved unchanged |
| Region C | shared `RunStateBar` (progress/ETA/current file/log/primary action) + per-mode Report |

---

## Copywriting Contract

| Element | Copy |
|---------|------|
| Primary CTA (Tag) | `Start` (existing) |
| Primary CTA (Dedup) | `Scan` (Apply stays secondary until Phase 3 busy state) |
| Primary CTA (Upscale) | `Upscale` (existing label — verify against StepUpscale.axaml) |
| Dashboard panels | Settings · Tag · Dedup · Upscale (names per `ui-design.md` §2) |
| Empty state | per `ui-design.md` §4/§7; no invented copy — reuse existing strings where present |
| Error state | problem + solution path (existing log/status patterns) |

---

## Registry Safety

| Registry | Blocks Used | Safety Gate |
|----------|-------------|-------------|
| shadcn | not applicable | not required (Avalonia) |

---

## Checker Sign-Off

- [ ] Dimension 1 Copywriting: PASS (CTA copy reuses existing labels; no new strings invented)
- [ ] Dimension 2 Visuals: PASS (regions/panels match `ui-design.md` §2–§3; audit test green)
- [ ] Dimension 3 Color: PASS (existing theme brushes only, one accent per region)
- [ ] Dimension 4 Typography: PASS (no new fonts/type scale)
- [ ] Dimension 5 Spacing: PASS (2/4/8/12/16 rhythm)
- [ ] Dimension 6 Registry Safety: not applicable (Avalonia)

**Approval:** approved 2026-10-08 (derived from `docs/ui-design.md` — the authority for visuals)
