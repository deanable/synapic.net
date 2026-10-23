---
phase: 04-extract-headless-engine-core
plan: 02
subsystem: core
tags: [extraction, metadata, xmp, daminion, refit, core-01, core-02]

# Dependency graph
requires:
  - phase: 04-extract-headless-engine-core
    provides: 04-01 (Synapic.Core exists, references Synapic.Shared, buildable)
provides:
  - Synapic.Core.Services.MetadataWriterService + IMetadataWriter, MetadataPatch and TagResult
  - Synapic.Core.Services.Daminion — DaminionApiClient, IDaminionApi, DaminionModels
  - Core package set completed: NetVips 3.2.0, MetadataExtractor 2.9.3, Refit 7.2.22
affects: [04-03, 04-04, Phase 7 (write-back is this writer, not a second implementation)]

# Tech tracking
tech-stack:
  added: [MetadataExtractor 2.9.3, Refit 7.2.22 to Synapic.Core]
  patterns:
    - "Move the behavior, leave the host policy: the Daminion credential store (HKCU + DPAPI) stays desktop-side and keeps handing plaintext to the client"

key-files:
  modified:
    - src/Synapic.Core/Synapic.Core.csproj
    - tests/Synapic.Main.Tests/DedupScan*Tests.cs, Step3*/Step4* construction sites (using updates only)
  created:
    - src/Synapic.Core/Services/MetadataWriterService.cs (moved)
    - src/Synapic.Core/Services/Daminion/DaminionApiClient.cs (moved)
    - src/Synapic.Core/Services/Daminion/DaminionModels.cs (moved)
    - src/Synapic.Core/Services/Daminion/IDaminionApi.cs (moved)

key-decisions:
  - "The write path moved whole — staged write, pixel validation, read-only/reparse-point refusal, TIFF sidecar pairing — rather than being reduced to the parts the indexer is expected to need"
  - "DaminionConnectionStore stays in Main: DPAPI-protected credentials in HKCU are a desktop-user concern; the service uses LocalMachine scope from its own configuration (D-11-07)"

patterns-established:
  - "A moved file keeps its namespace suffix (`Services.Daminion`), so the only edits at call sites are the assembly prefix"

requirements-completed: [CORE-01, CORE-02]

# Metrics
duration: ~30min
completed: 2026-10-22
---

# Phase 4 Plan 02: Metadata writer and Daminion client into Core

**Tags can now be written — to files or to a Daminion catalog — without the desktop assembly; 200 targeted tests still pass with unchanged assertions**

## Performance

- **Duration:** ~30 min
- **Tasks:** 3
- **Files changed:** Core csproj + 4 moved files; test call sites updated for the namespace only

## Accomplishments

- **The writer moved entire.** [MetadataWriterService.cs](src/Synapic.Core/Services/MetadataWriterService.cs) keeps its staged-write → validate → commit sequence, the NetVips pixel decode that rejects a corrupt file before anything is committed, the refusal of read-only images and symlinked artifacts, and the TIFF `.xmp` sidecar pairing. None of that behavior was re-derived.
- **The Daminion client moved entire.** [DaminionApiClient.cs](src/Synapic.Core/Services/Daminion/DaminionApiClient.cs), [IDaminionApi.cs](src/Synapic.Core/Services/Daminion/IDaminionApi.cs) and [DaminionModels.cs](src/Synapic.Core/Services/Daminion/DaminionModels.cs) live under `Synapic.Core.Services.Daminion`; the request construction, the version-control temp download, the indexed-tag-values query and the capture-date parsing are the same code the desktop shipped.
- **Core's package list grew by exactly what the moved files use.** `MetadataExtractor 2.9.3` (existing metadata reads) and `Refit 7.2.22` (the Daminion interface); `NetVips` was already needed by the writer and is pinned at 3.2.0 with `NetVips.Native 8.18.6`.
- **The desktop keeps only the credential policy.** `DaminionConnectionStore` (HKCU values, DPAPI-protected password) remains in `Synapic.Main` and still supplies a plaintext password to the client; the service host will read its own LocalMachine-scoped secret (D-11-07) and hand the same client the same strings.

## Verification

- `dotnet build Synapic.Net.sln --nologo -v minimal` → **0 warnings, 0 errors**
- `dotnet test tests/Synapic.Main.Tests/... --filter` over the metadata, Daminion and pipeline suites → **200 passed, 0 failed**
- `grep -rn "Synapic.Main" src/Synapic.Core` → no matches
- `git status --short src/Synapic.Main/Services/Daminion` → deletions only (moved, not copied)

## Decisions Made

- The whole writer moved, not a subset: the indexer's write-back is this one writer (Phase 7), and a trimmed copy would be the second implementation the milestone forbids.
- `DaminionConnectionStore` stays behind: it is the desktop's answer to "where does the password come from", not part of the engine.
