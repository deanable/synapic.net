# Synapic.NET — Systematic Implementation Plan

**Implements:** `README.md` — *Synapic .NET Migration Specification v1.0* (Avalonia UI + Python/PyInstaller sidecar)
**Porting source:** `C:\Users\deank\repos\Synapic` (Python/CustomTkinter)
**Target:** this repository → full `Synapic.Net` solution
**Execution rule:** each phase ends at a verification gate. Python inference logic is ported with **minimal changes** — only I/O boundaries (file/queue → HTTP) are adapted, per spec §4.3.
**Scope update (user direction):** cloud engines (OpenRouter/Groq) are **removed from scope** — the local LFM-based Python sidecar is the only engine; `CloudInferenceClient` and `ISecretStore` were deleted.

---

## 1. Key Decisions (locking spec §11 open questions)

| # | Question | Locked decision |
|---|----------|-----------------|
| Q1 | Bake model weights? | Bake LFM2.5-VL-1.6B (~2 GB) into "full" installers; also publish "lite" installers that download on first run via `/models/download` |
| Q2 | Daminion thumbnails | Stay in Avalonia (C#); image bytes never traverse the IPC |
| Q3 | Probability scoring | Stays in Python sidecar (transformers pipeline + CLIP scorer) |
| Q4 | API key storage | *N/A — cloud engines removed from scope; no API keys to store* |
| Q5 | Telemetry | Phase 6; opt-in `ui.telemetryEnabled`, default false |

**Stack pins:** .NET 10.0 LTS / C# 12 · Avalonia 11.1+ · CommunityToolkit.Mvvm 8.2+ · Refit 7+ · System.Text.Json source-gen · MetadataExtractor 2.8+ · NetVips 2.2+ · Serilog 3.1+ · Python 3.11 via python-build-standalone · PyInstaller 6.18+ · torch 2.9 CPU default + CUDA 12.x variants, runtime device detect with CPU fallback.

**Spec normalizations (documented, not silent):**
- CI workflows at standard `.github/workflows/` (spec §3 tree vs §7.1 disagree; §7.1 wins)
- One packaging script per platform (`package-windows.ps1`, `package-linux.sh`, `package-macos.sh`)
- `fetch-python` uses python-build-standalone on all RIDs (embeddable zip can't reliably pip-install)
- Sidecar `requirements.txt` trimmed: keep torch/vision, transformers≥5, hf hub, hf_xet, accelerate, sentencepiece, qwen-vl-utils, protobuf, pillow, tqdm, psutil + fastapi/uvicorn/pydantic/httpx; drop customtkinter, piexif, iptcinfo3, imagehash, faiss-cpu, sentence-transformers, requests
- PyInstaller hiddenimports updated to match (no cv2/imagehash in sidecar)
- **Out of scope, deferred post-parity:** upscaler, vector embedder & semantic search

---

## 2. Architecture Summary (per spec §2)

- Avalonia app ⇄ HTTP/JSON `127.0.0.1:<OS-assigned port>` ⇄ FastAPI sidecar `synapic-inference(.exe)`
- **Sidecar lifecycle (auto-launch default):** the sidecar starts with the app; set `ui.autoLaunchSidecar=false` to opt out and use the Start Server button → `--port=0` → port file `%TEMP%/synapic_port_{pid}.txt` (`port\npid\n`) → poll `/health` to `ready` (max 120 s) → `/tag` 5-min timeout, one retry on 503 → exit: `POST /shutdown` → 5 s grace → `Process.Kill(entireProcessTree: true)` — never an orphan
- **Status machine:** Stopped → Starting → Ready | Error + `StatusChanged`; PID liveness + failed /health polls → "Server stopped unexpectedly — restart?" prompt
- **Engine routing:** local HF → sidecar only (OpenRouter/Groq removed from scope)
- **Processing loop lives in C#** (`ProcessingOrchestrator`): per-item `/tag` via SemaphoreSlim; pause/abort/retry per item

---

## 3. Phase 0 — Foundation (Week 1)

| Task | Content |
|------|---------|
| P0.1 | .sln, Directory.Build.props, global.json, nuget.config, .gitignore |
| P0.2 | Projects: Synapic.Avalonia, Synapic.Shared, tests (xUnit+Headless, pytest, integration) |
| P0.3 | Sidecar source: service.py, model_loader.py ← huggingface_utils.py, inference_engine.py, tag_extractor.py ← extract_tags_from_result + keyword scoring; trimmed pinned requirements; synapic-inference.spec |
| P0.4 | Build scripts: fetch-python, install-python-deps, build-sidecar (ps1 + sh) |
| P0.5 | Avalonia shell: Start/Stop Server + status indicator, log pane; InferenceSidecarService full lifecycle; config `ui.autoLaunchSidecar` (default **true**; opt out for manual Start/Stop) |
| P0.6 | CI build.yml: 4-RID matrix |
| P0.7 | ConfigService v1; LoggingService (Serilog file + UI sink) |

**Gate:** CI produces 4 bundles; Start Server → /health ready ≤120 s; no orphan on exit.

## 4. Phase 1 — Core Inference Loop (Weeks 2–3)

- P1.1 Contract freeze: docs/sidecar-protocol.md = spec §4.2; CI diffs served /openapi.json against it
- P1.2 Synapic.Shared.Contracts: DTOs + source-gen JsonSerializerContext (camelCase)
- P1.3 InferenceApiClient: /tag (5-min, 503 retry), /models/*, /config
- P1.4 Sidecar behaviors: session model state, inference_ms, model_used, vram_used_mb, 404/503 mapping
- P1.5 Probability scoring port incl. tier-annotated `scoring`
- P1.6 Step 2 Engine UI: model picker, device, thresholds, probability mode (local sidecar only — cloud tabs removed)
- P1.7 E2E: folder → pick model → tag one image → render tags

**Gate:** tag one image via UI on CUDA and CPU; contract test green.

## 5. Phase 2 — Datasource & Daminion (Weeks 3–4)

- P2.1 DaminionApiClient (Refit): auth (user-pass + token), GetItemsFiltered, UpdateMetadata, thumbnail/original; typed errors
- P2.2 Step 1 UI: local browser + recursive; Daminion wizard (URL/auth/catalog/scope/filters/Test Connection)
- P2.3 ProcessingOrchestrator: _fetch_items port — local recursive + Daminion 500-page pagination, maxItems, resizeScale, thumbnail override
- P2.4 MetadataWriterService: MetadataExtractor read; JPEG via NetVips fields; PNG/TIFF tEXt/iTXt; Daminion via UpdateMetadata
- P2.5 Batch loop: IProgress<ProcessProgress> (count, ETA), per-item retry, abort
- P2.6 Daminion round-trip integration test

**Gate:** full local-folder and Daminion pipelines e2e.

## 6. Phase 3 — Wizard UX & Session State (Weeks 4–5)

- P3.1 Session model + ConfigService v2 (spec §6.1 schema) with v1→v2 migration
- P3.2 *dropped — ISecretStore removed along with the cloud engines; no API keys to store*
- P3.3 Step 3 UI: progress (items, ETA), colorized log (Serilog sink + sidecar LogReceived), pause/abort/retry, memory/VRAM monitor
- P3.4 Step 4 UI: results grid, CSV export, verify-Daminion
- P3.5 WizardViewModel: navigation, validation gates, persistence
- P3.6 HF_HOME: %LOCALAPPDATA%/Synapic/models | ~/.cache/synapic/models set at sidecar launch

**Gate:** wizard matches current CustomTkinter flow.

## 7. Phase 4 — Deduplication (Weeks 5–6)

- P4.1 DedupService: NetVips-backed pHash/dHash/aHash/color-moment (custom C# fallback), threshold 0.90, MaxDimension 512
- P4.2 Grouping + strategies port: UnionFind, keep-by-size/date/quality, persistent hash cache
- P4.3 StepDedup UI: side-by-side compare, auto-select keep-set, bulk Tag/Move/Delete
- P4.4 Daminion integration: tag duplicates / remove from catalog
- P4.5 10k+ image perf benchmark

**Gate:** parity dedup wizard at spec perf targets (<2 s/image GPU, <500 MB RSS).

## 8. Phase 5 — Polish & Distribution (Weeks 6–7)

- P5.1 Dark/light/system themes, high-DPI, accessibility
- P5.2 First-run model download UX; full (baked) + lite installers
- P5.3 Auto-update check via GitHub Releases API
- P5.4 Installers: Inno Setup 6 (EV signing) · AppImage (linuxdeploy) · create-dmg + notarytool (x64/arm64)
- P5.5 release.yml: tag → build → smoke → sign → notarize → upload, including the standalone CPU and CUDA inference-server executables (the CUDA bundle is split under GitHub's 2 GiB per-asset cap); a manual dispatch dry-run builds the same assets without publishing
- P5.6 Cross-platform smoke matrix

**Gate:** signed installers ×4 RIDs; standalone CPU + CUDA server downloads on the release page that boot and serve `/health`; clean-VM install → tag image → no orphan uninstall.

## 9. Phase 6 — Beta & Hardening (Weeks 7–8+)

- P6.1 Crash reporting (Sentry or custom), opt-in telemetry
- P6.2 Profiling (dotnet-trace, py-spy); 10k+ leak soak
- P6.3 Docs: architecture.md, packaging.md, migration-checklist.md
- P6.4 Beta cohort (5–10 users) + feedback loop

---

## 10. Testing Matrix (spec §10 → concrete)

| Layer | Tool | From phase |
|-------|------|-----------|
| C# unit (services 80% / VMs 90%) | xUnit | P0 |
| UI | Avalonia.Headless | P0 |
| Python (85% inference modules) | pytest | P0 |
| Contract | served /openapi.json diffed vs frozen spec in CI | P1 |
| Integration | Testcontainers Daminion + local fixtures | P2 |
| E2E | Playwright + headless sidecar | P3 |
| Perf | BenchmarkDotNet + py-spy | P4 |

## 11. Risk Mitigations (spec §9, phase-linked)

Bundle size → trimmed deps + UPX + lite installer (P0/P5) · Cold start → lazy load + status UI (P0/P3) · CUDA/ROCm/Metal variance → CPU fallback + wheel variants (P0/P1) · libvips deploy → custom C# hash fallback (P4) · Daminion drift → version-detect + integration tests (P2) · macOS notarization → sign all .so/.dylib in site-packages (P5) · Port conflicts → random port + PID file, reuse sidecar (P0) · AV false positives → EV signing, drop UPX if flagged (P5)

## 12. Definition of Done

- **MVP (end P2):** Sidecar auto-launch (manual opt-out) → Step 1 local+Daminion → Step 2 local+cloud → Step 3 progress/abort → Step 4 results → metadata written to files and Daminion
- **Feature parity (end P5):** + dedup wizard, themes, signed installers ×4, auto-update, baked models
- **Hardened (end P6):** opt-in telemetry + crash reporting, soak-tested, docs complete

## 13. Build-Mode PR Sequence

1. **PR1:** P0.1+P0.2 scaffold (solution builds clean)
2. **PR2:** P0.3 sidecar source port + pytest suite
3. **PR3:** P0.4 build scripts + P0.6 CI matrix
4. **PR4:** P0.5+P0.7 app shell, config, Start Server e2e → **Phase 0 gate**
5. **PR5:** P1.1–P1.5 contract, client, sidecar behaviors → **Phase 1 gate**
6. Then phase-by-phase; each gate = merge checkpoint with green CI + tests
