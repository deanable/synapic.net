# 09-04 SUMMARY — Silent install, dashboard, in-place reinstall, upgrade, uninstall

**Status:** Complete with three stated limits (see the end)
**Requirements:** MSI-02, MSI-03, MSI-04

## Machine and pre-state

| Item | Value |
|------|-------|
| OS | Windows 11 Pro, x64 (same machine as the Phase 8 acceptance) |
| .NET | .NET Desktop Runtime 10 present (so the bundle's runtime step would be a no-op here) |
| Pre-state | no Synapic in Add/Remove Programs, `sc query SynapicIndexer` → 1060, `C:\Program Files\Synapic` absent |
| ProgramData | `%ProgramData%\Synapic` present from v1.1, with configuration and a 45 056-byte queue (last write 05:57) |
| Package | `artifacts/msi/Synapic-win-x64.msi`, 234.5 MB; second build `artifacts/msi-b`, 07:52 |

Scripts: [acceptance-install.ps1](../../../artifacts/phase-9/acceptance-install.ps1),
[acceptance-upgrade.ps1](../../../artifacts/phase-9/acceptance-upgrade.ps1),
[check-dashboard.ps1](../../../artifacts/phase-9/check-dashboard.ps1).

## 1. Silent install — MSI-02, MSI-03 ✓

`msiexec /i … /qn` elevated, **exit 0**. Then, from
[install-acceptance.txt](../../../artifacts/phase-9/install-acceptance.txt):

| Check | Observed |
|-------|----------|
| Add/Remove Programs | 1 entry, `Synapic 1.00.00` |
| Service | `SERVICE_NAME: SynapicIndexer`, `WIN32_OWN_PROCESS`, **Running** |
| Account | `SERVICE_START_NAME : NT AUTHORITY\LocalService` |
| Start type | `2 AUTO_START` |
| Recovery | `RESTART --Delay= 60000 milliseconds.` ×3, reset period 86400 |
| Binary | `C:\Program Files\Synapic\Synapic.Indexer.exe` |
| Files | 87 installed; `Synapic.exe`, `Synapic.Indexer.exe`, `Synapic.Core.dll`, `synapic-inference.exe` present |
| Payload hygiene | `stray installer exe: 0`, `pdbs: 0` |
| Integrity | `Synapic.dll: 1399808 bytes, 2026-10-23T07:40:22Z`, `installed hash == published hash: True` |
| Data | `%ProgramData%\Synapic` and its queue present and unchanged |

**Dashboard** — [dashboard-check.txt](../../../artifacts/phase-9/dashboard-check.txt) launched
`C:\Program Files\Synapic\Synapic.exe` (the *installed* one, not a publish folder), found the window
`'Synapic — AI Image Tagging'` class `MainWindow`, 39 named elements, the four panels
(`Settings`, `Tagging`, `Deduplication`, `Upscaling`), and **no retired step-chain chrome** — none of
`Step 1`, `Step 2`, `Start over`, `Back to` is rendered. `RESULT: PASS`. This is the assertion `03-UAT.md`
could not make about the stale Oct-4 package.

**Non-elevated install** — [msi-non-elevated.log](../../../artifacts/phase-9/msi-non-elevated.log):
`MainEngineThread is returning 1603`, and afterwards no service and no `Program Files\Synapic`. A perMachine
package that cannot elevate fails whole; it does not half-install.

## 2. In-place reinstall of the same package — MSI-04 (first half) ✓

Second `msiexec /i` of the **same** package, exit 0: 1 ARP entry, one service, 87 files, installed hash
equal, service still `Running` as LocalService with the same recovery actions. No side-by-side copy.

## 3. Major upgrade to a second build — MSI-04 ✓

`artifacts/msi-b` was built from the same source, so WiX generated a different ProductCode
(`{45C1DDAB-…}` over `{82941FF3-…}`) while the UpgradeCode stayed put. From
[upgrade-acceptance.txt](../../../artifacts/phase-9/upgrade-acceptance.txt):

```
msiexec exit code: 0
ARP entries after: 1
product code changed (upgrade, not repair): True
old product code removed: True
service account: NT AUTHORITY\LocalService   state: Running   startmode: Auto
service instances: 1
installed files: 87
installed hash == published hash: True
stray installer exe: 0
```

The old product was removed and the new one took its place: one ARP entry, one service record, files
matching the new build. `service instances: 1` is the interesting number — the old product's
`UninstallIndexerService` and the new product's `InstallIndexerService` both ran inside one transaction
and left a single service.

## 4. Uninstall — MSI-04 ✓

`msiexec /x` **exit 0**, then `sc query SynapicIndexer` → **1060** (service gone), 0 ARP entries,
`C:\Program Files\Synapic` absent — while `%ProgramData%\Synapic`, its queue (45 056 bytes, last write
unchanged) and its log folder all survived. Recorded in
[uninstall-acceptance.txt](../../../artifacts/phase-9/uninstall-acceptance.txt), taken from this run's
`/x` step.

## Phase gate

- `dotnet build Synapic.Net.sln -c Release` → 0 warnings, 0 errors.
- `dotnet test Synapic.Net.sln -c Release -m:1 --filter "Category!=Live"` → **exit 0**: 981 passed,
  2 skipped, 0 failed (Shared 6, Main 598 + 1 skipped, Integration 5, Core 148, Indexer 224 + 1 skipped).
  Log: `artifacts/phase-9/full-suite.log`. No test was weakened or removed; one was *changed* because the
  claim it guarded changed (see 09-02/09-05).
- `build/check-installer-identities.py` and `build/check-installer-payloads.py` → both pass; three induced
  drifts each fail with the file or pattern named (09-01).

## Stated limits

1. **Registration rollback was not exercised.** `Return="check"` on `InstallIndexerService` should fail the
   install if `--install` fails; inducing that failure needs a deliberately broken ACL or a missing exe
   inside a live MSI transaction. Argued from WiX semantics, not observed.
2. **The bundle's own install was not run.** `Synapic-Setup-win-x64-msi.exe` compiled (292.8 MB) and is
   what `wix build` verified, but the machine already has .NET 10, so `netfx:DotNetCoreSearch` would skip
   the chained runtime and the run would reduce to the MSI install already verified here. All results above
   are the MSI's.
3. **No reboot, no reboot-suppressed install.** `/norestart` was used and no case triggered a reboot, so the
   3010/1641 handling in the bundle and the runtime chain is untested.
