# Synapic.NET Architecture

High-level overview. For the full picture see
[`codebase-guide.md`](codebase-guide.md), plus [`csharp-reference.md`](csharp-reference.md)
and [`sidecar-reference.md`](sidecar-reference.md).

Two-process design per the migration spec (README §2):

```
┌────────────────────────────────────────────┐
│ SYNAPIC APPLICATION BUNDLE                 │
│  ┌──────────────────┐   HTTP/JSON          │
│  │ AVALONIA (C#)    │ ◄───────────────┐    │
│  │  Wizard (1–4+D)  │  127.0.0.1:port │    │
│  │  Services        │                 │    │
│  └──────────────────┘   ┌──────────────┴──┐ │
│                         │ PYTHON SIDECAR  │ │
│                         │ synapic-inference│ │
│                         │  FastAPI+uvicorn │ │
│                         │  HF/transformers │ │
│                         └─────────────────┘ │
└────────────────────────────────────────────┘
```

## Components

### Windows indexer service (`src/Synapic.Indexer`)
An unattended host for the same engine: `net10.0-windows`, installed by `sc.exe` (`--install`), reading its
configuration from `HKLM\SOFTWARE\Synapic\Indexer` overlaid by `%ProgramData%\Synapic\indexer-config.json`.
It logs to one rolling file plus the Application Event Log, owns the sidecar's lifetime (launch, `/health`
readiness, idle stop, crash-leftover cleanup) and stops inside a 10-second budget. Detection feeds a SQLite
queue (`state\indexer.db`, WAL): an interval scan of the configured roots, a debounced `FileSystemWatcher` with
an exclusive-read probe, and a Daminion untagged query through the same `WorkflowRunner.FetchItemsAsync` the
desktop uses. Every detector enqueues through one `IWorkIntake`, keyed by canonical path or catalog id, so
re-detection is a no-op; `--status` reads the queue without starting the host, and `InFlight` rows left by a
crash return to `Pending` before the first scan. Processing those rows is phase 7.

### Engine core (`src/Synapic.Core`)
The headless half of the product: a `net10.0` library that references `Synapic.Shared` only. It holds no UI
framework reference, so a Windows service can run the same code the desktop does. The desktop app and any
future host consume it; nothing in it reaches back into the app.
  - `InferenceSidecarService` — sidecar process lifecycle (see below), launched for the device an
    `ISidecarDeviceProvider` reports
  - `InferenceApiClient` — typed HTTP client for the sidecar API
  - `DaminionApiClient` (Refit) — Daminion REST: fetch, thumbnails, metadata write
  - `ProcessingOrchestrator` / `WorkflowRunner` — batch kernel: fetch → /tag → write, bounded parallelism,
    pause/resume, per-item status
  - `MetadataWriterService` — EXIF/IPTC/XMP writes (JPEG APP1 XMP, PNG iTXt, TIFF sidecar)
  - `SynapicLog` (Serilog facade) — file sink plus a host-supplied sink configuration

### Avalonia frontend (`src/Synapic.Main`)
- **ViewModels** (CommunityToolkit.Mvvm): `MainWindowViewModel` (shell, server indicator, Start/Stop Server), `WizardViewModel` (step navigation + validation gates), per-step VMs under `ViewModels/Steps/`.
- **Services**:
  - `DedupService` — pHash/dHash/aHash via NetVips over EXIF-normalised, alpha-flattened pixels, Union-Find grouping; the one perceptual-hash scan behind both the Deduplication and the Similar images operations (`ImageMatchProfile`, starting at 0.90 vs 0.84 under the default pHash; each fingerprint carries its own default threshold, and the allowed bit distance is floored)
  - `ConfigService` / `UpdateCheckService`; `SessionSidecarDeviceProvider` supplies the Core sidecar the device
    chosen in Step 2 (`EngineServiceRegistration` wires the engine services in one place)
  - `CrashReporterService` — local-only crash capture: global exception handlers write per-crash reports (exception + session-log snapshot) to `logs/crashes/`; non-terminal crashes also show a dialog with a copy-diagnostics button. Nothing is sent over the network.
  - `TelemetryService` — opt-in (`ui.telemetryEnabled`, default false) local usage counters in `logs/synapic-usage.json`: launches, batches, items, model usage, dedup runs. No network, no paths, no identifiers.

### Python sidecar (`src/Synapic.Inference`)
| Module | Responsibility |
|--------|----------------|
| `service.py` | FastAPI app; port-file write; /health /tag /models/* /config /shutdown |
| `model_loader.py` | device probe, HF download/load/cache, label-confidence + CLIP scoring |
| `inference_engine.py` | `run_inference()` — task routing (VLM chat, caption, zero-shot, classification) |
| `tag_extractor.py` | raw model output → (category, keywords, description) |
| `keyword_scoring.py` | tier contract + softmax math |
| `json_utils.py` | JSON/literal extraction from model text |

Inference logic is a **faithful port** of the original Python app; only the I/O
boundaries changed (UI queues → HTTP).

## Sidecar lifecycle (README §2)

1. **Auto-launch (default):** the sidecar starts with the app (unless `ui.autoLaunchSidecar` is `false` in config.json, in which case the user presses **Start Server** to trigger this same sequence).
2. Avalonia launches `synapic-inference --port=0` with `SYNAPIC_PORT_FILE=%TEMP%/synapic_port_{pid}.txt`, `HF_HOME=%LOCALAPPDATA%/Synapic/models` (or `~/.cache/synapic/models`) and `SYNAPIC_DEVICE=<the Step 2 device>`. The device picks the variant to launch first: `cuda` resolves to the CUDA build when one is on disk, everything else to the CPU build.
3. The sidecar binds an OS-assigned port and writes `port\npid\n` to the port file.
4. Avalonia reads the port, states the device again with `PUT /config` (so a sidecar that ignores `SYNAPIC_DEVICE` — anything built before it existed — is configured too), then polls `/health` until `status == "ready"` (max 120 s). The `device` it reports is the one the model actually loaded on, and the status bar shows it.
5. **Inference:** `POST /tag` with a 5-minute timeout; one automatic retry on 503 (model loading).
6. **Manual launch (opt-out):** with `ui.autoLaunchSidecar: false` in config.json the server stays stopped until the user presses **Start Server**; the setting is config-file-only (there is no Options panel in the shipped UI).
7. **Shutdown (always):** app exit → `POST /shutdown` → 5 s grace → `Process.Kill(entireProcessTree: true)`. The sidecar never outlives the app.
8. **Crash detection:** a liveness watcher detects unexpected process exit and surfaces "Server stopped unexpectedly" in the UI; the user can restart from the toolbar.

## Status machine

`Stopped → Starting → Ready | Error`, exposed via `StatusChanged` and bound to
the toolbar indicator + Start/Stop button enablement.

## Engine routing

- **local** → sidecar `/tag` — the only engine (cloud providers OpenRouter/Groq
  were removed from scope; there are no API keys and no `ISecretStore`)

## Concurrency (short version)

The batch loop runs up to **4 items in parallel** (`SemaphoreSlim` in
`ProcessingOrchestrator`, hard-coded in Step 3). The sidecar is a single uvicorn
process whose `/tag` endpoint is a sync handler, so requests run on Starlette's
worker threadpool against **one shared cached pipeline**; model construction is
serialized by a lock. See [codebase-guide §6](codebase-guide.md#6-concurrency-model-important)
for the GPU/VRAM caveats.

## Data flow for a batch (Step 3)

```
Step1 selection ─► DatasourceSelection
                     │ (local recursive scan | Daminion 500-item pages,
                     │  walked to an empty page for dedup/similar)
                     ▼
              ProcessingOrchestrator.RunAsync
                     │ per item (SemaphoreSlim ≤ 4):
                     │  1. obtain image (local path | Daminion temp download per resizeScale)
                     │  2. POST /tag (sidecar)
                     │  3. write metadata (file XMP/IPTC | Daminion BatchChange)
                     │  4. record ProcessItemResult (incl. probabilities + scoring tiers)
                     ▼
              Session.Results ─► Step 4 grid / CSV export
```
