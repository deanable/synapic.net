# \# Synapic .NET Migration Specification

# \## Avalonia UI + Python Sidecar (PyInstaller) Architecture





This is a port from https://github.com/deanable/Synapic Located at C:\\Users\\deank\\repos\\Synapic

# 

# \*\*Version:\*\* 1.0  

# \*\*Target:\*\* Cross-platform (Windows, Linux, macOS)  

# \*\*Timeline:\*\* 6–8 weeks for MVP, 10–12 weeks for feature parity  

# 

# \---

# 

# \## 1. Executive Summary

# 

# Migrate Synapic from Python/CustomTkinter to \*\*C#/Avalonia\*\* for the UI and orchestration layer, while preserving the existing \*\*Python inference engine\*\* as a bundled PyInstaller executable (`synapic-inference.exe`). This eliminates user-facing Python dependency hell while retaining 100% model fidelity (LFM2.5-VL, BLIP, CLIP, etc.).

# 

# \### Key Decisions

# | Decision | Rationale |

# |----------|-----------|

# | \*\*Avalonia 11+\*\* | Native cross-platform, XAML, MVVM, mature ecosystem |

# | \*\*Python sidecar via PyInstaller\*\* | Zero user Python install; preserves all HF/transformers logic unchanged |

# | \*\*HTTP/JSON IPC (localhost)\*\* | Simple, debuggable, language-agnostic; supports future gRPC upgrade |

# | \*\*Single installer per platform\*\* | Inno Setup (Windows), AppImage (Linux), DMG (macOS) |

# | \*\*Model weights baked at build time\*\* | No first-run downloads; works offline |

# 

# \---

# 

# \## 2. High-Level Architecture

# 

# ```

# ┌─────────────────────────────────────────────────────────────────────────────┐

# │                          SYNAPIC APPLICATION BUNDLE                         │

# ├─────────────────────────────────────────────────────────────────────────────┤

# │                                                                             │

# │  ┌─────────────────────────────┐     HTTP/JSON (127.0.0.1:5001)     ┌─────┐│

# │  │   AVALONIA FRONTEND (C#)    │ ◄─────────────────────────────────► │     ││

# │  │   ┌─────────────────────┐   │                                     │     ││

# │  │   │  Wizard Controller  │   │   /health        →  {"status":"ok"} │     ││

# │  │   │  (Step 1–4 + Dedup) │   │   /tag           →  {cat, kws, desc}│     ││

# │  │   └─────────────────────┘   │   /models/list   →  \[{id, task...}] │     ││

# │  │   ┌─────────────────────┐   │   /models/download                │     ││

# │  │   │  Session State      │   │   /config/get/set                 │     ││

# │  │   │  (Config, Progress) │   │                                     │     ││

# │  │   └─────────────────────┘   │                                     │     ││

# │  │   ┌─────────────────────┐   │                                     │  ▼  ││

# │  │   │  Daminion Client    │   │   ┌─────────────────────────────┐  │     ││

# │  │   │  (HttpClient/Refit) │   │   │  PYTHON SIDECAR             │  │     ││

# │  │   └─────────────────────┘   │   │  (synapic-inference.exe)    │  │     ││

# │  │   ┌─────────────────────┐   │   │   ┌───────────────────────┐ │  │     ││

# │  │   │  Metadata Writer    │   │   │   │ FastAPI + Uvicorn     │ │  │     ││

# │  │   │  (MetadataExtractor,│   │   │   │   /health             │ │  │     ││

# │  │   │   ExifLib, custom)  │   │   │   │   /tag                │ │  │     ││

# │  │   └─────────────────────┘   │   │   │   /models/\*           │ │  │     ││

# │  └─────────────────────────────┘   │   │   │ ModelLoader         │ │  │     ││

# │                                    │   │   │   - HF Hub download │ │  │     ││

# │                                    │   │   │   - transformers    │ │  │     ││

# │                                    │   │   │   - torch/cuda/mps  │ │  │     ││

# │                                    │   │   │   - tokenizer       │ │  │     ││

# │                                    │   │   └───────────────────────┘ │  │     ││

# │                                    │   └─────────────────────────────┘  │     ││

# │                                    └─────────────────────────────────────┘     │

# │                                                                             │

# └─────────────────────────────────────────────────────────────────────────────┘

# ```

# 

# \### Process Lifecycle

# > **Design note (reconciled with the shipped implementation):** The sidecar **starts automatically with the app** and is stopped unconditionally on exit. The lifecycle is strictly one server per app instance — a server left over from a previous run is never adopted. Users who want a manual-only workflow set `ui.autoLaunchSidecar` to `false` in `config.json` and use the toolbar **Start Server** button. There is no Options panel in the shipped UI yet, so the setting is config-file-only.

# #### Automatic Launch (default)
# 1\. \*\*App start\*\* → Avalonia launches the sidecar with `--port=0` (OS assigns a free port) → the sidecar writes the port to `%TEMP%/synapic\_port\_{pid}.txt`.
# 2\. \*\*Avalonia reads the port\*\*, polls `/health` until `{"status":"ready"}` (max 120s).
# 3\. \*\*All inference requests\*\* route through `HttpClient` with a 5-min timeout.
# 4\. \*\*On Avalonia shutdown\*\* → `POST /shutdown` → sidecar exits gracefully; fallback `Process.Kill()` after 5s.

# #### Manual Launch (opt-out)
# - Set `ui.autoLaunchSidecar` to `false` in `config.json`.
# - The app then starts with the server stopped; press **Start Server** on the toolbar to run the exact sequence above on demand.
# - The setting persists in `config.json` (`ui.autoLaunchSidecar`, default `true`).
# - Either way the sidecar is always stopped when the app exits (see the shutdown flow below) — it never lingers as an orphan.

# #### Server Status
# - The UI shows a **server status indicator** (stopped / starting / ready / error).
# - A **Stop Server** button is available while the server is running, so the user can shut it down early without closing the app.
# - If the sidecar process dies unexpectedly, Avalonia detects it (PID liveness + failed `/health` polls) and shows the user a "Server stopped unexpectedly — restart?" prompt.

# 

# \---

# 

# \## 3. Repository Structure

# 

# ```

# Synapic.Net/

# ├── build/                          # Build \& packaging scripts

# │   ├── fetch-python.ps1            # Downloads python-embeddable / python-build-standalone

# │   ├── install-python-deps.ps1     # pip installs into bundled site-packages

# │   ├── build-sidecar.ps1           # Runs PyInstaller → synapic-inference.exe

# │   ├── package-windows.ps1         # Inno Setup compiler invocation

# │   ├── package-linux.sh            # AppImage creation

# │   ├── package-macos.sh            # create-dmg + notarization

# │   └── github-actions/             # CI/CD workflows

# │       ├── build.yml               # Matrix build for 4 RIDs

# │       └── release.yml             # Tag → build → sign → upload assets

# │

# ├── src/

# │   ├── Synapic.Avalonia/           # Main Avalonia application

# │   │   ├── Synapic.Avalonia.csproj

# │   │   ├── Program.cs

# │   │   ├── App.axaml / App.axaml.cs

# │   │   ├── ViewModels/

# │   │   │   ├── MainWindowViewModel.cs

# │   │   │   ├── WizardViewModel.cs

# │   │   │   ├── Step1\_DatasourceViewModel.cs

# │   │   │   ├── Step2\_EngineViewModel.cs

# │   │   │   ├── Step3\_ProcessViewModel.cs

# │   │   │   ├── Step4\_ResultsViewModel.cs

# │   │   │   └── StepDedupViewModel.cs

# │   │   ├── Views/

# │   │   │   ├── MainWindow.axaml

# │   │   │   ├── Wizard/

# │   │   │   │   ├── Step1\_Datasource.axaml

# │   │   │   │   ├── Step2\_Engine.axaml

# │   │   │   │   ├── Step3\_Process.axaml

# │   │   │   │   ├── Step4\_Results.axaml

# │   │   │   │   └── StepDedup.axaml

# │   │   │   └── Controls/           # Reusable: ProgressRing, LogViewer, ModelPicker, etc.

# │   │   ├── Services/

# │   │   │   ├── InferenceSidecarService.cs      # Launches/manages sidecar process

# │   │   │   ├── InferenceApiClient.cs           # HttpClient wrapper for /tag, /models/\*

# │   │   │   ├── DaminionApiClient.cs            # Refit-based Daminion REST client

# │   │   │   ├── MetadataWriterService.cs        # EXIF/IPTC via MetadataExtractor

# │   │   │   ├── DedupService.cs                 # pHash/dHash (NetVips or custom)

# │   │   │   ├── ConfigService.cs                # JSON config in %APPDATA%/Synapic/

# │   │   │   └── LoggingService.cs               # Serilog → file + UI log sink

# │   │   ├── Models/

# │   │   │   ├── Session.cs

# │   │   │   ├── DatasourceConfig.cs

# │   │   │   ├── EngineConfig.cs

# │   │   │   ├── TagResult.cs

# │   │   │   └── DaminionItem.cs

# │   │   ├── Converters/             # Avalonia value converters

# │   │   └── Resources/              # Icons, styles, themes

# │   │

# │   ├── Synapic.Inference/          # Python sidecar source (copied from current repo)

# │   │   ├── service.py              # FastAPI app (entry point for PyInstaller)

# │   │   ├── model\_loader.py         # HF model download/load (from huggingface\_utils.py)

# │   │   ├── inference\_engine.py     # run\_inference() for all model types

# │   │   ├── tag\_extractor.py        # JSON parsing, probability scoring (from image\_processing.py)

# │   │   ├── daminion\_client.py      # Optional: if sidecar needs direct Daminion calls

# │   │   ├── requirements.txt        # Pinned deps for reproducible build

# │   │   ├── pyproject.toml

# │   │   └── synapic-inference.spec  # PyInstaller spec file

# │   │

# │   └── Synapic.Shared/             # Shared DTOs (C# ↔ Python JSON contracts)

# │       ├── Synapic.Shared.csproj

# │       ├── Contracts/

# │       │   ├── TagRequest.cs / TagResponse.cs

# │       │   ├── ModelInfo.cs

# │       │   ├── HealthResponse.cs

# │       │   └── ConfigDto.cs

# │       └── JsonSerializerContext.cs  # Source-generated JSON serialization

# │

# ├── tests/

# │   ├── Synapic.Avalonia.Tests/     # Unit tests (xUnit + Avalonia.Headless)

# │   ├── Synapic.Inference.Tests/    # pytest for Python sidecar

# │   └── Synapic.Integration.Tests/  # End-to-end (require Daminion test server)

# │

# ├── docs/

# │   ├── architecture.md

# │   ├── sidecar-protocol.md         # HTTP API specification

# │   ├── packaging.md

# │   └── migration-checklist.md

# │

# ├── Synapic.Net.sln

# ├── Directory.Build.props           # Common MSBuild properties

# ├── global.json                     # SDK version pin

# ├── nuget.config

# └── README.md

# ```

# 

# \---

# 

# \## 4. Python Sidecar Specification (`Synapic.Inference`)

# 

# \### 4.1 PyInstaller Configuration (`synapic-inference.spec`)

# 

# ```python

# \# synapic-inference.spec

# block\_cipher = None

# 

# \# ── Collect all transformers/torch dependencies ──

# from PyInstaller.utils.hooks import collect\_submodules, collect\_data\_files

# 

# hiddenimports = \[

# &#x20;   # Core

# &#x20;   'torch', 'torchvision', 'torchaudio',

# &#x20;   'transformers', 'accelerate', 'safetensors',

# &#x20;   'huggingface\_hub', 'tokenizers',

# &#x20;   # Vision

# &#x20;   'PIL', 'cv2', 'imagehash',

# &#x20;   # FastAPI

# &#x20;   'fastapi', 'uvicorn', 'pydantic', 'pydantic\_core',

# &#x20;   'httpx', 'httpcore',

# &#x20;   # Stdlib modules that PyInstaller misses

# &#x20;   'json', 'pathlib', 'dataclasses', 'typing',

# ]

# 

# datas = \[

# &#x20;   # Include model cache if baking weights at build time

# &#x20;   # ('models', 'models'),

# ] + collect\_data\_files('transformers') + collect\_data\_files('tokenizers')

# 

# a = Analysis(

# &#x20;   \['service.py'],

# &#x20;   pathex=\['.'],

# &#x20;   binaries=\[],

# &#x20;   datas=datas,

# &#x20;   hiddenimports=hiddenimports,

# &#x20;   hookspath=\[],

# &#x20;   hooksconfig={},

# &#x20;   runtime\_hooks=\[],

# &#x20;   excludes=\[

# &#x20;       'tkinter', 'matplotlib', 'jupyter', 'notebook',

# &#x20;       'pytest', 'sphinx', 'docutils',

# &#x20;       'torch.testing', 'torch.distributed',

# &#x20;   ],

# &#x20;   cipher=block\_cipher,

# &#x20;   noarchive=False,

# )

# 

# \# ── Strip debug symbols, compress ──

# pyz = PYZ(a.pure, a.zipped\_data, cipher=block\_cipher)

# 

# exe = EXE(

# &#x20;   pyz,

# &#x20;   a.scripts,

# &#x20;   a.binaries,

# &#x20;   a.zipfiles,

# &#x20;   a.datas,

# &#x20;   \[],

# &#x20;   name='synapic-inference',

# &#x20;   debug=False,

# &#x20;   bootloader\_ignore\_signals=False,

# &#x20;   strip=True,

# &#x20;   upx=True,                    # Requires UPX installed

# &#x20;   upx\_exclude=\[],

# &#x20;   runtime\_tmpdir=None,

# &#x20;   console=True,                # Keep console for logging; hide via Avalonia CREATE\_NO\_WINDOW

# &#x20;   disable\_windowed\_traceback=False,

# &#x20;   argv\_emulation=False,

# &#x20;   target\_arch=None,

# &#x20;   codesign\_identity=None,

# &#x20;   entitlements\_file=None,

# )

# ```

# 

# \*\*Expected output size:\*\* 180–350 MB (depends on torch wheel + model weights baked in)

# 

# \### 4.2 HTTP API Contract (`/docs/sidecar-protocol.md`)

# 

# ```yaml

# openapi: 3.0.3

# info:

# &#x20; title: Synapic Inference API

# &#x20; version: 1.0.0

# servers:

# &#x20; - url: http://127.0.0.1:{port}

# &#x20;   description: Local sidecar (port assigned at launch)

# 

# paths:

# &#x20; /health:

# &#x20;   get:

# &#x20;     summary: Health \& readiness check

# &#x20;     responses:

# &#x20;       '200':

# &#x20;         description: Sidecar status

# &#x20;         content:

# &#x20;           application/json:

# &#x20;             schema:

# &#x20;               $ref: '#/components/schemas/HealthResponse'

# 

# &#x20; /models/list:

# &#x20;   get:

# &#x20;     summary: List available local models (from HF cache)

# &#x20;     responses:

# &#x20;       '200':

# &#x20;         content:

# &#x20;           application/json:

# &#x20;             schema:

# &#x20;               type: array

# &#x20;               items: { $ref: '#/components/schemas/ModelInfo' }

# 

# &#x20; /models/download:

# &#x20;   post:

# &#x20;     summary: Download a model from HF Hub

# &#x20;     requestBody:

# &#x20;       required: true

# &#x20;       content:

# &#x20;         application/json:

# &#x20;           schema:

# &#x20;             $ref: '#/components/schemas/DownloadRequest'

# &#x20;     responses:

# &#x20;       '200':

# &#x20;         description: Download started (poll /models/list for completion)

# &#x20;       '202':

# &#x20;         description: Already downloading

# 

# &#x20; /tag:

# &#x20;   post:

# &#x20;     summary: Run inference on single image

# &#x20;     requestBody:

# &#x20;       required: true

# &#x20;       content:

# &#x20;         application/json:

# &#x20;           schema:

# &#x20;             $ref: '#/components/schemas/TagRequest'

# &#x20;     responses:

# &#x20;       '200':

# &#x20;         content:

# &#x20;           application/json:

# &#x20;             schema:

# &#x20;               $ref: '#/components/schemas/TagResponse'

# &#x20;       '404': { description: Image not found }

# &#x20;       '503': { description: Model not loaded }

# 

# &#x20; /config:

# &#x20;   get:

# &#x20;     summary: Get current inference config

# &#x20;   put:

# &#x20;     summary: Update inference config (device, thresholds, etc.)

# 

# &#x20; /shutdown:

# &#x20;   post:

# &#x20;     summary: Graceful shutdown

# &#x20;     responses:

# &#x20;       '200': { description: Shutting down }

# 

# components:

# &#x20; schemas:

# &#x20;   HealthResponse:

# &#x20;     type: object

# &#x20;     properties:

# &#x20;       status: { type: string, enum: \[loading, ready, error] }

# &#x20;       model: { type: string }

# &#x20;       device: { type: string }

# &#x20;       vram\_used\_mb: { type: integer }

# &#x20;       error: { type: string, nullable: true }

# 

# &#x20;   ModelInfo:

# &#x20;     type: object

# &#x20;     properties:

# &#x20;       id: { type: string }

# &#x20;       task: { type: string }

# &#x20;       size\_mb: { type: number }

# &#x20;       path: { type: string }

# &#x20;       downloaded: { type: boolean }

# 

# &#x20;   DownloadRequest:

# &#x20;     type: object

# &#x20;     required: \[model\_id]

# &#x20;     properties:

# &#x20;       model\_id: { type: string }

# &#x20;       revision: { type: string, default: "main" }

# 

# &#x20;   TagRequest:

# &#x20;     type: object

# &#x20;     required: \[image\_path]

# &#x20;     properties:

# &#x20;       image\_path: { type: string }

# &#x20;       model\_id: { type: string }          # Optional: override session model

# &#x20;       task: { type: string }              # image-classification, image-text-to-text, zero-shot

# &#x20;       options:

# &#x20;         type: object

# &#x20;         properties:

# &#x20;           confidence\_threshold: { type: number, minimum: 0, maximum: 1, default: 0.3 }

# &#x20;           probability\_mode: { type: string, enum: \[llm, probability, both], default: "both" }

# &#x20;           probability\_threshold: { type: number, minimum: 0, maximum: 1, default: 0.5 }

# &#x20;           candidate\_labels: { type: array, items: { type: string } }

# &#x20;           system\_prompt: { type: string }

# &#x20;           max\_new\_tokens: { type: integer, default: 512 }

# 

# &#x20;   TagResponse:

# &#x20;     type: object

# &#x20;     properties:

# &#x20;       category: { type: string }

# &#x20;       keywords: { type: array, items: { type: string } }

# &#x20;       description: { type: string }

# &#x20;       probabilities: { type: object, additionalProperties: { type: number } }

# &#x20;       scoring: { type: object }           # Tier-annotated scoring result

# &#x20;       inference\_ms: { type: integer }

# &#x20;       model\_used: { type: string }

# ```

# 

# \### 4.3 Python Module Responsibilities

# 

# | Module | Responsibility | Source (Current Repo) |

# |--------|---------------|----------------------|

# | `service.py` | FastAPI app, lifespan, routing, port file write | New (thin wrapper) |

# | `model\_loader.py` | `load\_model()`, `download\_model()`, cache management | `huggingface\_utils.py` |

# | `inference\_engine.py` | `run\_inference(model, image\_path, options)` → raw model output | `processing.py` (inference section) |

# | `tag\_extractor.py` | `extract\_tags(raw\_output, task, options)` → TagResponse | `image\_processing.py`, `keyword\_scoring\_adapters.py` |

# | `daminion\_client.py` | (Optional) Direct Daminion calls if sidecar handles thumbnails | `daminion\_client.py` |

# 

# \*\*Critical:\*\* Keep `model\_loader.py` and `inference\_engine.py` as close to current logic as possible. Only adapt I/O boundaries (file paths → HTTP).

# 

# \---

# 

# \## 5. Avalonia Frontend Specification (`Synapic.Avalonia`)

# 

# \### 5.1 Technology Stack

# 

# | Component | Library | Version |

# |-----------|---------|---------|

# | UI Framework | Avalonia | 11.1+ |

# | MVVM | CommunityToolkit.Mvvm | 8.2+ |

# | DI/Hosting | Microsoft.Extensions.\* | 8.0+ |

# | HTTP Client | Refit | 7.0+ |

# | JSON | System.Text.Json (source-gen) | 8.0+ |

# | Metadata | MetadataExtractor | 2.8+ |

# | Image Processing | NetVips (libvips bindings) | 2.2+ |

# | Perceptual Hash | Custom (pHash/dHash) | — |

# | Logging | Serilog + Serilog.Sinks.File + Custom UI sink | 3.1+ |

# | Config | JSON in `%APPDATA%/Synapic/config.json` | — |

# | Packaging | Inno Setup / AppImage / create-dmg | — |

# 

# \### 5.2 Wizard Flow (Preserved from Current)

# 

# ```

# Step 1: Datasource ──────────────────► Step 2: Engine ──────────────────► Step 3: Process ──────────────────► Step 4: Results

# &#x20;  ├─ Local Folder                            ├─ Local (HF)                         ├─ Progress: items, ETA, log        ├─ Grid: file, status, tags

# &#x20;  ├─ Daminion Server                         ├─   Model picker (HF cache + search) ├─ Controls: pause, abort, retry    ├─ Actions: export CSV, retry

# &#x20;  │   ├─ Server URL                          ├─   Device: CPU/CUDA/MPS             ├─ Live log (colorized)             └─ Verify Daminion writes

# &#x20;  │   ├─ Auth (user/pass/token)              ├─   Thresholds                       └─ Memory/VRAM monitor

# &#x20;  │   ├─ Catalog / Scope                     ├─ OpenRouter / Groq (cloud)

# &#x20;  │   ├─ Filters: status, untagged fields    │   ├─ API key

# &#x20;  │   └─ Test Connection                     │   └─ Model list from API

# &#x20;  └─ Recursive scan                          └─ Probability mode (llm/prob/both)

# ```

# 

# \### 5.3 Core Services (C#)

# 

# \#### `InferenceSidecarService.cs`

# ```csharp

# public interface IInferenceSidecar : IAsyncDisposable

# {

# &#x20;   Task StartAsync(CancellationToken ct = default);

# &#x20;   Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default);

# &#x20;   Task<ModelInfo\[]> ListModelsAsync(CancellationToken ct = default);

# &#x20;   Task DownloadModelAsync(string modelId, IProgress<double> progress, CancellationToken ct);

# &#x20;   event Action<string> LogReceived;  // Forward sidecar stdout/stderr to UI

# }

# ```

# 

# \*\*Implementation details:\*\*

# \- `StartAsync`: resolves sidecar path (`AppContext.BaseDirectory/synapic-inference.exe`), launches with `--port=0`, reads assigned port from `%TEMP%/synapic\_port\_{pid}.txt`, polls `/health` (max 120s). **Started automatically on app launch (default) or manually from the Start Server button when `ui.autoLaunchSidecar` is `false`.**

# \- `StopAsync`: `POST /shutdown` → wait 5s → `Process.Kill(true)`. **Always invoked on Avalonia exit** so the sidecar never lingers as an orphan consuming CPU/memory.

# \- `TagAsync`: `POST /tag` with 5-min timeout; retries once on 503 (model loading)

# \- \*\*Port file protocol\*\*: sidecar writes `port\\npid\\n` on startup; Avalonia reads, validates PID alive

# \- \*\*Status tracking\*\*: `CurrentStatus` property (`Stopped` / `Starting` / `Ready` / `Error`) plus a `StatusChanged` event; the UI binds to this for the server indicator and Start/Stop buttons

# 

# \#### `DaminionApiClient.cs` (Refit)

# ```csharp

# public interface IDaminionApi

# {

# &#x20;   \[Get("/api/items")]

# &#x20;   Task<DaminionPagedResponse<DaminionItem>> GetItemsFiltered(

# &#x20;       \[Query] string scope,

# &#x20;       \[Query] int? savedSearchId,

# &#x20;       \[Query] int? collectionId,

# &#x20;       \[Query] string searchTerm,

# &#x20;       \[Query] string\[] untaggedFields,

# &#x20;       \[Query] string statusFilter,

# &#x20;       \[Query] int maxItems = 500,

# &#x20;       \[Query] int startIndex = 0);

# 

# &#x20;   \[Post("/api/items/{id}/metadata")]

# &#x20;   Task<bool> UpdateMetadata(int id, \[Body] MetadataUpdateRequest request);

# 

# &#x20;   \[Get("/api/items/{id}/thumbnail")]

# &#x20;   Task<Stream> GetThumbnail(int id, \[Query] int width, \[Query] int height);

# 

# &#x20;   \[Get("/api/items/{id}/original")]

# &#x20;   Task<Stream> GetOriginal(int id);

# }

# ```

# 

# \#### `MetadataWriterService.cs`

# ```csharp

# public interface IMetadataWriter

# {

# &#x20;   Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct);

# &#x20;   Task<TagResult> ReadAsync(string filePath, CancellationToken ct);

# }

# 

# // Implementation uses MetadataExtractor for reading

# // For writing: 

# //   - JPEG: MetadataExtractor + custom IPTC/EXIF injection (or ExifLib)

# //   - PNG/TIFF: tEXt/iTXt chunks + IPTC via libvips (NetVips)

# ```

# 

# \#### `DedupService.cs`

# ```csharp

# public interface IDedupService

# {

# &#x20;   Task<DedupResult> FindDuplicatesAsync(IEnumerable<string> imagePaths, DedupOptions opts, IProgress<DedupProgress> progress, CancellationToken ct);

# &#x20;   Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct);

# }

# 

# public record DedupOptions(

# &#x20;   HashAlgorithm Algorithm = HashAlgorithm.PHash,  // PHash, DHash, AHash, ColorMoment

# &#x20;   double Threshold = 0.90,                        // Similarity threshold

# &#x20;   int MaxDimension = 512                          // Resize before hash

# );

# 

# public enum HashAlgorithm { PHash, DHash, AHash, ColorMoment }

# public enum DedupAction { Tag, Move, Delete }

# ```

# 

# \*\*Implementation:\*\* Use `NetVips` for fast perceptual hashing (libvips is 10–50× faster than Pillow). Fallback to custom C# pHash if libvips deployment proves problematic.

# 

# \---

# 

# \## 6. Configuration \& Persistence

# 

# \### 6.1 Config File (`%APPDATA%/Synapic/config.json` / `\~/.config/Synapic/config.json`)

# ```json

# {

# &#x20; "version": 2,

# &#x20; "datasource": {

# &#x20;   "type": "local|daminion",

# &#x20;   "localPath": "",

# &#x20;   "localRecursive": true,

# &#x20;   "daminion": { "serverUrl": "", "username": "", "token": "", "catalogId": 1, ... }

# &#x20; },

# &#x20; "engine": {

# &#x20;   "provider": "local|openrouter|groq",

# &#x20;   "local": { "modelId": "LiquidAI/LFM2.5-VL-1.6B", "task": "image-text-to-text", "device": "cuda", "confidenceThreshold": 0.3, "probabilityMode": "both", "probabilityThreshold": 0.5 },

# &#x20;   "openrouter": { "apiKey": "", "modelId": "google/gemini-2.0-flash" },

# &#x20;   "groq": { "apiKey": "", "modelId": "llama-3.2-90b-vision" }

# &#x20; },

# &#x20; "processing": { "maxItems": 0, "autoPaginate": true, "resizeScale": 100, "useThumbnailOverride": false },

# &#x20; "ui": { "theme": "system", "logLevel": "info", "autoLaunchSidecar": true }

# }

# ```

# 

# \### 6.2 Model Cache Location

# \- \*\*Windows:\*\* `%LOCALAPPDATA%\\Synapic\\models\\` (symlink to HF cache or standalone)

# \- \*\*Linux/macOS:\*\* `\~/.cache/synapic/models/`

# \- Sidecar reads `HF\_HOME` env var set by Avalonia at launch

# 

# \---

# 

# \## 7. Build \& Packaging Pipeline

# 

# \### 7.1 GitHub Actions Matrix Build (`.github/workflows/build.yml`)

# 

# ```yaml

# name: Build \& Package

# 

# on:

# &#x20; push:

# &#x20;   branches: \[main]

# &#x20;   tags: \['v\*']

# &#x20; workflow\_dispatch:

# 

# jobs:

# &#x20; build-sidecar:

# &#x20;   runs-on: ${{ matrix.os }}

# &#x20;   strategy:

# &#x20;     matrix:

# &#x20;       include:

# &#x20;         - os: windows-2022

# &#x20;           rid: win-x64

# &#x20;           python: "python-3.11-embed-amd64.zip"

# &#x20;           artifact: synapic-inference.exe

# &#x20;         - os: ubuntu-22.04

# &#x20;           rid: linux-x64

# &#x20;           python: "cpython-3.11.9+20240409-x86\_64-unknown-linux-gnu-install\_only.tar.gz"

# &#x20;           artifact: synapic-inference

# &#x20;         - os: macos-14

# &#x20;           rid: osx-x64

# &#x20;           python: "cpython-3.11.9+20240409-x86\_64-apple-darwin-install\_only.tar.gz"

# &#x20;           artifact: synapic-inference

# &#x20;         - os: macos-14

# &#x20;           rid: osx-arm64

# &#x20;           python: "cpython-3.11.9+20240409-arm64-apple-darwin-install\_only.tar.gz"

# &#x20;           artifact: synapic-inference

# &#x20;   steps:

# &#x20;     - uses: actions/checkout@v4

# &#x20;     - name: Setup .NET

# &#x20;       uses: actions/setup-dotnet@v4

# &#x20;       with: { dotnet-version: '10.0.x' }

# &#x20;     - name: Fetch Python (standalone)

# &#x20;       run: ./build/fetch-python.sh ${{ matrix.rid }}

# &#x20;     - name: Install Python deps

# &#x20;       run: ./build/install-python-deps.sh ${{ matrix.rid }}

# &#x20;     - name: Build sidecar (PyInstaller)

# &#x20;       run: ./build/build-sidecar.sh ${{ matrix.rid }}

# &#x20;     - name: Build Avalonia

# &#x20;       run: dotnet publish src/Synapic.Avalonia -c Release -r ${{ matrix.rid }} --self-contained -p:PublishSingleFile=true -o artifacts/${{ matrix.rid }}

# &#x20;     - name: Stage bundle

# &#x20;       run: |

# &#x20;         cp artifacts/${{ matrix.rid }}/synapic-inference\* artifacts/${{ matrix.rid }}/

# &#x20;     - name: Package installer

# &#x20;       run: ./build/package-${{ matrix.os }}.sh

# &#x20;     - name: Upload artifacts

# &#x20;       uses: actions/upload-artifact@v4

# &#x20;       with:

# &#x20;         name: Synapic-${{ matrix.rid }}

# &#x20;         path: artifacts/${{ matrix.rid }}/installer.\*

# ```

# 

# \### 7.2 Installer Specifications

# 

# | Platform | Tool | Output | Key Steps |

# |----------|------|--------|-----------|

# | \*\*Windows\*\* | Inno Setup 6 | `Synapic-Setup-x64.exe` | Sign with EV cert; `PrivilegesRequired=admin` for Program Files; create Start Menu + Desktop shortcuts; uninstaller removes `%APPDATA%/Synapic` optionally |

# | \*\*Linux\*\* | AppImage (linuxdeploy) | `Synapic-x86\_64.AppImage` | Bundle `synapic-inference` + `.so` deps; `AppRun` launches Avalonia exe; desktop integration via `xdg-desktop-icon` |

# | \*\*macOS (Intel)\*\* | create-dmg + codesign | `Synapic-x64.dmg` | Hardened runtime; notarize with `notarytool`; Gatekeeper stapling; `Info.plist` with `LSMinimumSystemVersion=10.15` |

# | \*\*macOS (ARM)\*\* | create-dmg + codesign | `Synapic-arm64.dmg` | Same as x64; universal binary not needed (separate builds) |

# 

# \*\*Code Signing:\*\*

# \- Windows: EV certificate (USB token or Azure Key Vault via `signtool`)

# \- macOS: Developer ID Application + Developer ID Installer certs; `--timestamp --options runtime`

# \- Linux: Optional GPG signature on AppImage

# 

# \---

# 

# \## 8. Migration Phases \& Milestones

# 

# \### Phase 0: Foundation (Week 1)

# \- \[ ] Create `Synapic.Net` solution with 3 projects (Avalonia, Inference, Shared)

# \- \[ ] Set up CI/CD matrix build (4 RIDs)

# \- \[ ] Port `huggingface\_utils.py` → `model\_loader.py` + `inference\_engine.py` (minimal changes)

# \- \[ ] Write `service.py` + `synapic-inference.spec`

# \- \[ ] Verify PyInstaller builds on all 4 runners

# \- \[ ] Verify Avalonia `dotnet publish -r win-x64 -r linux-x64 -r osx-x64 -r osx-arm64` works

# 

# \*\*Deliverable:\*\* `synapic-inference.exe` + empty Avalonia window with a **Start Server** button that launches the sidecar and calls `/health` (auto-launched by default; set `ui.autoLaunchSidecar=false` for manual Start/Stop)

# 

# \### Phase 1: Core Inference Loop (Week 2–3)

# \- \[ ] Implement `InferenceSidecarService` + `InferenceApiClient`

# \- \[ ] Implement `TagRequest/TagResponse` DTOs with source-gen JSON

# \- \[ ] Port `image\_processing.extract\_tags\_from\_result` → `tag\_extractor.py`

# \- \[ ] Port probability scoring (`keyword\_scoring\_adapters`) → Python sidecar

# \- \[ ] End-to-end test: Avalonia button → `/tag` → displays category/keywords/description

# \- \[ ] Add model picker UI (calls `/models/list`, `/models/download`)

# 

# \*\*Deliverable:\*\* Working "Select Model → Tag Image" flow for local LFM2.5-VL-1.6B

# 

# \### Phase 2: Datasource \& Daminion (Week 3–4)

# \- \[ ] Port `daminion\_api.py` + `daminion\_client.py` → `DaminionApiClient.cs` (Refit)

# \- \[ ] Implement Step 1 UI (Local folder browser + Daminion connection wizard)

# \- \[ ] Implement `\_fetch\_items` logic (local recursive scan + Daminion paginated fetch)

# \- \[ ] Implement metadata writer (`MetadataWriterService`) using MetadataExtractor + NetVips

# \- \[ ] Test Daminion round-trip: fetch → tag → write → verify

# 

# \*\*Deliverable:\*\* Full datasource → engine → process → write pipeline for both local and Daminion

# 

# \### Phase 3: Wizard UI \& Session State (Week 4–5)

# \- \[ ] Build all 4 wizard steps in Avalonia XAML + ViewModels

# \- \[ ] Implement `Session` model + `ConfigService` (JSON persistence)

# \- \[ ] Progress reporting: `IProgress<ProcessProgress>` from sidecar → UI

# \- \[ ] Logging: Serilog file sink + `LogReceived` event from sidecar → UI log view

# \- \[ ] Abort/pause/resume handling (sidecar respects cancellation)

# 

# \*\*Deliverable:\*\* Complete wizard UX matching current Synapic flow

# 

# \### Phase 4: Deduplication (Week 5–6)

# \- \[ ] Implement pHash/dHash in C# (NetVips or custom)

# \- \[ ] Build StepDedup UI (side-by-side comparison, auto-select, bulk actions)

# \- \[ ] Integrate with Daminion (tag duplicates / delete from catalog)

# \- \[ ] Performance test: 10k+ images

# 

# \*\*Deliverable:\*\* Feature-parity deduplication wizard

# 

# \### Phase 5: Polish \& Distribution (Week 6–7)

# \- \[ ] Dark/light theme, high-DPI scaling, accessibility

# \- \[ ] First-run model download with progress (bake common models at build time)

# \- \[ ] Auto-update check (GitHub Releases API)

# \- \[ ] Installer branding, EULA, start menu entries

# \- \[ ] Code signing + notarization pipeline

# \- \[ ] Cross-platform smoke tests (VMs or GitHub Actions matrix)

# 

# \*\*Deliverable:\*\* Signed installers for all 4 platforms

# 

# \### Phase 6: Beta \& Hardening (Week 7–8+)

# \- \[ ] Internal beta with 5–10 power users

# \- \[ ] Crash reporting (Sentry or custom)

# \- \[ ] Performance profiling (dotnet-trace, py-spy)

# \- \[ ] Memory leak testing (long runs, 10k+ images)

# \- \[ ] Documentation update

# 

# \---

# 

# \## 9. Risk Register \& Mitigations

# 

# | Risk | Likelihood | Impact | Mitigation |

# |------|------------|--------|------------|

# | PyInstaller bundle too large (>500 MB) | Medium | High | Use `--exclude-module` aggressively; consider `torch` CPU-only wheel; compress with UPX; offer "lite" installer without baked models |

# | Sidecar cold start >10s (model load) | High | Medium | Show splash screen with progress; pre-warm model on sidecar startup (lazy load on first `/tag`); bake model weights into sidecar |

# | CUDA/ROCm/Metal compatibility across user GPUs | High | High | Ship `torch` CPU + CUDA 11.8 + CUDA 12.1 wheels; detect at runtime; fallback to CPU; document GPU requirements |

# | libvips (NetVips) deployment issues on Linux/macOS | Medium | Medium | Static-link libvips in PyInstaller; provide fallback C# pHash implementation |

# | Daminion API changes break client | Low | High | Version-detect Daminion server; graceful degradation; integration tests against test server |

# | macOS notarization fails on Python binaries | Medium | High | Sign all `.dylib`/`.so` in `site-packages` with `codesign --deep --force --sign`; test on clean VM |

# | Port conflict (multiple instances) | Medium | Low | Random port + PID file; second instance detects running sidecar and reuses it |

# | Antivirus false positive on PyInstaller exe | Medium | Medium | EV code sign; submit to Microsoft/AV vendors; avoid UPX if it triggers heuristics |

# 

# \---

# 

# \## 10. Testing Strategy

# 

# | Layer | Tool | Coverage Target |

# |-------|------|-----------------|

# | \*\*C# Unit\*\* | xUnit + Avalonia.Headless | 80% services, 90% ViewModels |

# | \*\*Python Unit\*\* | pytest | 85% inference modules |

# | \*\*Contract\*\* | Pact / custom JSON schema validation | 100% API surface |

# | \*\*Integration\*\* | Testcontainers (Daminion test image) + local images | Happy paths + error cases |

# | \*\*E2E\*\* | Playwright (Avalonia) + headless sidecar | Full wizard flows |

# | \*\*Performance\*\* | BenchmarkDotNet (C#) + py-spy (Python) | <2s/image (1.6B, GPU), <500 MB RSS |

# 

# \---

# 

# \## 11. Open Questions (Resolve Before Phase 1)

# 

# 1\. \*\*Model baking:\*\* Bake LFM2.5-VL-1.6B weights into sidecar at build time (\~2 GB), or download on first run? (Recommend: bake for offline UX)

# 2\. \*\*Daminion thumbnail download:\*\* Keep in Avalonia (current) or move to sidecar? (Keep in Avalonia — avoids passing image bytes over HTTP)

# 3\. \*\*Probability scoring location:\*\* Python sidecar (current) or C#? (Keep in Python — uses transformers pipeline directly)

# 4\. \*\*Cloud API keys:\*\* Store in config.json (encrypted via DPAPI/Keychain/libsecret) or OS credential manager? (Use `Microsoft.Extensions.Configuration.KeyPerFile` + platform secure storage)

# 5\. \*\*Telemetry:\*\* Opt-in anonymous usage stats? (Add `telemetryEnabled` config; send only version, OS, model used, processing time)

# 

# \---

# 

# \## 12. Appendix: File-by-File Porting Map

# 

# | Current (Python) | New (C# / Python) | Notes |

# |------------------|-------------------|-------|

# | `main.py` | `Program.cs` + `App.axaml.cs` | Entry point, sidecar launch |

# | `src/ui/app.py` | `MainWindowViewModel.cs` + `WizardViewModel.cs` | Wizard orchestration |

# | `src/ui/steps/step1\_datasource.py` | `Step1\_DatasourceViewModel.cs` + `.axaml` | Local + Daminion |

# | `src/ui/steps/step2\_tagging.py` | `Step2\_EngineViewModel.cs` + `.axaml` | Model picker, cloud/local tabs |

# | `src/ui/steps/step3\_process.py` | `Step3\_ProcessViewModel.cs` + `.axaml` | Progress, log, abort |

# | `src/ui/steps/step4\_results.py` | `Step4\_ResultsViewModel.cs` + `.axaml` | Grid, export, verify |

# | `src/ui/steps/step\_dedup.py` | `StepDedupViewModel.cs` + `.axaml` | Dedup wizard |

# | `src/core/processing.py` | `InferenceApiClient.cs` + `TagExtractor` (Python) | Inference loop moved to sidecar |

# | `src/core/huggingface\_utils.py` | `model\_loader.py` (Python) | Nearly identical |

# | `src/core/daminion\_api.py` + `daminion\_client.py` | `DaminionApiClient.cs` (Refit) | Mechanical port |

# | `src/core/image\_processing.py` | `tag\_extractor.py` (Python) + `MetadataWriterService.cs` | Split: extraction in Python, writing in C# |

# | `src/core/keyword\_scoring\*.py` | `tag\_extractor.py` (Python) | Keep in Python |

# | `src/core/dedup/\*.py` | `DedupService.cs` (NetVips) | Rewrite in C# for speed |

# | `src/core/config.py` + `config\_manager.py` | `ConfigService.cs` | JSON + source-gen |

# | `src/utils/logger.py` | `LoggingService.cs` (Serilog) | Structured logging |

# | `src/utils/concurrency.py` | `DaemonThreadPoolExecutor` → `Task.Run` + `SemaphoreSlim` | Simpler in .NET |

# 

# \---

# 

# \## 13. Sign-Off

# 

# | Role | Name | Date | Signature |

# |------|------|------|-----------|

# | Technical Lead | | | |

# | Product Owner | | | |

# | DevOps / Release | | | |

# | QA Lead | | | |

# 

# \---

# 

# \*\*Next Step:\*\* Approve this spec → create GitHub repo `Synapic.Net` → bootstrap Phase 0 tasks. Want me to generate the initial solution structure, `.csproj` files, and the first GitHub Actions workflow?

