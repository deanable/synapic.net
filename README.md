# Synapic .NET Migration Specification

## Avalonia UI + Python Sidecar (PyInstaller) Architecture

This is a port from https://github.com/deanable/Synapic Located at C:\Users\deank\repos\Synapic



*Version:* 1.0  

**Target:** Cross-platform (Windows, Linux, macOS)  

**Timeline:** 6–8 weeks for MVP, 10–12 weeks for feature parity  



---



## 1. Executive Summary



Migrate Synapic from Python/CustomTkinter to **C#/Avalonia** for the UI and orchestration layer, while preserving the existing **Python inference engine** as a bundled PyInstaller executable (`synapic-inference.exe`). This eliminates user-facing Python dependency hell while retaining 100% model fidelity (LFM2.5-VL, BLIP, CLIP, etc.).



### Key Decisions

| Decision | Rationale |

|----------|-----------|

| **Avalonia 11+** | Native cross-platform, XAML, MVVM, mature ecosystem |

| **Python sidecar via PyInstaller** | Zero user Python install; preserves all HF/transformers logic unchanged |

| **HTTP/JSON IPC (localhost)** | Simple, debuggable, language-agnostic; supports future gRPC upgrade |

| **Single installer per platform** | Inno Setup (Windows), AppImage (Linux), DMG (macOS) |

| **Model weights baked at build time** | No first-run downloads; works offline |



---



## 2. High-Level Architecture



```

┌─────────────────────────────────────────────────────────────────────────────┐

│                          SYNAPIC APPLICATION BUNDLE                         │

├─────────────────────────────────────────────────────────────────────────────┤

│                                                                             │

│  ┌─────────────────────────────┐     HTTP/JSON (127.0.0.1:5001)     ┌─────┐│

│  │   AVALONIA FRONTEND (C#)    │ ◄─────────────────────────────────► │     ││

│  │   ┌─────────────────────┐   │                                     │     ││

│  │   │  Wizard Controller  │   │   /health        →  {"status":"ok"} │     ││

│  │   │  (Step 1–4 + Dedup) │   │   /tag           →  {cat, kws, desc}│     ││

│  │   └─────────────────────┘   │   /models/list   →  [{id, task...}] │     ││

│  │   ┌─────────────────────┐   │   /models/download                │     ││

│  │   │  Session State      │   │   /config/get/set                 │     ││

│  │   │  (Config, Progress) │   │                                     │     ││

│  │   └─────────────────────┘   │                                     │     ││

│  │   ┌─────────────────────┐   │                                     │  ▼  ││

│  │   │  Daminion Client    │   │   ┌─────────────────────────────┐  │     ││

│  │   │  (HttpClient/Refit) │   │   │  PYTHON SIDECAR             │  │     ││

│  │   └─────────────────────┘   │   │  (synapic-inference.exe)    │  │     ││

│  │   ┌─────────────────────┐   │   │   ┌───────────────────────┐ │  │     ││

│  │   │  Metadata Writer    │   │   │   │ FastAPI + Uvicorn     │ │  │     ││

│  │   │  (MetadataExtractor,│   │   │   │   /health             │ │  │     ││

│  │   │   ExifLib, custom)  │   │   │   │   /tag                │ │  │     ││

│  │   └─────────────────────┘   │   │   │   /models/*           │ │  │     ││

│  └─────────────────────────────┘   │   │   │ ModelLoader         │ │  │     ││

│                                    │   │   │   - HF Hub download │ │  │     ││

│                                    │   │   │   - transformers    │ │  │     ││

│                                    │   │   │   - torch/cuda/mps  │ │  │     ││

│                                    │   │   │   - tokenizer       │ │  │     ││

│                                    │   │   └───────────────────────┘ │  │     ││

│                                    │   └─────────────────────────────┘  │     ││

│                                    └─────────────────────────────────────┘     │

│                                                                             │

└─────────────────────────────────────────────────────────────────────────────┘

```



### Process Lifecycle

> **Design note (reconciled with the shipped implementation):** The sidecar **starts automatically with the app** and is stopped unconditionally on exit. The lifecycle is strictly one server per app instance — a server left over from a previous run is never adopted. Users who want a manual-only workflow set `ui.autoLaunchSidecar` to `false` in `config.json` and use the toolbar **Start Server** button. There is no Options panel in the shipped UI yet, so the setting is config-file-only.

#### Automatic Launch (default)
1. **App start** → Avalonia launches the sidecar with `--port=0` (OS assigns a free port) → the sidecar writes the port to `%TEMP%/synapic_port_{pid}.txt`.
2. **Avalonia reads the port**, polls `/health` until `{"status":"ready"}` (max 120s).
3. **All inference requests** route through `HttpClient` with a 5-min timeout.
4. **On Avalonia shutdown** → `POST /shutdown` → sidecar exits gracefully; fallback `Process.Kill()` after 5s.

#### Manual Launch (opt-out)
- Set `ui.autoLaunchSidecar` to `false` in `config.json`.
- The app then starts with the server stopped; press **Start Server** on the toolbar to run the exact sequence above on demand.
- The setting persists in `config.json` (`ui.autoLaunchSidecar`, default `true`).
- Either way the sidecar is always stopped when the app exits (see the shutdown flow below) — it never lingers as an orphan.

#### Server Status
- The UI shows a **server status indicator** (stopped / starting / ready / error).
- A **Stop Server** button is available while the server is running, so the user can shut it down early without closing the app.
- If the sidecar process dies unexpectedly, Avalonia detects it (PID liveness + failed `/health` polls) and shows the user a "Server stopped unexpectedly — restart?" prompt.



---



## 3. Repository Structure



```

Synapic.Net/

├── build/                          # Build & packaging scripts

│   ├── fetch-python.ps1            # Downloads python-embeddable / python-build-standalone

│   ├── install-python-deps.ps1     # pip installs into bundled site-packages

│   ├── build-sidecar.ps1           # Runs PyInstaller → synapic-inference.exe

│   ├── package-windows.ps1         # Inno Setup compiler invocation

│   ├── package-linux.sh            # AppImage creation

│   ├── package-macos.sh            # create-dmg + notarization

│   └── github-actions/             # CI/CD workflows

│       ├── build.yml               # Matrix build for 4 RIDs

│       └── release.yml             # Tag → build → sign → upload assets

│

├── src/

│   ├── Synapic.Avalonia/           # Main Avalonia application

│   │   ├── Synapic.Avalonia.csproj

│   │   ├── Program.cs

│   │   ├── App.axaml / App.axaml.cs

│   │   ├── ViewModels/

│   │   │   ├── MainWindowViewModel.cs

│   │   │   ├── WizardViewModel.cs

│   │   │   ├── Step1_DatasourceViewModel.cs

│   │   │   ├── Step2_EngineViewModel.cs

│   │   │   ├── Step3_ProcessViewModel.cs

│   │   │   ├── Step4_ResultsViewModel.cs

│   │   │   └── StepDedupViewModel.cs

│   │   ├── Views/

│   │   │   ├── MainWindow.axaml

│   │   │   ├── Wizard/

│   │   │   │   ├── Step1_Datasource.axaml

│   │   │   │   ├── Step2_Engine.axaml

│   │   │   │   ├── Step3_Process.axaml

│   │   │   │   ├── Step4_Results.axaml

│   │   │   │   └── StepDedup.axaml

│   │   │   └── Controls/           # Reusable: ProgressRing, LogViewer, ModelPicker, etc.

│   │   ├── Services/

│   │   │   ├── InferenceSidecarService.cs      # Launches/manages sidecar process

│   │   │   ├── InferenceApiClient.cs           # HttpClient wrapper for /tag, /models/*

│   │   │   ├── DaminionApiClient.cs            # Refit-based Daminion REST client

│   │   │   ├── MetadataWriterService.cs        # EXIF/IPTC via MetadataExtractor

│   │   │   ├── DedupService.cs                 # pHash/dHash (NetVips or custom)

│   │   │   ├── ConfigService.cs                # JSON config in %APPDATA%/Synapic/

│   │   │   └── LoggingService.cs               # Serilog → file + UI log sink

│   │   ├── Models/

│   │   │   ├── Session.cs

│   │   │   ├── DatasourceConfig.cs

│   │   │   ├── EngineConfig.cs

│   │   │   ├── TagResult.cs

│   │   │   └── DaminionItem.cs

│   │   ├── Converters/             # Avalonia value converters

│   │   └── Resources/              # Icons, styles, themes

│   │

│   ├── Synapic.Inference/          # Python sidecar source (copied from current repo)

│   │   ├── service.py              # FastAPI app (entry point for PyInstaller)

│   │   ├── model_loader.py         # HF model download/load (from huggingface_utils.py)

│   │   ├── inference_engine.py     # run_inference() for all model types

│   │   ├── tag_extractor.py        # JSON parsing, probability scoring (from image_processing.py)

│   │   ├── daminion_client.py      # Optional: if sidecar needs direct Daminion calls

│   │   ├── requirements.txt        # Pinned deps for reproducible build

│   │   ├── pyproject.toml

│   │   └── synapic-inference.spec  # PyInstaller spec file

│   │

│   └── Synapic.Shared/             # Shared DTOs (C# ↔ Python JSON contracts)

│       ├── Synapic.Shared.csproj

│       ├── Contracts/

│       │   ├── TagRequest.cs / TagResponse.cs

│       │   ├── ModelInfo.cs

│       │   ├── HealthResponse.cs

│       │   └── ConfigDto.cs

│       └── JsonSerializerContext.cs  # Source-generated JSON serialization

│

├── tests/

│   ├── Synapic.Avalonia.Tests/     # Unit tests (xUnit + Avalonia.Headless)

│   ├── Synapic.Inference.Tests/    # pytest for Python sidecar

│   └── Synapic.Integration.Tests/  # End-to-end (require Daminion test server)

│

├── docs/

│   ├── architecture.md

│   ├── sidecar-protocol.md         # HTTP API specification

│   ├── packaging.md

│   └── migration-checklist.md

│

├── Synapic.Net.sln

├── Directory.Build.props           # Common MSBuild properties

├── global.json                     # SDK version pin

├── nuget.config

└── README.md

```



---



## 4. Python Sidecar Specification (`Synapic.Inference`)



### 4.1 PyInstaller Configuration (`synapic-inference.spec`)



```python

# synapic-inference.spec

block_cipher = None



# ── Collect all transformers/torch dependencies ──

from PyInstaller.utils.hooks import collect_submodules, collect_data_files



hiddenimports = [

    # Core

    'torch', 'torchvision', 'torchaudio',

    'transformers', 'accelerate', 'safetensors',

    'huggingface_hub', 'tokenizers',

    # Vision

    'PIL', 'cv2', 'imagehash',

    # FastAPI

    'fastapi', 'uvicorn', 'pydantic', 'pydantic_core',

    'httpx', 'httpcore',

    # Stdlib modules that PyInstaller misses

    'json', 'pathlib', 'dataclasses', 'typing',

]



datas = [

    # Include model cache if baking weights at build time

    # ('models', 'models'),

] + collect_data_files('transformers') + collect_data_files('tokenizers')



a = Analysis(

    ['service.py'],

    pathex=['.'],

    binaries=[],

    datas=datas,

    hiddenimports=hiddenimports,

    hookspath=[],

    hooksconfig={},

    runtime_hooks=[],

    excludes=[

        'tkinter', 'matplotlib', 'jupyter', 'notebook',

        'pytest', 'sphinx', 'docutils',

        'torch.testing', 'torch.distributed',

    ],

    cipher=block_cipher,

    noarchive=False,

)



# ── Strip debug symbols, compress ──

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)



exe = EXE(

    pyz,

    a.scripts,

    a.binaries,

    a.zipfiles,

    a.datas,

    [],

    name='synapic-inference',

    debug=False,

    bootloader_ignore_signals=False,

    strip=True,

    upx=True,                    # Requires UPX installed

    upx_exclude=[],

    runtime_tmpdir=None,

    console=True,                # Keep console for logging; hide via Avalonia CREATE_NO_WINDOW

    disable_windowed_traceback=False,

    argv_emulation=False,

    target_arch=None,

    codesign_identity=None,

    entitlements_file=None,

)

```



**Expected output size:** 180–350 MB (depends on torch wheel + model weights baked in)



### 4.2 HTTP API Contract (`/docs/sidecar-protocol.md`)



```yaml

openapi: 3.0.3

info:

  title: Synapic Inference API

  version: 1.0.0

servers:

  - url: http://127.0.0.1:{port}

    description: Local sidecar (port assigned at launch)



paths:

  /health:

    get:

      summary: Health & readiness check

      responses:

        '200':

          description: Sidecar status

          content:

            application/json:

              schema:

                $ref: '#/components/schemas/HealthResponse'



  /models/list:

    get:

      summary: List available local models (from HF cache)

      responses:

        '200':

          content:

            application/json:

              schema:

                type: array

                items: { $ref: '#/components/schemas/ModelInfo' }



  /models/download:

    post:

      summary: Download a model from HF Hub

      requestBody:

        required: true

        content:

          application/json:

            schema:

              $ref: '#/components/schemas/DownloadRequest'

      responses:

        '200':

          description: Download started (poll /models/list for completion)

        '202':

          description: Already downloading



  /tag:

    post:

      summary: Run inference on single image

      requestBody:

        required: true

        content:

          application/json:

            schema:

              $ref: '#/components/schemas/TagRequest'

      responses:

        '200':

          content:

            application/json:

              schema:

                $ref: '#/components/schemas/TagResponse'

        '404': { description: Image not found }

        '503': { description: Model not loaded }



  /config:

    get:

      summary: Get current inference config

    put:

      summary: Update inference config (device, thresholds, etc.)



  /shutdown:

    post:

      summary: Graceful shutdown

      responses:

        '200': { description: Shutting down }



components:

  schemas:

    HealthResponse:

      type: object

      properties:

        status: { type: string, enum: [loading, ready, error] }

        model: { type: string }

        device: { type: string }

        vram_used_mb: { type: integer }

        error: { type: string, nullable: true }



    ModelInfo:

      type: object

      properties:

        id: { type: string }

        task: { type: string }

        size_mb: { type: number }

        path: { type: string }

        downloaded: { type: boolean }



    DownloadRequest:

      type: object

      required: [model_id]

      properties:

        model_id: { type: string }

        revision: { type: string, default: "main" }



    TagRequest:

      type: object

      required: [image_path]

      properties:

        image_path: { type: string }

        model_id: { type: string }          # Optional: override session model

        task: { type: string }              # image-classification, image-text-to-text, zero-shot

        options:

          type: object

          properties:

            confidence_threshold: { type: number, minimum: 0, maximum: 1, default: 0.3 }

            probability_mode: { type: string, enum: [llm, probability, both], default: "both" }

            probability_threshold: { type: number, minimum: 0, maximum: 1, default: 0.5 }

            candidate_labels: { type: array, items: { type: string } }

            system_prompt: { type: string }

            max_new_tokens: { type: integer, default: 512 }



    TagResponse:

      type: object

      properties:

        category: { type: string }

        keywords: { type: array, items: { type: string } }

        description: { type: string }

        probabilities: { type: object, additionalProperties: { type: number } }

        scoring: { type: object }           # Tier-annotated scoring result

        inference_ms: { type: integer }

        model_used: { type: string }

```



### 4.3 Python Module Responsibilities



| Module | Responsibility | Source (Current Repo) |

|--------|---------------|----------------------|

| `service.py` | FastAPI app, lifespan, routing, port file write | New (thin wrapper) |

| `model_loader.py` | `load_model()`, `download_model()`, cache management | `huggingface_utils.py` |

| `inference_engine.py` | `run_inference(model, image_path, options)` → raw model output | `processing.py` (inference section) |

| `tag_extractor.py` | `extract_tags(raw_output, task, options)` → TagResponse | `image_processing.py`, `keyword_scoring_adapters.py` |

| `daminion_client.py` | (Optional) Direct Daminion calls if sidecar handles thumbnails | `daminion_client.py` |



**Critical:** Keep `model_loader.py` and `inference_engine.py` as close to current logic as possible. Only adapt I/O boundaries (file paths → HTTP).



---



## 5. Avalonia Frontend Specification (`Synapic.Avalonia`)



### 5.1 Technology Stack



| Component | Library | Version |

|-----------|---------|---------|

| UI Framework | Avalonia | 11.1+ |

| MVVM | CommunityToolkit.Mvvm | 8.2+ |

| DI/Hosting | Microsoft.Extensions.* | 8.0+ |

| HTTP Client | Refit | 7.0+ |

| JSON | System.Text.Json (source-gen) | 8.0+ |

| Metadata | MetadataExtractor | 2.8+ |

| Image Processing | NetVips (libvips bindings) | 2.2+ |

| Perceptual Hash | Custom (pHash/dHash) | — |

| Logging | Serilog + Serilog.Sinks.File + Custom UI sink | 3.1+ |

| Config | JSON in `%APPDATA%/Synapic/config.json` | — |

| Packaging | Inno Setup / AppImage / create-dmg | — |



### 5.2 Wizard Flow (Preserved from Current)



```

Step 1: Datasource ──────────────────► Step 2: Engine ──────────────────► Step 3: Process ──────────────────► Step 4: Results

   ├─ Local Folder                            ├─ Local (HF)                         ├─ Progress: items, ETA, log        ├─ Grid: file, status, tags

   ├─ Daminion Server                         ├─   Model picker (HF cache + search) ├─ Controls: pause, abort, retry    ├─ Actions: export CSV, retry

   │   ├─ Server URL                          ├─   Device: CPU/CUDA/MPS             ├─ Live log (colorized)             └─ Verify Daminion writes

   │   ├─ Auth (user/pass/token)              ├─   Thresholds                       └─ Memory/VRAM monitor

   │   ├─ Catalog / Scope                     ├─ OpenRouter / Groq (cloud)

   │   ├─ Filters: status, untagged fields    │   ├─ API key

   │   └─ Test Connection                     │   └─ Model list from API

   └─ Recursive scan                          └─ Probability mode (llm/prob/both)

```



### 5.3 Core Services (C#)



#### `InferenceSidecarService.cs`

```csharp

public interface IInferenceSidecar : IAsyncDisposable

{

    Task StartAsync(CancellationToken ct = default);

    Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default);

    Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default);

    Task DownloadModelAsync(string modelId, IProgress<double> progress, CancellationToken ct);

    event Action<string> LogReceived;  // Forward sidecar stdout/stderr to UI

}

```



**Implementation details:**

- `StartAsync`: resolves sidecar path (`AppContext.BaseDirectory/synapic-inference.exe`), launches with `--port=0`, reads assigned port from `%TEMP%/synapic_port_{pid}.txt`, polls `/health` (max 120s). **Started automatically on app launch (default) or manually from the Start Server button when `ui.autoLaunchSidecar` is `false`.**

- `StopAsync`: `POST /shutdown` → wait 5s → `Process.Kill(true)`. **Always invoked on Avalonia exit** so the sidecar never lingers as an orphan consuming CPU/memory.

- `TagAsync`: `POST /tag` with 5-min timeout; retries once on 503 (model loading)

- **Port file protocol**: sidecar writes `port\npid\n` on startup; Avalonia reads, validates PID alive

- **Status tracking**: `CurrentStatus` property (`Stopped` / `Starting` / `Ready` / `Error`) plus a `StatusChanged` event; the UI binds to this for the server indicator and Start/Stop buttons



#### `DaminionApiClient.cs` (Refit)

```csharp

public interface IDaminionApi

{

    [Get("/api/items")]

    Task<DaminionPagedResponse<DaminionItem>> GetItemsFiltered(

        [Query] string scope,

        [Query] int? savedSearchId,

        [Query] int? collectionId,

        [Query] string searchTerm,

        [Query] string[] untaggedFields,

        [Query] string statusFilter,

        [Query] int maxItems = 500,

        [Query] int startIndex = 0);



    [Post("/api/items/{id}/metadata")]

    Task<bool> UpdateMetadata(int id, [Body] MetadataUpdateRequest request);



    [Get("/api/items/{id}/thumbnail")]

    Task<Stream> GetThumbnail(int id, [Query] int width, [Query] int height);



    [Get("/api/items/{id}/original")]

    Task<Stream> GetOriginal(int id);

}

```



#### `MetadataWriterService.cs`

```csharp

public interface IMetadataWriter

{

    Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct);

    Task<TagResult> ReadAsync(string filePath, CancellationToken ct);

}



// Implementation uses MetadataExtractor for reading

// For writing: 

//   - JPEG: MetadataExtractor + custom IPTC/EXIF injection (or ExifLib)

//   - PNG/TIFF: tEXt/iTXt chunks + IPTC via libvips (NetVips)

```



#### `DedupService.cs`

```csharp

public interface IDedupService

{

    Task<DedupResult> FindDuplicatesAsync(IEnumerable<string> imagePaths, DedupOptions opts, IProgress<DedupProgress> progress, CancellationToken ct);

    ulong? ComputeHash(string path, DedupOptions opts);              // null when unreadable

    DedupResult GroupFromHashes(IReadOnlyDictionary<string, ulong> hashes, DedupOptions opts);

    Task<bool> ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, CancellationToken ct);

    Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct);

}



public record DedupOptions(

    HashAlgorithm Algorithm = HashAlgorithm.PHash,  // PHash, DHash, AHash, ColorMoment

    double Threshold = 0.90,                        // Similarity threshold

    int MaxDimension = 512                          // Resize before hash

);



public enum HashAlgorithm { PHash, DHash, AHash, ColorMoment }

public enum DedupAction { Tag, Move, Delete }

```



**Implementation:** Use `NetVips` for fast perceptual hashing (libvips is 10–50× faster than Pillow). Fallback to custom C# pHash if libvips deployment proves problematic.



---



## 6. Configuration & Persistence



### 6.1 Config File (`%APPDATA%/Synapic/config.json` / `~/.config/Synapic/config.json`)

```json

{

  "version": 2,

  "datasource": {

    "type": "local|daminion",

    "localPath": "",

    "localRecursive": true,

    "daminion": { "serverUrl": "", "username": "", "token": "", "catalogId": 1, ... }

  },

  "engine": {

    "provider": "local|openrouter|groq",

    "local": { "modelId": "LiquidAI/LFM2.5-VL-1.6B", "task": "image-text-to-text", "device": "cuda", "confidenceThreshold": 0.3, "probabilityMode": "both", "probabilityThreshold": 0.5 },

    "openrouter": { "apiKey": "", "modelId": "google/gemini-2.0-flash" },

    "groq": { "apiKey": "", "modelId": "llama-3.2-90b-vision" }

  },

  "processing": { "maxItems": 0, "autoPaginate": true, "resizeScale": 100, "useThumbnailOverride": false },

  "ui": { "theme": "system", "logLevel": "info", "autoLaunchSidecar": true }

}

```



### 6.2 Model Cache Location

- **Windows:** `%LOCALAPPDATA%\Synapic\models\` (symlink to HF cache or standalone)

- **Linux/macOS:** `~/.cache/synapic/models/`

- Sidecar reads `HF_HOME` env var set by Avalonia at launch



---



## 7. Build & Packaging Pipeline



### 7.1 GitHub Actions Matrix Build (`.github/workflows/build.yml`)



```yaml

name: Build & Package



on:

  push:

    branches: [main]

    tags: ['v*']

  workflow_dispatch:



jobs:

  build-sidecar:

    runs-on: ${{ matrix.os }}

    strategy:

      matrix:

        include:

          - os: windows-2022

            rid: win-x64

            python: "python-3.11-embed-amd64.zip"

            artifact: synapic-inference.exe

          - os: ubuntu-22.04

            rid: linux-x64

            python: "cpython-3.11.9+20240409-x86_64-unknown-linux-gnu-install_only.tar.gz"

            artifact: synapic-inference

          - os: macos-14

            rid: osx-x64

            python: "cpython-3.11.9+20240409-x86_64-apple-darwin-install_only.tar.gz"

            artifact: synapic-inference

          - os: macos-14

            rid: osx-arm64

            python: "cpython-3.11.9+20240409-arm64-apple-darwin-install_only.tar.gz"

            artifact: synapic-inference

    steps:

      - uses: actions/checkout@v4

      - name: Setup .NET

        uses: actions/setup-dotnet@v4

        with: { dotnet-version: '10.0.x' }

      - name: Fetch Python (standalone)

        run: ./build/fetch-python.sh ${{ matrix.rid }}

      - name: Install Python deps

        run: ./build/install-python-deps.sh ${{ matrix.rid }}

      - name: Build sidecar (PyInstaller)

        run: ./build/build-sidecar.sh ${{ matrix.rid }}

      - name: Build Avalonia

        run: dotnet publish src/Synapic.Avalonia -c Release -r ${{ matrix.rid }} --self-contained -p:PublishSingleFile=true -o artifacts/${{ matrix.rid }}

      - name: Stage bundle

        run: |

          cp artifacts/${{ matrix.rid }}/synapic-inference* artifacts/${{ matrix.rid }}/

      - name: Package installer

        run: ./build/package-${{ matrix.os }}.sh

      - name: Upload artifacts

        uses: actions/upload-artifact@v4

        with:

          name: Synapic-${{ matrix.rid }}

          path: artifacts/${{ matrix.rid }}/installer.*

```



### 7.2 Installer Specifications



| Platform | Tool | Output | Key Steps |

|----------|------|--------|-----------|

| **Windows** | Inno Setup 6 | `Synapic-Setup-x64.exe` | Sign with EV cert; `PrivilegesRequired=admin` for Program Files; create Start Menu + Desktop shortcuts; uninstaller removes `%APPDATA%/Synapic` optionally |

| **Linux** | AppImage (linuxdeploy) | `Synapic-x86_64.AppImage` | Bundle `synapic-inference` + `.so` deps; `AppRun` launches Avalonia exe; desktop integration via `xdg-desktop-icon` |

| **macOS (Intel)** | create-dmg + codesign | `Synapic-x64.dmg` | Hardened runtime; notarize with `notarytool`; Gatekeeper stapling; `Info.plist` with `LSMinimumSystemVersion=10.15` |

| **macOS (ARM)** | create-dmg + codesign | `Synapic-arm64.dmg` | Same as x64; universal binary not needed (separate builds) |



**Code Signing:**

- Windows: EV certificate (USB token or Azure Key Vault via `signtool`)

- macOS: Developer ID Application + Developer ID Installer certs; `--timestamp --options runtime`

- Linux: Optional GPG signature on AppImage



---



## 8. Migration Phases & Milestones



### Phase 0: Foundation (Week 1)

- [ ] Create `Synapic.Net` solution with 3 projects (Avalonia, Inference, Shared)

- [ ] Set up CI/CD matrix build (4 RIDs)

- [ ] Port `huggingface_utils.py` → `model_loader.py` + `inference_engine.py` (minimal changes)

- [ ] Write `service.py` + `synapic-inference.spec`

- [ ] Verify PyInstaller builds on all 4 runners

- [ ] Verify Avalonia `dotnet publish -r win-x64 -r linux-x64 -r osx-x64 -r osx-arm64` works



**Deliverable:** `synapic-inference.exe` + empty Avalonia window with a **Start Server** button that launches the sidecar and calls `/health` (auto-launched by default; set `ui.autoLaunchSidecar=false` for manual Start/Stop)



### Phase 1: Core Inference Loop (Week 2–3)

- [ ] Implement `InferenceSidecarService` + `InferenceApiClient`

- [ ] Implement `TagRequest/TagResponse` DTOs with source-gen JSON

- [ ] Port `image_processing.extract_tags_from_result` → `tag_extractor.py`

- [ ] Port probability scoring (`keyword_scoring_adapters`) → Python sidecar

- [ ] End-to-end test: Avalonia button → `/tag` → displays category/keywords/description

- [ ] Add model picker UI (calls `/models/list`, `/models/download`)



**Deliverable:** Working "Select Model → Tag Image" flow for local LFM2.5-VL-1.6B



### Phase 2: Datasource & Daminion (Week 3–4)

- [ ] Port `daminion_api.py` + `daminion_client.py` → `DaminionApiClient.cs` (Refit)

- [ ] Implement Step 1 UI (Local folder browser + Daminion connection wizard)

- [ ] Implement `_fetch_items` logic (local recursive scan + Daminion paginated fetch)

- [ ] Implement metadata writer (`MetadataWriterService`) using MetadataExtractor + NetVips

- [ ] Test Daminion round-trip: fetch → tag → write → verify



**Deliverable:** Full datasource → engine → process → write pipeline for both local and Daminion



### Phase 3: Wizard UI & Session State (Week 4–5)

- [ ] Build all 4 wizard steps in Avalonia XAML + ViewModels

- [ ] Implement `Session` model + `ConfigService` (JSON persistence)

- [ ] Progress reporting: `IProgress<ProcessProgress>` from sidecar → UI

- [ ] Logging: Serilog file sink + `LogReceived` event from sidecar → UI log view

- [ ] Abort/pause/resume handling (sidecar respects cancellation)



**Deliverable:** Complete wizard UX matching current Synapic flow



### Phase 4: Deduplication (Week 5–6)

- [x] Implement pHash/dHash in C# (NetVips or custom)

- [x] Build StepDedup UI (duplicate-group cards with per-item keep checkboxes, auto-select: oldest/newest/smallest/largest, bulk actions)

- [x] Integrate with Daminion (source = Step 1 scope/collection, delete from catalog after confirmation)

- [ ] Performance test: 10k+ images



**Deliverable:** Feature-parity deduplication wizard



### Phase 5: Polish & Distribution (Week 6–7)

- [ ] Dark/light theme, high-DPI scaling, accessibility

- [ ] First-run model download with progress (bake common models at build time)

- [ ] Auto-update check (GitHub Releases API)

- [ ] Installer branding, EULA, start menu entries

- [ ] Code signing + notarization pipeline

- [ ] Cross-platform smoke tests (VMs or GitHub Actions matrix)



**Deliverable:** Signed installers for all 4 platforms



### Phase 6: Beta & Hardening (Week 7–8+)

- [ ] Internal beta with 5–10 power users

- [ ] Crash reporting (Sentry or custom)

- [ ] Performance profiling (dotnet-trace, py-spy)

- [ ] Memory leak testing (long runs, 10k+ images)

- [ ] Documentation update



---



## 9. Risk Register & Mitigations



| Risk | Likelihood | Impact | Mitigation |

|------|------------|--------|------------|

| PyInstaller bundle too large (>500 MB) | Medium | High | Use `--exclude-module` aggressively; consider `torch` CPU-only wheel; compress with UPX; offer "lite" installer without baked models |

| Sidecar cold start >10s (model load) | High | Medium | Show splash screen with progress; pre-warm model on sidecar startup (lazy load on first `/tag`); bake model weights into sidecar |

| CUDA/ROCm/Metal compatibility across user GPUs | High | High | Ship `torch` CPU + CUDA 11.8 + CUDA 12.1 wheels; detect at runtime; fallback to CPU; document GPU requirements |

| libvips (NetVips) deployment issues on Linux/macOS | Medium | Medium | Static-link libvips in PyInstaller; provide fallback C# pHash implementation |

| Daminion API changes break client | Low | High | Version-detect Daminion server; graceful degradation; integration tests against test server |

| macOS notarization fails on Python binaries | Medium | High | Sign all `.dylib`/`.so` in `site-packages` with `codesign --deep --force --sign`; test on clean VM |

| Port conflict (multiple instances) | Medium | Low | Random port + PID file; second instance detects running sidecar and reuses it |

| Antivirus false positive on PyInstaller exe | Medium | Medium | EV code sign; submit to Microsoft/AV vendors; avoid UPX if it triggers heuristics |



---



## 10. Testing Strategy



| Layer | Tool | Coverage Target |

|-------|------|-----------------|

| **C# Unit** | xUnit + Avalonia.Headless | 80% services, 90% ViewModels |

| **Python Unit** | pytest | 85% inference modules |

| **Contract** | Pact / custom JSON schema validation | 100% API surface |

| **Integration** | Testcontainers (Daminion test image) + local images | Happy paths + error cases |

| **E2E** | Playwright (Avalonia) + headless sidecar | Full wizard flows |

| **Performance** | BenchmarkDotNet (C#) + py-spy (Python) | <2s/image (1.6B, GPU), <500 MB RSS |



---



## 11. Open Questions (Resolve Before Phase 1)



1. **Model baking:** Bake LFM2.5-VL-1.6B weights into sidecar at build time (~2 GB), or download on first run? (Recommend: bake for offline UX)

2. **Daminion thumbnail download:** Keep in Avalonia (current) or move to sidecar? (Keep in Avalonia — avoids passing image bytes over HTTP)

3. **Probability scoring location:** Python sidecar (current) or C#? (Keep in Python — uses transformers pipeline directly)

4. **Cloud API keys:** Store in config.json (encrypted via DPAPI/Keychain/libsecret) or OS credential manager? (Use `Microsoft.Extensions.Configuration.KeyPerFile` + platform secure storage)

5. **Telemetry:** Opt-in anonymous usage stats? (Add `telemetryEnabled` config; send only version, OS, model used, processing time)



---



## 12. Appendix: File-by-File Porting Map



| Current (Python) | New (C# / Python) | Notes |

|------------------|-------------------|-------|

| `main.py` | `Program.cs` + `App.axaml.cs` | Entry point, sidecar launch |

| `src/ui/app.py` | `MainWindowViewModel.cs` + `WizardViewModel.cs` | Wizard orchestration |

| `src/ui/steps/step1_datasource.py` | `Step1_DatasourceViewModel.cs` + `.axaml` | Local + Daminion |

| `src/ui/steps/step2_tagging.py` | `Step2_EngineViewModel.cs` + `.axaml` | Model picker, cloud/local tabs |

| `src/ui/steps/step3_process.py` | `Step3_ProcessViewModel.cs` + `.axaml` | Progress, log, abort |

| `src/ui/steps/step4_results.py` | `Step4_ResultsViewModel.cs` + `.axaml` | Grid, export, verify |

| `src/ui/steps/step_dedup.py` | `StepDedupViewModel.cs` + `.axaml` | Dedup wizard |

| `src/core/processing.py` | `InferenceApiClient.cs` + `TagExtractor` (Python) | Inference loop moved to sidecar |

| `src/core/huggingface_utils.py` | `model_loader.py` (Python) | Nearly identical |

| `src/core/daminion_api.py` + `daminion_client.py` | `DaminionApiClient.cs` (Refit) | Mechanical port |

| `src/core/image_processing.py` | `tag_extractor.py` (Python) + `MetadataWriterService.cs` | Split: extraction in Python, writing in C# |

| `src/core/keyword_scoring*.py` | `tag_extractor.py` (Python) | Keep in Python |

| `src/core/dedup/*.py` | `DedupService.cs` (NetVips) | Rewrite in C# for speed |

| `src/core/config.py` + `config_manager.py` | `ConfigService.cs` | JSON + source-gen |

| `src/utils/logger.py` | `LoggingService.cs` (Serilog) | Structured logging |

| `src/utils/concurrency.py` | `DaemonThreadPoolExecutor` → `Task.Run` + `SemaphoreSlim` | Simpler in .NET |



---



## 13. Sign-Off



| Role | Name | Date | Signature |

|------|------|------|-----------|

| Technical Lead | | | |

| Product Owner | | | |

| DevOps / Release | | | |

| QA Lead | | | |



---



**Next Step:** Approve this spec → create GitHub repo `Synapic.Net` → bootstrap Phase 0 tasks. Want me to generate the initial solution structure, `.csproj` files, and the first GitHub Actions workflow?

