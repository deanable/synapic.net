---
phase: 04-extract-headless-engine-core
plan: 04
subsystem: core
tags: [test-extraction, phase-gate, core-04, dependency-proof]

# Dependency graph
requires:
  - phase: 04-extract-headless-engine-core
    provides: 04-03 (kernel in Core, Main repointed, baseline Main 738 passed / 3 skipped)
provides:
  - tests/Synapic.Core.Tests — xunit only, ProjectReference to Synapic.Core, no desktop test host
  - phase 4 gate record (five criteria, each with the command that proves it)
affects: [Phase 5, Phase 6, Phase 7, Phase 8 (tests inherit this headless pattern)]

# Tech tracking
tech-stack:
  added: [tests/Synapic.Core.Tests]
  patterns:
    - "A headless test project is the proof of headlessness: if the engine needed a UI type, these tests could not compile"
    - "Two test assemblies keep their own stubs (FixedTagSidecar, CapturingMetadataWriter) rather than referencing each other"

key-files:
  modified:
    - Synapic.Net.sln
    - src/Synapic.Core/Synapic.Core.csproj (dropped InternalsVisibleTo for Main.Tests)
    - tests/Synapic.Main.Tests/SidecarDeviceSelectionTests.cs (DI test targets EngineServiceRegistration)
  created:
    - tests/Synapic.Core.Tests/Synapic.Core.Tests.csproj
    - tests/Synapic.Core.Tests/*.cs (10 moved files + SidecarDeviceProviderTests)
    - tests/Synapic.Core.Tests/TestData/sample.jpg
    - tests/Synapic.Main.Tests/CapturingMetadataWriter.cs

key-decisions:
  - "The two Daminion test files that look pure stay in Main.Tests: DaminionQueryLoggingTests asserts against DaminionSelectionBehaviorTests' loopback stub and DaminionVersionControlRequestTests drives UpscaleItemHandler, both desktop types — moving them would have meant moving the desktop code they pin"
  - "Core.Tests carries its own sample.jpg instead of reaching into another project's TestData: a test that depends on a sibling project's file layout is a hidden coupling, even when it compiles"
  - "Core exposes no internals to Synapic.Main.Tests — nothing in the desktop suite needed them once the service tests moved"

patterns-established:
  - "A moved test keeps its method names and assertions; the diff is namespace and `using` lines only"

requirements-completed: [CORE-04, CORE-01, CORE-02]

# Metrics
duration: ~45min
completed: 2026-10-22
---

# Phase 4 Plan 04: Headless Core test project and the phase gate

**The engine now has a test home that cannot load the desktop stack — 144 Core cases, 756 across the solution (+4 new, 0 deleted) — and all five Phase 4 criteria pass on evidence**

## Performance

- **Duration:** ~45 min
- **Tasks:** 2
- **Files changed:** 10 tests moved, 2 test files created, 1 fixture copied, solution + Core csproj edited

## Accomplishments

- **`Synapic.Core.Tests` is genuinely headless.** [Synapic.Core.Tests.csproj](tests/Synapic.Core.Tests/Synapic.Core.Tests.csproj) references `Synapic.Core` and xunit only — no Avalonia package, no desktop test host, no `Synapic.Main` project reference. `Synapic.Core` no longer grants `InternalsVisibleTo` to `Synapic.Main.Tests`, because nothing there needed it after the move.
- **Ten service-only test files moved**: `DaminionBaseUrlTests`, `DaminionCaptureDateTests`, `DaminionCatalogGuidTests`, `DaminionDeleteRequestTests`, `DaminionFlagRequestTests`, `DaminionIndexedTagValuesRequestTests`, `DaminionLayoutParsingTests`, `DaminionTempDownloadTests`, `MetadataWriterTests`, `TagFieldSelectionTests`. Their bodies are unchanged; the diff is the namespace and the assembly prefix. `SidecarDeviceProviderTests` (4 facts) came across as a new file — 04-01 had already written the provider facts in their own file, so nothing had to be deleted from `SidecarDeviceSelectionTests`, which keeps all 13 of its facts.
- **Six UI-dependent files stayed**, as the plan requires: `DaminionConnectionStoreTests`, `DaminionEmptyTagResultTests`, `DaminionSelectionBehaviorTests`, `DaminionVerificationBehaviorTests`, `ProcessingEtaTests`, `SidecarDeviceSelectionTests`.
- **Two more stayed for a proven reason.** A trial move showed `DaminionQueryLoggingTests` asserting through `DaminionSelectionBehaviorTests`' loopback stub and `DaminionVersionControlRequestTests` driving `UpscaleItemHandler`; both would have dragged desktop code into the headless project, so they were returned to Main.Tests.
- **No test was deleted.** Method-name diff per moved file: 76 methods at HEAD, 80 now (76 preserved + 4 new provider facts), **0 missing**. Case counts: Core 144, Main 601 (598 passed / 3 skipped), Shared 6, Integration 5 → **756** versus the 04-03 baseline of 752, the difference being exactly the 4 new provider facts.
- **Two small fixes the move required.** `FixedTagSidecar.TagAsync` became `virtual` because a Main test overrides it, and Main.Tests received its own `CapturingMetadataWriter` (the original moved to Core with the kernel tests).

## Verification — the five Phase 4 criteria

| # | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | Core in the solution, `net10.0`, no Avalonia package, no `Synapic.Main` reference | ✓ | `dotnet list src/Synapic.Core/Synapic.Core.csproj reference` → `..\Synapic.Shared\Synapic.Shared.csproj` only; `dotnet list ... package` → MetadataExtractor 2.9.3, NetVips 3.2.0, NetVips.Native 8.18.6, Refit 7.2.22, Serilog 4.0.2, Serilog.Sinks.File 6.0.0, Serilog.Sinks.Debug 3.0.0 |
| 2 | Build 0/0, suite green, test count equal with zero tests deleted | ✓ | `dotnet build Synapic.Net.sln -c Release` → **0 warnings, 0 errors**; Core **144/144**, Main **598 passed / 3 skipped / 0 failed**, Shared **6/6**, Integration **5/5**; totals 756 = 752 baseline + 4 new; method-name diff **0 missing** |
| 3 | Main compiles against Core, desktop suite unchanged | ✓ | Main builds with a single project reference added (`Synapic.Core`); every Main test file keeps its pre-move facts (e.g. `SidecarDeviceSelectionTests` 13 → 13, `DaminionSelectionBehaviorTests` 29 → 30, the +1 being pre-existing uncommitted work from before this phase) |
| 4 | Sidecar host constructible and drivable with a fake provider and no `Session` | ✓ | `SidecarDeviceProviderTests` — `new InferenceSidecarService(options: new SidecarLaunchOptions { DeviceProvider = new StaticSidecarDeviceProvider("cuda") })` asserts `DeviceSelection == "cuda"`; no provider → `cpu`; unknown → `cpu`; dispose leaves nothing running. All 4 pass in Core.Tests |
| 5 | Compiled Core output references neither Avalonia nor Synapic.Main | ✓ | `src/Synapic.Core/bin/Release/net10.0/Synapic.Core.deps.json` → 20 libraries, zero names starting `Avalonia` or `Synapic.Main`; `grep -rn "Avalonia\|Synapic.Main" src/Synapic.Core tests/Synapic.Core.Tests --exclude-dir=obj --exclude-dir=bin` → empty |

Other checks:
- `grep -rn "Avalonia" tests/Synapic.Core.Tests` → no matches in source or project files.
- `ls tests/Synapic.Main.Tests/MetadataWriterTests.cs` → absent (moved, not copied).

## Issues Encountered

- **A mechanical `using`-dedupe step removed C# `using var` declarations** from the files it touched. The affected files were restored from `HEAD` and only the intended namespace change was re-applied; `git diff -U0 -- tests | grep '^-.*using var '` is now empty, so no declaration is missing.
- **One headless UI test failed once in a full run** (`WorkflowOrderTests.The_run_bar_shows_the_modes_own_stop_and_pause_actions`) and passed in isolation and on the complete re-run (598/598). It exercises the same run bar regardless of the extraction and is order-sensitive under the serial headless host; recorded as flaky, not as a pass-by-assumption — the suite was re-run rather than the test skipped.
- **`dotnet test Synapic.Net.sln -c Release` exceeds the 10-minute command cap on this machine**, so the gate ran per test project. `Synapic.Main.Tests` needs `xUnit.parallelizeTestCollections=false` here; parallel headless collections crash the test host on this machine, which is pre-existing and unrelated to the extraction.

## Next Phase Readiness

- Phase 5 can reference `Synapic.Core` from a new host and register `ISidecarDeviceProvider` over its configuration; `SidecarLaunchOptions.ExecutablePathOverride` is where a packaged sidecar path goes.
- Phase 7 calls `ProcessingOrchestrator` directly and writes through `IMetadataWriter`; there is one implementation, in Core.
- The engine has no desktop dependency left to unwind: the remaining coupling is the desktop's choice of adapter, not the engine's requirement.
