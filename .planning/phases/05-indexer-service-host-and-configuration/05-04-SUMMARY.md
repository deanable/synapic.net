---
phase: 05-indexer-service-host-and-configuration
plan: 04
subsystem: indexer
tags: [installer, sc.exe, icacls, event-log, DPAPI, acceptance, HOST-01, HOST-07, HOST-06]

# Dependency graph
requires:
  - phase: 05-indexer-service-host-and-configuration
    provides: 05-01 (config/DPAPI), 05-02 (host), 05-03 (sidecar supervisor)
provides:
  - ServiceCommandLine (modes + sc.exe token builders)
  - ServiceInstaller (idempotent install, bounded uninstall)
  - ProgramDataSetup (ProgramData tree + SID-based ACL grants)
  - PasswordCommand (no-echo DPAPI password save)
  - phase 5 gate record, acceptance transcript and idempotency transcript
affects: [Phase 8 (installer calls this same path), Phase 6 (--status joins the control surface)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "sc.exe and icacls take ArgumentList tokens, never a shell string; the builders are unit-tested without touching the SCM"
    - "ACLs by SID: *S-1-5-18 and *S-1-5-19 mean the same on every locale"

key-files:
  modified:
    - src/Synapic.Indexer/Program.cs
  created:
    - src/Synapic.Indexer/Install/ServiceCommandLine.cs
    - src/Synapic.Indexer/Install/ServiceInstaller.cs
    - src/Synapic.Indexer/Install/ProgramDataSetup.cs
    - src/Synapic.Indexer/Install/PasswordCommand.cs
    - tests/Synapic.Indexer.Tests/ServiceCommandLineTests.cs
    - tests/Synapic.Indexer.Tests/ProgramDataSetupTests.cs
    - .planning/phases/05-indexer-service-host-and-configuration/acceptance.ps1 (+ transcript)
    - .planning/phases/05-indexer-service-host-and-configuration/idempotency.ps1 (+ transcript)

key-decisions:
  - "--set-daminion-password runs before configuration validation: the password is what a Daminion config is missing, and --uninstall must work with a broken config"
  - "Recovery actions are refreshed on a re-install rather than refusing with 'already exists' — the useful part of a second run is the repair"
  - "--status is not recognized yet: it belongs to the job queue (phase 6), and a flag that prints 'not implemented' is worse than the usage message"

patterns-established:
  - "The installer is a thin layer over testable command builders; phase 8 calls the same code instead of re-deriving sc.exe sequences"

requirements-completed: [HOST-01, HOST-07, HOST-06, HOST-02]

# Metrics
duration: ~60min
completed: 2026-10-22
---

# Phase 5 Plan 04: Control surface and the phase gate

**The service installs, starts, reports ready, stops in 4.87 s and removes itself cleanly through sc.exe — all seven HOST requirements pass on recorded evidence**

## Performance

- **Duration:** ~60 min (including three elevated acceptance runs)
- **Tasks:** 3
- **Files changed:** 1 modified, 4 created, 2 test files, 2 acceptance scripts + transcripts

## Accomplishments

- **The CLI cannot silently become a service.** [ServiceCommandLine.cs](src/Synapic.Indexer/Install/ServiceCommandLine.cs) parses `--install`, `--uninstall`, `--console`, `--set-daminion-password` and `--help`; two modes, an unknown flag, or a typo prints usage and exits **64**.
- **Install is idempotent and complete.** [ServiceInstaller.cs](src/Synapic.Indexer/Install/ServiceInstaller.cs) queries first, creates only when absent, always refreshes the three restart actions, creates the `SynapicIndexer` Event Log source, and runs the ProgramData setup. A second `--install` exits 0 and logs "already installed; refreshing its recovery settings" (transcript).
- **Uninstall is bounded and non-destructive.** It stops the service (ignoring 1062), waits up to 10 s for `STOPPED`, deletes the service and keeps logs and data.
- **ProgramData is created with narrow, locale-proof ACLs.** [ProgramDataSetup.cs](src/Synapic.Indexer/Install/ProgramDataSetup.cs) grants modify to `*S-1-5-18` and `*S-1-5-19` with `(OI)(CI)` through `icacls` `ArgumentList`s; nothing is granted to Everyone or Users.
- **The password path never leaks.** [PasswordCommand.cs](src/Synapic.Indexer/Install/PasswordCommand.cs) reads without echo, protects with LocalMachine DPAPI, writes only that member, and prints no secret — a test asserts the plaintext appears neither in the file nor in the output.

## Verification — HOST-01..07

Evidence lives in [acceptance-transcript.txt](.planning/phases/05-indexer-service-host-and-configuration/acceptance-transcript.txt) and [idempotency-transcript.txt](.planning/phases/05-indexer-service-host-and-configuration/idempotency-transcript.txt).

| Req | Criterion | Result | Evidence (elevated, real SCM) |
|---|---|---|---|
| HOST-01 | Installs and starts via `sc.exe` without a desktop session | ✓ | `--install` exit **0**; `sc query` exit 0 (STOPPED, WIN32_EXIT_CODE 1077 = never started); `sc start` exit **0**, PID 48780, `Get-Service` **Running**; service log: `Windows service mode` → `Indexer service started`; `--uninstall` exit 0; final `sc query` exit **1060** (not installed) |
| HOST-02 | Registry read, JSON overrides, precedence proven | ✓ | `IndexerConfigStoreTests`: registry `service.maxConcurrentItems=8` + JSON `16` → 16, while registry-only `service.logLevel` survives; sources list both; a live service run logged `Configuration read from file:C:\ProgramData\Synapic\indexer-config.json` |
| HOST-03 | Invalid config aborts startup naming the key | ✓ | Live run with `intervalMinutes: 0` and `device: "gpu"` → exit **2**, stderr `schedule.intervalMinutes: must be between 1 and 1440 minutes; was 0` and `engine.device: must be cpu, cuda or mps; was 'gpu'`; log `Startup aborted: 2 configuration error(s); nothing was started.` |
| HOST-04 | Sidecar launched, `/health` ready before "ready" is reported | ✓ | Service run: `Sidecar launched … pid 42012` → `Port file read` → `/health ready after 1 poll(s)` → `Sidecar ready on port 50709` → `Indexer ready` (**97.9 s** from start, inside the budget); console run earlier the same with the user sidecar root |
| HOST-05 | File log at `%ProgramData%\Synapic\Logs` and Event Log source `SynapicIndexer` | ✓ | `indexer-20261022.log` carries the whole run (no `synapic.log`); Application log entries from source `SynapicIndexer`: Information `Service started/stopped successfully`, Warning `Graceful shutdown failed …; killing process tree`, Error `Sidecar startup failed` — the Warning+ filter is visible in what did and did not appear |
| HOST-06 | `sc stop` finishes within 10 s with no orphaned sidecar | ✓ | Ready sidecar: `sc stop` → **4.87 s** to `Stopped`. Stop during sidecar startup: **5.46 s** and **5.15 s**. Each run's orphan check: `no synapic-inference processes` |
| HOST-07 | Install/uninstall/console/password control surface, idempotent | ✓ | Second `--install` exit **0** + log `already installed; refreshing its recovery settings`; `--uninstall` exit 0; `--set-daminion-password` covered by tests (ciphertext stored, plaintext absent); unknown flag → exit 64 (unit test); `--console` ran a real sidecar to ready |

Suite at the gate: Shared **6**, Core **144**, Integration **5**, Indexer **72**, Main **598 passed / 3 skipped** → **828 cases**, 0 failures. Release build **0 warnings / 0 errors**.

## Issues Encountered

- **The first stop measurement read 30.24 s and was wrong.** My script polled `sc query` and never matched the UTF-16 output, so it hit its own deadline; the service log and the Event Log show the service itself stopped 2.3 s after the signal. The acceptance was re-run polling `Get-Service`, which measured 4.87 s. The wrong number is recorded here rather than quietly replaced.
- **Readiness as a LocalSystem service took 97.9 s**, dominated by PyInstaller's cold extraction of the 227 MB sidecar into the service profile. It is inside the 120 s readiness budget, but the margin is thin on a slower disk — worth watching in the unattended acceptance (phase 8).
- **One Main-suite flake**: `DedupFingerprintRestoreTests.The_app_container_hands_the_store_to_the_dedup_searches` failed once in a full serial headless run, passed in isolation and on a complete re-run (598/598). Recorded as flaky, not as a pass by assumption.

## Decisions Made

- Control modes run before validation (password/uninstall must work on a broken config); the host path validates first.
- A failed `icacls` is reported as `Could not secure … Run --install from an elevated prompt` with exit 1, not as an unhandled exception inside a service binary.
- A second install refreshes recovery actions instead of failing; phase 8 can run `--install` unconditionally.
- `--status` waits for phase 6, when there is a queue to report.
