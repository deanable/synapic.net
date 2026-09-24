# Synapic.NET Documentation

Start here. This folder documents the shipped implementation; `README.md`
(repo root) is the original migration *specification* and `plan.md` is the
phase plan that tracked it.

| Document | What it covers |
|----------|----------------|
| [`codebase-guide.md`](codebase-guide.md) | **Start here.** Repository layout, how to build/run/test, the two-process architecture, DI wiring, data flow, concurrency, persistence, and a troubleshooting index. |
| [`architecture.md`](architecture.md) | Short component/process overview and the batch data flow. |
| [`csharp-reference.md`](csharp-reference.md) | The Avalonia host (`src/Synapic.Avalonia`, `src/Synapic.Shared`): every service, view model, model, and contract. |
| [`sidecar-reference.md`](sidecar-reference.md) | The Python sidecar (`src/Synapic.Inference`): routes, inference pipeline, scoring tiers, model loading, and prompt handling. |
| [`sidecar-protocol.md`](sidecar-protocol.md) | The HTTP/JSON contract between the two processes. **Generated** from the live FastAPI schema + the C# DTOs (`python build/generate-protocol-doc.py`); CI fails if it goes stale. |
| [`testing.md`](testing.md) | Test projects, what each suite pins down, and how to run them. |
| [`packaging.md`](packaging.md) | Installers, signing, bundle layout, upgrade path. |
| [`migration-checklist.md`](migration-checklist.md) | File-by-file porting map from the original Python app. |
| [`help/`](help/README.md) | The **user and administrator help** itself: HTML topics compiled into `Synapic.chm` by `docs/help/build-chm.ps1` (HTML Help Workshop). Separate from this developer documentation set, and checked by `docs/help/check-help.py`. |

## 30-second orientation

Synapic tags images with a local vision-language model and writes the results
to image files or a Daminion catalog. It is **two processes**:

```
Synapic.exe (Avalonia/C#)  ──HTTP/JSON 127.0.0.1:port──►  synapic-inference(.exe) (Python/FastAPI)
   wizard UI + orchestration                                transformers + torch inference
```

The C# app owns the UI, the wizard, Daminion I/O, file metadata writing, and
the processing loop. The Python sidecar owns model download/loading and
inference only; it never touches the filesystem except to read the image it is
asked to tag.

## Build / run / test cheat sheet

```bash
# .NET host (from repo root)
dotnet build Synapic.Net.sln -c Debug
dotnet run --project src/Synapic.Avalonia          # needs a built sidecar to tag

# Python sidecar from source (flat imports: run it from its own folder)
python -m venv .venv && source .venv/bin/activate   # Windows: .venv\Scripts\activate
pip install -r src/Synapic.Inference/requirements.txt
cd src/Synapic.Inference
SYNAPIC_PORT_FILE=/tmp/synapic_port.txt python service.py --port=0

# Build the packaged sidecar the app launches
build/build-server.ps1 win-x64        # Windows (CPU); -Rid win-x64-cuda for GPU
build/build-server.sh linux-x64      # Linux/macOS

# Tests
dotnet test Synapic.Net.sln -c Release
python -m pytest tests/Synapic.Inference.Tests
```

The host locates a sidecar either bundled next to `Synapic.exe` or in
`artifacts/<rid>/` of a dev checkout. With neither present the wizard stays
disabled and a **Build Server** panel is shown.
