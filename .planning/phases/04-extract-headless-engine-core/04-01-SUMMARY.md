---
phase: 04-extract-headless-engine-core
plan: 01
subsystem: core
tags: [extraction, sidecar, serilog, dependency-injection, core-01, core-03]

# Dependency graph
requires:
  - phase: 03-collapse-navigation-and-retire-settings-dialogs
    provides: source tree stable; the v1.0 Services scope freeze ends with this phase
provides:
  - src/Synapic.Core — net10.0 class library, references Synapic.Shared only
  - ISidecarDeviceProvider + StaticSidecarDeviceProvider (Core) and SessionSidecarDeviceProvider (Main)
  - SidecarLaunchOptions (DeviceProvider, ExecutablePathOverride)
  - SynapicLog in Core with an optional sink-configuration hook
  - EngineServiceRegistration.AddSynapicEngine (Main) — the one place the desktop wires engine services
  - SidecarDeviceProviderTests (4 facts, later relocated to Synapic.Core.Tests by 04-04)
affects: [04-02, 04-03, 04-04, Phase 5 (the service host registers the same engine), Phase 7]

# Tech tracking
tech-stack:
  added: [Synapic.Core project]
  patterns:
    - "A host seam, not a host reference: the UI's Session never enters Core; the host supplies the value through an interface the engine declares"
    - "The DI graph is a testable artifact: App.axaml.cs calls one registration method so a test can assert the wiring the app really uses instead of copying it"

key-files:
  modified:
    - src/Synapic.Main/App.axaml.cs
    - src/Synapic.Main/Synapic.Main.csproj
    - Synapic.Net.sln
    - tests/Synapic.Main.Tests/SidecarDeviceSelectionTests.cs
  created:
    - src/Synapic.Core/Synapic.Core.csproj
    - src/Synapic.Core/Services/SidecarLaunchOptions.cs
    - src/Synapic.Main/Services/SessionSidecarDeviceProvider.cs
    - src/Synapic.Main/Services/EngineServiceRegistration.cs
    - tests/Synapic.Main.Tests/SidecarDeviceProviderTests.cs

key-decisions:
  - "One launch-options record instead of a second constructor parameter: the service's public shape is `(HttpClient?, SidecarLaunchOptions?)`, so every existing test that constructs it compiles unchanged"
  - "The device provider is resolved lazily per read (DeviceSelection is a property), so Step 2 changing the device mid-session still changes which variant the next launch prefers"
  - "ProcessJob moved with the sidecar host: it is the kill-on-close lifetime wrapper for that child process, not a UI concern"

patterns-established:
  - "Engine services take host inputs through a Core-declared interface; the desktop's adapter is a two-line class that reads the session"

requirements-completed: [CORE-01, CORE-03]

# Metrics
duration: ~40min
completed: 2026-10-22
---

# Phase 4 Plan 01: Create Core and move the inference transport, sidecar host and log facade

**`Synapic.Core` exists as an Avalonia-free net10.0 library holding the sidecar host, the inference client and the Serilog facade; the sidecar's `Session` coupling is now an `ISidecarDeviceProvider` the desktop implements — build 0/0, targeted tests 63 passed**

## Performance

- **Duration:** ~40 min
- **Tasks:** 3
- **Files changed:** 4 modified, 5 created, 4 moved into Core

## Accomplishments

- **The library exists with the right dependency list.** [Synapic.Core.csproj](src/Synapic.Core/Synapic.Core.csproj) targets `net10.0`, references `Synapic.Shared` only, and carries no Avalonia package. It is in `Synapic.Net.sln`.
- **Four services moved, verbatim.** `InferenceSidecarService`, `InferenceApiClient`, `SynapicLog` and `ProcessJob` now live in `src/Synapic.Core/Services/`. The port-file protocol (`--port=0`, `SYNAPIC_PORT_FILE`, `%TEMP%\synapic_port_{pid}.txt`, liveness poll, stale sweep) was moved as-is.
- **The `Session` coupling is gone.** [SidecarLaunchOptions.cs](src/Synapic.Core/Services/SidecarLaunchOptions.cs) declares `ISidecarDeviceProvider` plus a `StaticSidecarDeviceProvider` for hosts and tests; [SessionSidecarDeviceProvider.cs](src/Synapic.Main/Services/SessionSidecarDeviceProvider.cs) is the desktop adapter over `Session.Engine.Device`. `DeviceSelection` normalizes through the existing whitelist, so unknown values still land on CPU and a missing provider still means CPU — the old default.
- **The desktop's wiring is one named method.** [EngineServiceRegistration.cs](src/Synapic.Main/Services/EngineServiceRegistration.cs) registers the provider, the launch options, the sidecar and the metadata writer; [App.axaml.cs](src/Synapic.Main/App.axaml.cs) calls it. `SidecarDeviceSelectionTests` asserts against that method rather than a hand-built copy of the registrations, so the test cannot pass while the app is wired differently.
- **The service host can bundle its own executable.** `SidecarLaunchOptions.ExecutablePathOverride` lets a host point at a packaged sidecar; when the file is absent, discovery falls back to the existing locations in the existing order.

## Verification

- `dotnet build Synapic.Net.sln --nologo -v minimal` → **0 warnings, 0 errors**
- `dotnet test --filter "FullyQualifiedName~SidecarDeviceSelectionTests|FullyQualifiedName~SidecarDeviceProviderTests"` → **63 passed, 0 failed**
- `grep -rn "Session\|Avalonia" src/Synapic.Core/Services` → no matches in the moved services; the only `Session` strings are gone from Core
- `dotnet list src/Synapic.Core/Synapic.Core.csproj reference` → `..\Synapic.Shared\Synapic.Shared.csproj` only

## Decisions Made

- `SidecarLaunchOptions` is a record of host inputs, not a second constructor argument set: the constructor stays `(HttpClient?, SidecarLaunchOptions?)` so every existing construction site and test compiles untouched.
- `DeviceSelection` stays a property (not a captured value): the device is read when a launch decides its variant, so a mid-session change still takes effect.
- `ProcessJob` moved with the sidecar because it exists only to tie that child process's lifetime to the host's.
