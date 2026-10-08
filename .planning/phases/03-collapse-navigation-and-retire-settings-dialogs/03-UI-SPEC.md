---
phase: 3
slug: collapse-navigation-and-retire-settings-dialogs
status: ready
tool: avalonia-xaml
created: 2026-10-08
---

# Phase 3 — UI Design Contract

> Contract for the collapse phase: what disappears, what must remain pixel/behavior-identical, and
> the one behavior addition (dedup Apply busy state). Authority: `docs/ui-design.md` §5, §6, §9, §10.

---

## Design System

| Property | Value |
|----------|-------|
| Tool | none — Avalonia XAML (desktop) |
| Preset | not applicable |
| Component library | existing project styles only — deletion phase adds no controls |
| Icon library | existing app assets |
| Font | app default; `Consolas,monospace` for logs (unchanged) |

---

## Spacing Scale

Unchanged from Phase 2 (2/4/8/12/16). Deletions must not shift the template's spacing — after
removing chrome, Regions A/B/C sit at the same positions defined by `docs/ui-design.md` §3.

---

## Typography / Color

Unchanged. No new brushes, fonts, or type styles are introduced; orphaned styles belonging to
deleted chrome are removed with it.

---

## What Dies (visual removals)

| Surface | Removal |
|---------|---------|
| Numbered sidebar | gone from `MainLayout.axaml` incl. per-route visibility rules |
| Action bar (Back / Next / Start Over) | gone |
| Start-screen chooser | gone — dashboard (Phase 2) is the only entry |
| Step DataTemplates in shell | gone — operations render via `OperationLayout` |
| Settings dialogs (`Engine`/`Dedup`/`Upscale`) | gone — all parameters inline in Region B |

---

## Behavior Addition (the only one)

| Element | Contract |
|---------|----------|
| Dedup **Apply** | `IsApplying` busy state for the ENTIRE operation: indeterminate indicator visible, Apply disabled, **Stop stays enabled**, summary shows applying status; clears on completion/cancel/error. Closes `UI-REVIEW.md` #1. |

---

## Copywriting Contract

Unchanged from Phase 2 — deletions remove strings, they don't rename them. Dashboard and
template copy stays per `docs/ui-design.md`.

---

## Registry Safety

| Registry | Blocks Used | Safety Gate |
|----------|-------------|-------------|
| shadcn | not applicable | not required (Avalonia) |

---

## Checker Sign-Off

- [ ] Dimension 1 Copywriting: PASS (no renames)
- [ ] Dimension 2 Visuals: PASS (deleted chrome leaves no orphans; template unchanged; all nine `ui-design.md` §10 criteria pass)
- [ ] Dimension 3 Color: PASS (no palette change; dead styles removed)
- [ ] Dimension 4 Typography: PASS (unchanged)
- [ ] Dimension 5 Spacing: PASS (template layout unchanged by deletions)
- [ ] Dimension 6 Registry Safety: not applicable (Avalonia)

**Approval:** approved 2026-10-08 (derived from `docs/ui-design.md` — the authority for visuals)
