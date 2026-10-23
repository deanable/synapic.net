# 08-01 SUMMARY — Payload, installer and the service lifecycle

**Status:** Complete
**Requirements:** OPS-01, HOST-07

## What was built

| File | Role |
|------|------|
| [package-windows.ps1](../../../build/package-windows.ps1) | Publishes `src/Synapic.Indexer` into `artifacts/<rid>` **after** the app publish, then fails by name if `Synapic.Indexer.exe`, `Synapic.Core.dll` or `synapic-inference.exe` is missing (D-11-28/29) |
| [package-windows-msi.ps1](../../../build/package-windows-msi.ps1) | Same publish into the same folder; the MSI ships the payload without registering the service (D-11-31) |
| [installer-windows.iss](../../../build/installer-windows.iss) | `--install` runs from `CurStepChanged(ssPostInstall)`; `--uninstall` from `[UninstallRun]`; the exit code is appended to the finish page (T-08-03) |
| [ServiceCommandLine.cs](../../../src/Synapic.Indexer/Install/ServiceCommandLine.cs) | `sc create` now passes `obj= "NT AUTHORITY\LocalService"` so the installed account matches the documented one |
| [ServiceInstallerTests.cs](../../../tests/Synapic.Indexer.Tests/ServiceInstallerTests.cs), [ServiceCommandLineTests.cs](../../../tests/Synapic.Indexer.Tests/ServiceCommandLineTests.cs) | Fake `IScRunner` asserts create → failure → start order, idempotent re-install, 1056 tolerance, bounded uninstall, and the exact token list |
| [docs/packaging.md](../../../docs/packaging.md) | Payload layout, publish order, installer entry point and the MSI's non-registration stated in one place |

`--install` itself was already the single implementation of create → recovery → start (05-04); 08-01 put it in
the shipping path rather than re-writing it in the `.iss`.

### Two changes the acceptance run forced

1. **The service was installed as LocalSystem.** `sc create` without `obj=` defaults to SYSTEM, so install #1
   produced `SERVICE_START_NAME : LocalSystem` while `docs/windows-indexer.md` §7.1 and the operations doc both
   promise `NT AUTHORITY\LocalService`. Fixed in `ServiceCommandLine` (one token pair) and pinned by
   `Create_arguments_are_separate_tokens_with_the_service_identity`; install #2 verified
   `SERVICE_START_NAME : NT AUTHORITY\LocalService`.
2. **The `[Run]` entry could not report a failed start.** `[Run]` launches a program but discards its exit code,
   so a service that failed to register or start was invisible on the finish page. Moved to
   `CurStepChanged(ssPostInstall)` with `Exec(...)`: the code is logged
   (`Synapic.Indexer.exe --install: started=1, exit code=…`) and, when non-zero, the finish page names the fix.

## Evidence — payload

```
package-windows.ps1 → Successful compile
Installer created: C:\Users\Dean\source\repos\Synapic.NET\artifacts\win-x64\Synapic-Setup-win-x64.exe
```

Installed payload (`C:\Program Files\Synapic`):

```
Synapic.exe                     347648
Synapic.Indexer.exe             162304
Synapic.Core.dll                314880
Synapic.Shared.dll              122880
Synapic.Indexer.runtimeconfig.json    447
synapic-inference.exe        227438621      ← one copy, shared by app and service
```

Smoke check of the app from the same payload folder:

```
Synapic.exe → SMOKE_ALIVE=True, WINDOW_TITLE=Synapic - AI Image Tagging
```

## Evidence — service registration (install #2, silent installer)

```
Installer exit code: 0
sc query  → STATE : 4 RUNNING
sc qc     → START_TYPE : 2 AUTO_START
            BINARY_PATH_NAME : C:\Program Files\Synapic\Synapic.Indexer.exe
            SERVICE_START_NAME : NT AUTHORITY\LocalService
sc qfailure → RESET_PERIOD 86400; RESTART 60000 ×3
Event Log source exists: True
icacls %ProgramData%\Synapic →
  NT AUTHORITY\LOCAL SERVICE:(OI)(CI)(M)
  NT AUTHORITY\SYSTEM:(OI)(CI)(M)
```

Log after the install: `Created service SynapicIndexer (auto start)`, `Recovery actions set: restart 3 times at
60 s intervals`, `Granted modify … *S-1-5-18` / `*S-1-5-19`, `Started service SynapicIndexer`.

## Evidence — tests

```
dotnet test tests/Synapic.Indexer.Tests -c Release --filter
  "FullyQualifiedName~ServiceCommandLineTests|FullyQualifiedName~ServiceInstallerTests|FullyQualifiedName~ConfigDocumentationTests"
Passed!  - Failed: 0, Passed: 29, Skipped: 0, Total: 29
```

## Stated limits

- **The MSI path was not compiled or installed in this pass.** Both packaging scripts carry the same publish and
  the same payload, and `package-windows-msi.ps1` was edited only to match; the shipping channel is Inno. WiX is
  not installed on this machine.
- **A fresh install starts a service that has no configuration yet.** `--install` registers, secures and starts;
  the process then exits with the configuration error (`schedule.watchPaths`, `engine.modelId`), and the SCM
  reports 1053. That is now stated on the finish page, in `docs/indexer-operations.md` and here rather than
  papered over. Configure first (or immediately after) and the install starts it — install #2 proved that with a
  valid `indexer-config.json` already in place.
