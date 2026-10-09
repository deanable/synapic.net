# Synapic.NET

## What This Is

Synapic is a desktop app that tags images with a local vision-language model and writes the
results to image files or a Daminion catalog. It is two processes: an Avalonia/C# host (UI,
orchestration, metadata writing) and a bundled Python sidecar (transformers/torch inference)
talking HTTP/JSON over localhost. This repository is the .NET migration of the original
Python/CustomTkinter app.

## Core Value

Pick a source, run one of the three operations (Tag, Dedup, Upscale), and get correct,
reviewable results written back — without the user ever fighting the UI.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- UI redesign per `docs/ui-design.md` (SSOT): dashboard-first, one shared operation layout
- Presentation-layer refactor per `docs/ui-refactor-plan.md` (the Phase 1–3 work this roadmap tracks)
- §5 app-wide settings view (the dashboard Settings panel: inference server, appearance, logging, defaults,
  about) — **delivered and now owned by plan `03-04`**: it landed as untracked files in 03-02's commit
  (`d79917c`) after 03-01 freed the panel, and the plan was written on 2026-10-09 to adopt it. Its
  must-haves and evidence are in `03-04-SUMMARY.md`, which the `03-05` gate verifies (two rows recorded ✗
  there: a health-details surface, and §5's wide section-nav layout)

## Constraints

- C#/.NET 10, Avalonia 11.1, CommunityToolkit.Mvvm; Python 3.11 sidecar unchanged
- All existing functionality preserved (docs/ui-redesign-proposal.md §4 checklist)
- 422+ headless tests must stay green; layout audit gates every UI change

## Links

- `docs/ui-design.md` — single source of truth for UI/UX decisions
- `docs/ui-refactor-plan.md` — evaluation + phase gates this roadmap implements
- `README.md` — migration spec; `plan.md` — original migration phases (historical)
