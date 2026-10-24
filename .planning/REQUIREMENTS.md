# Requirements: Synapic.NET — v1.4 Service Console application

**Defined:** 2026-10-24
**Core Value:** The service managed by its own application — and a save you can believe.
**Scope:** move v1.3's Settings tab into a separate app and close the fire-and-forget save hole. No new service capability.

## v1.4 Requirements

- [x] **SVC-06 — the split**: the service-management surface (lifecycle, watch folders, queue) lives in its own application, `Synapic.ServiceConsole.exe` ("Synapic Service"), shipped in both installers with its own Start-menu shortcut (Inno + MSI). The main application carries no service-management surface (seven tabs again, no `Synapic.Indexer.Contract` reference); the console carries no engine, sidecar host, or tagging surface.
- [x] **SVC-07 — the verified save**: `--apply-config` writes `<patch>.result.json` (exit code, restarted, messages) before exiting; the console awaits the elevated process (3-minute budget for the UAC prompt) and reads the result, so the note reports *applied and restarted*, *applied for the next start*, the exact refusal messages (edit stays dirty), or that the command never started. A declined prompt or refused config can no longer look like a saved change — the hole that made v1.3's watch-folder save appear not to persist.

## Deferred (carried forward)

Unchanged from v1.3: UI-02 (fuller service history), DIST-01, BG-01/02, help-corpus wizard names, installer signing.

## Out of Scope

| Feature | Reason |
|---------|--------|
| F1/CHM help inside the console | The console has no help system; the topic lives in the main app's compiled help and names the console |
| Wiring the main app's source folder to the service's watch folders | They are different settings; the console's UI now says so instead |
| Sharing one inference sidecar between app and service | A design decision of its own (lifecycle ownership, device/model choice); recorded as a candidate, not smuggled into this milestone |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| SVC-06 | Phase 14 | Complete |
| SVC-07 | Phase 14 | Complete |

**Coverage:** 2 total, 2 mapped, 0 unmapped.

---
*Requirements defined: 2026-10-24*
