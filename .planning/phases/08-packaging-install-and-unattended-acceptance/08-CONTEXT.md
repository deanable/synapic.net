# Phase 8: Packaging, install and unattended acceptance - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning
**Source:** `docs/windows-indexer.md` §7, §9; `docs/packaging.md`

<domain>
## Phase Boundary

Ship it and prove it runs with nobody watching. Add the indexer to the Windows payload, wire service
registration into the Inno installer, document operations, and execute the end-to-end acceptance: install, drop
an image, get tags, stop cleanly, recover an interrupted job, compare with the desktop, uninstall. No new
tagging/detection behavior — this phase packages and verifies phases 4–7.

</domain>

<decisions>
## Implementation Decisions

### Payload and install channel
- **D-11-28:** `Synapic.Indexer.exe` is published into the **same** `artifacts/<rid>/` payload folder as
  `Synapic.Main.exe`, not a subfolder: the service finds `synapic-inference.exe` beside itself (phase 5, D-11-13)
  and duplicating a 226 MB sidecar into a subfolder is not acceptable. Both installers already harvest the whole
  payload directory (`build/installer-windows.iss` copies `{#ArtifactsDir}\*`; `build/wix/Synapic.Msi.wxs`
  `ApplicationFiles` includes `$(var.PayloadDir)\**`), so no per-file list changes are needed.
- **D-11-29:** Publish order is Main, then Indexer, then verify `Synapic.Main.exe` still launches (shared
  Microsoft.Extensions/Serilog assemblies must not be downgraded). The packaging script fails if
  `Synapic.Indexer.exe` or `Synapic.Core.dll` is missing from the payload.
- **D-11-30:** Inno Setup is the **primary** channel and installs the service by running
  `{app}\Synapic.Indexer.exe --install` (elevated, wait-until-terminated) after files are copied, and
  `--uninstall` from `[UninstallRun]` before files are removed. `--install` is extended in this phase to
  **start** the service after creating it (the spec's create → failure → start sequence), so the installer has one
  call to make and the phases cannot drift.
- **D-11-31:** The MSI ships the same payload but does **not** register the service in v1.1: WiX `ServiceInstall`
  cannot express the recovery actions, account and Event Log rules that `--install` owns, and a second
  implementation is how the two channels start disagreeing. The MSI's finish page/docs state the one-line
  post-install command. Accepted deviation, recorded in the docs and the gate; revisiting is a v2 item.
- **D-11-32:** Uninstall stops and deletes the service but **keeps** `%ProgramData%\Synapic` (database, logs,
  config). Data removal is a manual, documented step — an uninstall must not destroy an index or a log trail.
- **D-11-33:** Service account stays `NT AUTHORITY\LocalService` by default. UNC/domain-catalog scenarios use a
  documented `sc.exe config SynapicIndexer obj= "DOMAIN\svc-synapic" password= "..."` step with a note that the
  account needs read/write on the watch paths and modify on `%ProgramData%\Synapic`. No account UI in the
  installer for v1.1.

### Documentation
- **D-11-34:** One operator document, `docs/indexer-operations.md`: install/upgrade/uninstall, the full
  configuration reference (both sources, defaults, validation rules), the password command, log locations, the
  service account/UNC note, `--status`, and a troubleshooting table mirroring the spec's five failure modes.
  `docs/windows-indexer.md` stays the component specification (what/why); this is the how-to.
- Update `docs/packaging.md`'s payload layout and `README.md`/`docs/README.md` links. Do not touch the compiled
  help corpus — it documents the desktop wizard, not the service.

### Acceptance
- **D-11-35:** Acceptance runs against a real Windows machine with a real sidecar and model, because that is what
  the spec's §9 asks for. Use a small fixture set (3–5 images) and record the model id, device, and the exact
  tags so a later reader can reproduce. If a Daminion server is unavailable, the local-file acceptance must still
  pass and the Daminion row is recorded as blocked, not as ✓.
- **D-11-36:** "Reboot recovery" is simulated by stopping the service with `sc.exe stop` (or killing the worker)
  mid-batch and starting it again — the queue's InFlight → Pending rule is what is under test, not the OS boot.
  The record must say which method was used.
- **D-11-37:** The milestone gate is the phase 8 summary: OPS-01..05 plus a one-line verdict per v1.1 phase,
  each pointing at its own gate record. A green build never substitutes for the unattended run.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Specification
- `docs/windows-indexer.md` §7.1–7.2 — install commands, service account/UNC note
- `docs/windows-indexer.md` §8.1 — Event Log source and file retention
- `docs/windows-indexer.md` §9 — all five acceptance criteria

### Build
- `docs/packaging.md` — payload layout and per-RID steps
- `build/package-windows.ps1` — Inno packaging entry (ArtifactsDir, AppVersion, ISCC invocation)
- `build/installer-windows.iss` — `[Files]` payload copy, `[Run]`/`[UninstallRun]` sections, AppId identity
- `build/package-windows-msi.ps1`, `build/wix/Synapic.Msi.wxs`, `build/wix/Synapic.Installer.wixproj`,
  `build/wix/Synapic.Identities.wxi` — MSI payload
- `build/check-installer-appid.py`, `build/check-installer-identities.py` — identity gates that must keep passing
- `src/Synapic.Indexer/Install/ServiceInstaller.cs` — the one install implementation (phase 5)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `--install`/`--uninstall` already own service creation, recovery, Event Log source and ProgramData ACLs; the
  installer is a caller, not a reimplementation.
- Both installers harvest directories, so adding a binary to the payload needs no WiX component surgery.
- `--status` (phase 6) and the rolling log (phase 5) are the acceptance's observability tools.

### Established Patterns
- Installer identity is guarded by scripts (`check-installer-appid.py`, `check-installer-identities.py`); those
  checks must pass unchanged.
- Packaging is verified by scripts under `build/`, not by hand-editing artifacts.
- The app is framework-dependent on Windows; the indexer must be published the same way (no second runtime).

### Integration Points
- The service reads `%ProgramData%\Synapic\indexer-config.json`; the acceptance writes it (JSON override) rather
  than HKLM, so no registry edits are required to test.
- The installer's `[Run]` entry runs after the payload and after the runtime prerequisite — the indexer must not
  be started before `synapic-inference.exe` is present.

</code_context>

<deferred>
## Deferred Ideas

- Native WiX service registration and MSI parity for recovery actions (D-11-31)
- Installer UI for service account selection / UNC credentials (D-11-33)
- Upgrade-in-place migration of the queue schema (user_version exists; no migrations needed in v1.1)
- Code signing of `Synapic.Indexer.exe` beyond whatever signing the existing installer flow applies
- A health dashboard in the desktop app (UI-01)

</deferred>

---

*Phase: 08-packaging-install-and-unattended-acceptance*
*Context gathered: 2026-10-22*
