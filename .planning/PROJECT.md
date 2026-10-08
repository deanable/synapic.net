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

## Constraints

- C#/.NET 10, Avalonia 11.1, CommunityToolkit.Mvvm; Python 3.11 sidecar unchanged
- All existing functionality preserved (docs/ui-redesign-proposal.md §4 checklist)
- 422+ headless tests must stay green; layout audit gates every UI change

## Links

- `docs/ui-design.md` — single source of truth for UI/UX decisions
- `docs/ui-refactor-plan.md` — evaluation + phase gates this roadmap implements
- `README.md` — migration spec; `plan.md` — original migration phases (historical)
