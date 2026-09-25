# Synapic.NET — Codebase Guide

The master orientation document. Deep dives live in
[`csharp-reference.md`](csharp-reference.md) (Avalonia host) and
[`sidecar-reference.md`](sidecar-reference.md) (Python inference engine).

## 1. What the application does

Synapic is a desktop wizard that tags image libraries with a **local**
vision-language model (default `LiquidAI/LFM2.5-VL-450M`) and writes the
results either into the image files' metadata or back into a **Daminion** DAM
catalog.

Wizard flow:

1. **Datasource** — local folder (recursive or not) or a connected Daminion
   server with a scope (whole catalog / keyword search / shared collection /
   saved search), filters (status flag, "only untagged fields"), and a fetch
   limit (plus an optional *Process all*).
2. **Engine** — model id (from the local HF cache or by hand), device
   (CPU/CUDA/MPS), probability mode + candidate labels, a custom VLM system
   prompt, and which of the three returned fields to write.
3. **Process** — runs the batch: fetch → infer → write, with progress, ETA,
   pause/resume/abort, and a live log.
4. **Results** — grid of per-item outcomes, CSV export, retry of failed items,
   and verification of Daminion writes.
5. **Deduplication** — separate tool: perceptual-hash duplicate scan with
   Tag/Move/Delete actions.

Cloud engines (OpenRouter/Groq) were removed from scope: the local sidecar is
the only engine and there are no API keys to store.

## 2. Repository layout

```
Synapic.Net.sln              solution (3 src + 3 test projects)
Directory.Build.props        net10.0, C# 12, nullable, warnings-as-errors
global.json                  .NET SDK pin (10.0.112, rollForward latestFeature)
nuget.config
README.md                    the original migration specification
plan.md                      phased implementation plan + build-mode PR sequence

src/
  Synapic.Avalonia/          the desktop app (WinExe, assembly name "Synapic")
    Program.cs               STAThread entry point
    App.axaml(.cs)           startup: logging, crash handler, DI, sidecar lifecycle
    Models/Session.cs        wizard state (datasource, engine, results, counters)
    Services/                all non-UI logic (see csharp-reference.md)
    ViewModels/              shell + wizard + per-step view models
    Views/                   MainWindow + wizard .axaml (+ minimal code-behind)
  Synapic.Shared/            HTTP contract DTOs + System.Text.Json source-gen context
  Synapic.Inference/         Python sidecar (NOT a .NET project; not in the .sln)
tests/
  Synapic.Avalonia.Tests/    xUnit + Avalonia.Headless (the bulk of the suite)
  Synapic.Shared.Tests/      contract round-trip tests
  Synapic.Inference.Tests/   pytest (sidecar)
  Synapic.Integration.Tests/ placeholder for environment-dependent E2E
build/                       fetch-python / install-deps / build-sidecar / variant + tie
                             guards / protocol-doc generator / release-asset splitter
                             / packaging
docs/                        this documentation set
artifacts/<rid>/             built sidecar + published app (dev output)
```

The sidecar lives under `src/` for convenience but is built by
`build/fetch-python.*`, `build/install-python-deps.*`, `build/build-sidecar.*`
and packaged by PyInstaller; it is never compiled by MSBuild. Two guards run
inside the build: `check-lfm2vl-tie.py` (transformers must tie the LFM2.5-VL
weights) and `check-sidecar-variant.py` (the packaged payload must match the
RID's CPU/CUDA torch wheels). `split-release-asset.py` is a packaging-time tool
rather than a build guard: it cuts a release asset that is over GitHub's 2 GiB
per-file cap (today only the ~2.5 GB CUDA sidecar) into parts and emits a
reassembly helper alongside them.

### Tech stack (pinned centrally)

`Directory.Build.props` defines the shared package versions: Avalonia 11.1.3
(+ DataGrid, Fonts.Inter, Diagnostics in Debug), CommunityToolkit.Mvvm 8.3.2,
Refit 7.2.22, MetadataExtractor 2.9.3, NetVips 3.2.0, Serilog 4.0.2 +
Serilog.Sinks.File, xUnit 2.9.2, Microsoft.Extensions.DependencyInjection
10.0.12, `System.Security.Cryptography.ProtectedData` (DPAPI).
`TreatWarningsAsErrors` is on — a new warning fails the build.

Python deps are pinned in `src/Synapic.Inference/requirements.txt`
(torch 2.9.1 CPU by default, transformers 5.1.0, fastapi/uvicorn/pydantic,
huggingface_hub, pillow, tqdm, psutil, qwen-vl-utils, PyInstaller 6.18.0).

## 3. Two-process architecture

```
┌─────────────────────────────── Synapic.exe (C#) ───────────────────────────────┐
│  MainWindow ─ WizardViewModel ─ Step1..4 + Dedup                               │
│  InferenceSidecarService  ──spawn/stop/health──►  synapic-inference --port=0   │
│  ProcessingOrchestrator   ──POST /tag──────────►  FastAPI (127.0.0.1:<port>)   │
│  DaminionApiClient / MetadataWriterService / DedupService (all in C#)          │
└────────────────────────────────────────────────────────────────────────────────┘
                                        │
                          port file: %TEMP%/synapic_port_{pid}.txt  ("port\npid\n")
```

- The full wire contract is [`sidecar-protocol.md`](sidecar-protocol.md).
- The sidecar binds port 0 (OS-assigned), pre-binds the socket so it can write
  the port file **before** uvicorn serves, and the host polls `/health` until
  `ready` (max 120 s).
- `HF_HOME` is set by the host at launch to the Synapic-managed model cache, so
  the sidecar's downloads land in the app's cache and not a stray `~/.cache`.
- On Windows the sidecar is assigned to a **kill-on-close job object**, so it
  can never outlive the app even on a crash. On every platform a graceful
  `POST /shutdown` is tried first, then a 5 s grace, then
  `Process.Kill(entireProcessTree: true)`.

## 4. Startup sequence (`App.OnFrameworkInitializationCompleted`)

1. Load `config.json` (see §7) — enough of it to configure logging.
2. `SynapicLog.Initialize` — one Serilog logger to `logs/synapic.log`
   (**starts empty each run; the previous run is rotated to
   `logs/archives/`, kept for 10 runs**) plus an in-memory `UiLogSink` the UI binds to.
3. Install `CrashReporterService` (global handlers → `logs/crashes/`), with a
   dialog for non-terminal crashes.
4. Fire-and-forget `DotNetRuntimeCheckService.EnsureRuntimeAsync` (Windows:
   silently installs the .NET 10 Desktop Runtime if missing).
5. Initialise `TelemetryService.Shared` (opt-in, local counters only).
6. Build the DI container (below) and construct `MainWindow` +
   `MainWindowViewModel`.
7. `DetectServerAsync()` — locate the sidecar executable, populate the CPU/CUDA
   setup panel, and set the status indicator.
8. Auto-launch the sidecar (the shipped default; skipped when
   `ui.autoLaunchSidecar` is false)
   (**default is true** — the server starts with the app).
9. Hook `ShutdownRequested` to cancel any build and stop the sidecar before the
   process exits.

### Dependency injection

`Microsoft.Extensions.DependencyInjection`, registered as singletons in
`App.axaml.cs`:

| Registration | Concrete type |
|--------------|---------------|
| `AppConfig` | the loaded config object |
| `Session` | wizard state shared by every view model |
| `DaminionConnectionStore` | registry-backed Step 1 persistence |
| `EngineSettingsStore` | registry-backed Step 2 persistence |
| `IInferenceSidecar` | `InferenceSidecarService` |
| `ISidecarBuildService` | `SidecarBuildService` |
| `ISidecarDownloadService` | `SidecarDownloadService` |
| `MainWindowViewModel` | shell view model (constructs `WizardViewModel`) |
| `MainWindow` | the window itself |

`App.Services` is the global provider; a few places resolve from it directly
because they are constructed outside DI (e.g. `WizardViewModel.PersistConfig`
and the Avalonia designer path).

## 5. Data flow for a batch

```
Step 1 view model ──► Session.Datasource ──► DatasourceSelection ──┐
Step 2 view model ──► Session.Engine     ──► TagRequest template   │
                                                                    ▼
                               ProcessingOrchestrator.RunAsync(ds, request, progress, log, ct,
                                                               Session.Results, pause, tagFields)
                                                                    │
       ┌──────────────────────── per work item (SemaphoreSlim ≤ 4) ─┘
       │ 1. acquire image   local path | Daminion download
       │                    (thumbnail override, resize scale, or original)
       │ 2. POST /tag       → TagResponse {category, keywords, description,
       │                                    probabilities, scoring, inference_ms}
       │ 3. write metadata  local XMP/IPTC (MetadataWriterService)
       │                    | Daminion ItemData/BatchChange
       │ 4. append ProcessItemResult to Session.Results
       ▼
Step 4 grid ─ CSV export / retry failed / verify Daminion
```

Progress is an `IProgress<ProcessProgress>` carrying processed/failed/total,
percent, ETA, per-item duration and the current file name. ETA is
`elapsed / processed × remaining`, available from the first completed item
(matching the original Python app). Step 3 renders it plus a per-image
duration, and the log auto-scrolls to the newest line.

## 6. Concurrency model (important)

- **Per-item work** (obtain image → `/tag` → write metadata) is bounded by a
  `SemaphoreSlim` in `ProcessingOrchestrator` — Step 3 hard-codes
  `maxDegreeOfParallelism: 4` (Step 4's retry uses the constructor default,
  also 4). Fetching itself is a single sequential paging loop that completes
  before processing begins.
- **The sidecar** is a single uvicorn process (no `workers=`). `/tag` is a
  **sync** FastAPI endpoint, so Starlette runs each request on its AnyIO
  worker threadpool (default 40 threads). There is no server-side queue or
  limiter: as many concurrent `/tag` calls as arrive will run against the same
  cached pipeline.
- **Model construction is serialized** by `model_loader._model_build_lock`, and
  only one pipeline is kept resident (`_model_cache` is cleared on each load).
  This was the fix for mixed float32/bfloat16 weights under a cold 4-way
  parallel batch.
- **This differs from the original Python app**, which pinned local inference
  to a single worker (`max_workers = 1` in `processing.py`) because one model
  instance thrashes under concurrent calls. 4-way parallelism is a deliberate
  port decision; it helps on CPU, but on a GPU it can serialize at the CUDA
  level and multiply VRAM. Consider 1–2 for GPU devices.
- **Pause/abort**: `PauseToken` lets running items finish while queued items
  wait; `CancellationTokenSource` (Abort) always wins over pause. Navigation is
  locked while a batch runs (`WizardViewModel.IsNavigationLocked`).

## 7. Persistence & paths

| What | Where |
|------|-------|
| App config (wizard + engine defaults, UI prefs) | Windows `%APPDATA%\Synapic\config.json`; otherwise `$XDG_CONFIG_HOME/Synapic/config.json` or `~/.config/Synapic/config.json` |
| Step 1 connection + scope/filter/limit settings | Windows registry `HKCU\Software\Synapic\Daminion` (password DPAPI-protected); no-op elsewhere |
| Step 2 engine settings | Windows registry `HKCU\Software\Synapic\Engine`; no-op elsewhere |
| Log file (live file starts empty each run; previous runs in `logs/archives/`, last 10) | `logs/synapic.log` next to the executable, falling back to `%LOCALAPPDATA%\Synapic\logs` |
| Crash reports | `logs/crashes/` (exception + session-log snapshot) |
| Opt-in usage counters | `logs/synapic-usage.json` |
| Model cache (sidecar `HF_HOME`) | Windows `%LOCALAPPDATA%\Synapic\models`; elsewhere `~/.cache/synapic/models` |
| Sidecar port file | `%TEMP%/synapic_port_{hostpid}.txt` containing `port\npid\n` |

Notes:
- `config.json` is written by `MainWindowViewModel.PersistConfig` when the user
  reaches Results, on Start Over, and at other lifecycle points. Only a
  successful Daminion login rewrites the registry connection block, so a typo
  can never clobber a working set.
- `EngineSettingsStore`/`DaminionConnectionStore` are Windows-only by design;
  the app remains functional (with defaults) on Linux/macOS.
- Config has a `Version` (currently 2). Unknown JSON fields are preserved on
  round-trip by `ConfigService`.

## 8. Engine settings → prompt behaviour (practical notes)

- For the VLM (`image-text-to-text`), the output is steered by three things:
  the **system prompt**, the **tag instruction** (both editable in Step 2), and
  the shape the extractor can read. The model's `category` is what lands in the
  file's category/headline field (Title-Cased).
- The **tag instruction** is `/tag`'s `user_prompt`. Step 2 keeps it in the
  `UserPrompt` field (session + registry + config file) and sends it only when
  non-blank: **blank means "use the sidecar's built-in instruction"**, so
  clearing the box is always a safe way back to a prompt that parses. The host
  keeps no copy of the built-in text - "Use built-in instruction" loads it from
  `GET /prompt` (the same value `/tag` falls back to, so the two cannot drift),
  and "Reset to built-in" clears the box back to that default.
- Because a hand-edited instruction can quietly cost you a field, the sidecar
  logs a warning when a **custom** instruction comes back with an empty
  description/category/keywords ("check that it asks for those keys"); measured,
  asking for the three keys without specifying that keywords are an array of
  5-10 tags returns valid JSON with no keywords at all. Warnings are visible in
  the Step 3 log panel.
- The built-in tag instruction is deliberately explicit rather than a one-liner: it
  bans code fences and text outside the object, demands double quotes (the
  model's own preference is Python-style single quotes), asks for a single-line
  description (a literal newline inside a string is not valid JSON), and shows
  the object shape. Measured on the default model, the one-sentence version it
  replaced produced **zero** strictly valid JSON replies out of 13 — all fenced,
  three single-quoted — so `tag_extractor` rescued every one and any rescue
  failure wrote the raw payload into Description. The wording now shipped is
  13/13. Treat it as load-bearing; `test_tag_prompt.py` guards it.
- **System-prompt presets are history, not settings.** Every system prompt the
  user commits (leaving the box, or moving off Step 2) is added to
  `%APPDATA%/Synapic/system-prompts.json` by `SystemPromptPresetStore`
  (`{"version":1,"prompts":[…]}`, newest first, capped at
  `MaxPresets`), and Step 2's combobox offers them back; Delete on the
  highlighted entry removes it. The *current* prompt still lives in
  `Session`/registry like every other Step 2 field, so pruning the list can never
  lose the run's configuration. Deleting the prompt that is in the box also
  clears the box, because otherwise the next commit would put it straight back.
- A **system prompt must not restate the format.** It is prepended, so asking
  for a different shape (bare prose, a different key set, a markdown report)
  fights the instruction above and is the one way to make the parser's job hard
  again. Ask for tone, vocabulary, or locale instead.
- The system turn is built by `inference_engine.build_vlm_messages` as content
  **parts**, not a bare string. transformers 5.1's image-text-to-text
  `preprocess` reads `["type"]` off every content part of every message, so a
  string system content raised `TypeError: string indices must be integers`
  before the model was called — a custom system prompt failed *every* image of a
  run with HTTP 500. Measured with and without a system prompt: 13/13 valid JSON
  replies either way (`build/check-tag-prompt.py`).
- **Probability mode / candidate labels do not constrain a VLM.** The scoring
  ladder only runs for `image-classification` pipelines (tier 2, label
  confidence) or the CLIP embedding rescue (tier 2.5). For a VLM,
  `pick_scoring_tier` returns `None`, the scoring result degrades to
  `unavailable`, and `both`/`probability` fall through to plain LLM tagging.
- `Confidence threshold` applies to classification/zero-shot extraction, not to
  the VLM's category.
- **Which fields get written** is chosen by the Step 2 "Tag fields"
  checkboxes (`TagFieldSelection`, persisted to the registry and re-hydrated on
  startup); the model always returns all three. The write step drops the
  unticked fields silently, so a partial selection is announced twice: in Step 2
  while it can still be changed, and in the batch's first log line (`writing
  keywords only`) - the missing fields otherwise look like a broken model.

## 9. Metadata writing (what lands where)

`MetadataWriterService` writes a minimal, lossless **XMP packet**:

| Tag result field | XMP property |
|------------------|--------------|
| `Category` | `photoshop:Headline` (and `Iptc4xmpCore:IntellectualGenre`) |
| `Keywords` | `dc:subject` (rdf:Bag) |
| `Description` | `dc:description` (rdf:Alt, `x-default`) |

- **JPEG** — the XMP is inserted/replaced as an APP1 segment via byte surgery
  (no re-encode).
- **PNG** — written as an `iTXt` chunk (`XML:com.adobe.xmp`) after IHDR.
- **TIFF** — an `.xmp` sidecar file is written next to the image (Daminion and
  Windows Photos both honour it).
- **Daminion** items are written back through `ItemData/BatchChange`.

`ReadAsync` reads the same properties back (XMP plus IPTC/EXIF fallbacks) and is
used by the round-trip tests and Daminion verification.

## 10. Build, run, test

```bash
# Build everything
dotnet build Synapic.Net.sln -c Debug

# Regenerate the wire-protocol doc, or verify it in CI
python build/generate-protocol-doc.py
python build/generate-protocol-doc.py --check

# Run the app (needs a built sidecar for actual tagging)
dotnet run --project src/Synapic.Avalonia

# Tests
dotnet test Synapic.Net.sln -c Release
python -m pytest tests/Synapic.Inference.Tests -q

# Build the sidecar (CPU / CUDA variants on Windows)
build/build-server.ps1 -Rid win-x64
build/build-server.ps1 -Rid win-x64-cuda
build/build-server.sh linux-x64
```

CI (`.github/workflows/build.yml`) runs pytest + xUnit first, then a 3-RID
matrix (win-x64, linux-x64, osx-arm64) that builds the sidecar, publishes the
app, stages them together, publishes the Windows installer, and (on `main`
pushes) runs an installer smoke test that also exercises the .NET runtime
prerequisite path. `release.yml` handles `v*` tags (and manual dry runs) with
signing/notarization, and publishes the standalone CPU **and** CUDA sidecar
executables as their own release assets — see `packaging.md`.

## 11. Troubleshooting index (log messages you will actually see)

| Symptom / log line | Where to look |
|--------------------|---------------|
| `Server not detected` / `No inference server built` | `ISidecarBuildService.CanBuild`, `artifacts/<rid>/`; use the setup panel's Build button, or **Download** to fetch the prebuilt exe from the latest release (`ISidecarDownloadService`). |
| A built variant has no action left (`Built`, no Build/Download button) | **Update** replaces it: `MainWindowViewModel.UpdateVariantAsync` rebuilds from source when `ISidecarBuildService.CanBuild`, otherwise re-downloads the latest release. It stops a running server first, then starts it again on the new exe. |
| `Sidecar executable not found - build it first` | `InferenceSidecarService.FindExecutable` — bundled exe or dev `artifacts/`. |
| `Timed out waiting for the sidecar port file` | Sidecar failed to boot; check sidecar stdout in the log. |
| `Sidecar did not become ready within 120s` | Model load/first import is slow; check `/health` status lines. |
| `Sidecar process exited unexpectedly` | Liveness watcher; crash before `/shutdown`. |
| `Model loading — retry shortly` (503) | Only after a load that outlasted the server's 240 s wait; a load already in flight is waited out instead, and the model is warmed up at boot (`service._warm_up_model`). The host still retries once after 3 s. |
| `Stale server build: exe built … but the sidecar source changed …` | `InferenceSidecarService.DescribeStaleness` — the running exe predates `src/Synapic.Inference`; press **Update** on that variant's row. The status bar shows "stale build, rebuild recommended" too, and the row itself shows "Update available — …" (which also keeps the setup panel visible). |
| `Both max_new_tokens (...) and max_length (...) seem to have been set` | Fixed in `inference_engine._generation_kwargs` (clones the pipeline generation config, pins `max_new_tokens`, clears `max_length`). |
| `Progress scoring unavailable: ...` | Expected when probability/candidate labels are used with a VLM (see §8). |
| Only keywords reach Daminion (no category/description) | Not the model: the write step keeps only the fields ticked in Step 2 ("Tag fields"), which persist in `HKCU\Software\Synapic\Engine` as `TagKeywords`/`TagCategories`/`TagDescription`. The batch's first log line names them (`writing keywords only`); a `/tag` call always returns all three. |
| `Daminion returned the same ids as the previous page` | Infinite-loop guard in `FetchItemsAsync` (offset ignored server-side). |
| `Count N matches total catalog size despite filters` | `GetFilteredItemCountAsync` sanity fallback. |
| `[sidecar:err] ...` lines in the UI log | Sidecar stderr, forwarded verbatim through `LogReceived`. |

## 12. Known divergences / decisions worth remembering

- **Auto-launch by default** — `UiSettings.AutoLaunchSidecar` ships `true`, so
  the server starts with the app; `ui.autoLaunchSidecar=false` opts out to the
  manual Start/Stop flow. The spec/README §2 originally described a manual
  default and was reconciled to the shipped behaviour (this setting is
  config-file-only; there is no Options panel).
- **4-way parallel inference** vs the original single-worker local loop (§6).
- **No cloud engines / no secret store** — removed from scope; the config has
  no API-key fields.
- **Deduplication, metadata writing, and DaemonThreadPoolExecutor** were
  deliberately re-implemented in C# rather than ported to the sidecar.
- **Deferred post-parity**: upscaler and vector embedder / semantic search.
- `Category` is stored as an XMP *Headline/Genre*, not as an EXIF/IPTC
  `Object Name`; Daminion continues to use its own `Categories` tag via the
  API.
