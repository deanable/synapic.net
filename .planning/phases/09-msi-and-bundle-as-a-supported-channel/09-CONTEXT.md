---
phase: 09-msi-and-bundle-as-a-supported-channel
created: 2026-10-23
milestone: v1.2
requirements: [MSI-01, MSI-02, MSI-03, MSI-04, MSI-05]
---

# Phase 9 Context — MSI and bundle as a supported channel

## Why this phase exists

v1.1 shipped the Inno installer and stated the MSI as "not exercised". Two facts changed the picture on
2026-10-23:

- WiX 7 is installed as a global dotnet tool (`~/.dotnet/tools/wix`), and `build/wix/Synapic.Installer.wixproj`
  restores against it. The v1.1 gate's "WiX absent" no longer holds.
- `artifacts/msi` holds an MSI from Oct 14 and a bundle from Oct 4, both older than the indexer work. The
  Oct-4 package is the stale installer `03-UAT.md` blames for *"No dashboard on launch."* The UAT failure and
  the unverified MSI are the same defect seen from two directions.

So the phase is not "build an MSI": it is making the second channel honest, and proving the installed app is
the app that was just published.

## Decisions

- **D-12-04 — the MSI registers the service itself.** D-11-31 left registration to one documented elevated
  command because a second implementation of create → recovery → start would drift. The resolution keeps the
  one implementation and changes only the invoker: WiX custom actions run
  `Synapic.Indexer.exe --install` / `--uninstall`, the same code Inno and an operator run. `Return="check"` on
  install so a registration failure rolls back rather than shipping a service-less install; `Return="ignore"`
  on uninstall so removal never fails on an already-gone service (D-11-32).
- **D-12-05 — one payload definition, one guard.** Both sources glob `artifacts/win-x64`; the exclusion lists
  drifted (see below), so `build/check-installer-payloads.py` compares them at build time in both packaging
  scripts, and the MSI build reads its own `File` table back and compares it with the folder.
- **D-12-06 — the payload source, not the installer, owns payload hygiene.** A stray `smoke.log` from a
  September sidecar smoke run sat in `artifacts/win-x64` and both installers carried it. It was deleted from
  the payload folder and the documented limitation removed, rather than excluded per installer.

## The drift this phase found

`package-windows.ps1` (Inno) writes `Synapic-Setup-win-x64.exe` — 302 MB — into `artifacts/win-x64`, and the
`.iss` excludes it. `Synapic.Msi.wxs` excluded only `*.pdb` and the runtime installer, because it had never
been built against a payload that contained an Inno output. The first MSI built after a release would have
installed a copy of the other installer into Program Files. Fixed by the exclusion plus the guard.

## Environment

| Item | Value |
|------|-------|
| WiX | 7.0.0 global dotnet tool; `build/wix/Synapic.Installer.wixproj` targets `WixToolset.Sdk/7.0.0` |
| Inno | ISCC at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` (per-user, no elevation) |
| Payload | `artifacts/win-x64` — 94 files after removing `smoke.log`, including the 227 MB sidecar |
| Pre-state | no Synapic in Add/Remove Programs, `sc query SynapicIndexer` → 1060, `C:\Program Files\Synapic` absent |
| ProgramData | `%ProgramData%\Synapic` exists from the v1.1 acceptance, with a config and a queue |

## Scope fence

No new installer technology, no signing (no certificate), no change to what the app does. `UNSIGNED` packages
are the expected state and SmartScreen warnings are factual, not a bug.
