---
phase: 1
slug: extract-run-state-and-operation-contract
status: ready
tool: avalonia-xaml
created: 2026-10-08
---

# Phase 1 — UI Design Contract

> **No visible change.** This phase is a presentation-layer *extraction*: the UI contract below
> pins what must look/behave EXACTLY as today, so `git diff` on any `.axaml` is empty and the
> existing pins keep passing. Visual/interaction design for new surfaces lands in Phase 2's spec.

---

## Design System

| Property | Value |
|----------|-------|
| Tool | none — Avalonia XAML (desktop) |
| Preset | not applicable |
| Component library | Avalonia built-in controls + existing project styles (`Classes="card"`, existing `ControlTheme`s) |
| Icon library | existing app assets (no new icon library) |
| Font | app default (Avalonia system default); `Consolas,monospace` for log text (existing) |

---

## Invariants (must be byte-for-byte behavior-identical after extraction)

| Surface | Invariant |
|---------|-----------|
| Progress display | `ProgressPercent`/`ProgressText`/`EtaText`/`CurrentFile` bindings keep working on Step3, Upscale, Dedup with same names and formats |
| ETA | identical mapper → identical strings for identical progress sequences |
| Log | same append semantics, same 2000-line cap, same UI-thread marshalling |
| Cancel/Pause | same command availability rules; no new or removed enabled states |
| Views | **zero** `.axaml` diffs — enforced by Phase 1 Success Criterion 3 |

---

## Spacing Scale

Unchanged — no layout edits this phase. (Design rhythm for Phases 2–3: 2/4/8/12/16 per
`docs/ui-design.md` §3.)

---

## Color

Unchanged — existing theme resources (`SynapicAccentBrush` #2563EB, `SynapicSuccessBrush` #16A34A,
`SynapicDangerBrush` #DC2626, `SynapicWarningBrush` #EA580C) remain the only accent sources.

---

## Copywriting Contract

Unchanged — all existing button/label/status copy stays verbatim.

---

## Registry Safety

| Registry | Blocks Used | Safety Gate |
|----------|-------------|-------------|
| shadcn | not applicable | not required (Avalonia) |

---

## Checker Sign-Off

- [ ] Dimension 1 Copywriting: N/A (no copy changes)
- [ ] Dimension 2 Visuals: PASS criterion = zero view diffs
- [ ] Dimension 3 Color: N/A (no color changes)
- [ ] Dimension 4 Typography: N/A (no font changes)
- [ ] Dimension 5 Spacing: N/A (no layout changes)
- [ ] Dimension 6 Registry Safety: not applicable

**Approval:** approved 2026-10-08 (no-visible-change contract; verified by `git diff --stat` gate)
