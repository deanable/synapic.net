# 09-02 SUMMARY — The MSI registers the indexer itself

**Status:** Complete
**Requirements:** MSI-03

## The decision it implements

D-11-31 left service registration to one documented elevated command (`--install` after the MSI), on the
argument that a WiX reimplementation of create → recovery → start would drift from the Inno installer's.
D-12-04 keeps that argument and removes the gap: the custom actions invoke `Synapic.Indexer.exe`'s own
`--install` / `--uninstall`, so there is still exactly one implementation — only the caller changed.

## What changed (`build/wix/Synapic.Msi.wxs`)

```
<CustomAction Id="InstallIndexerService"   Directory="INSTALLFOLDER"
              ExeCommand="&quot;[INSTALLFOLDER]Synapic.Indexer.exe&quot; --install"
              Execute="deferred" Impersonate="no" Return="check" />
<CustomAction Id="UninstallIndexerService" Directory="INSTALLFOLDER"
              ExeCommand="&quot;[INSTALLFOLDER]Synapic.Indexer.exe&quot; --uninstall"
              Execute="deferred" Impersonate="no" Return="ignore" />

<InstallExecuteSequence>
  <Custom Action="InstallIndexerService"   After="InstallFiles"  Condition="NOT Installed" />
  <Custom Action="UninstallIndexerService" Before="RemoveFiles"  Condition='REMOVE~="ALL"' />
</InstallExecuteSequence>
```

Each choice, and what it prevents:

| Choice | Why |
|--------|-----|
| `Directory` + `ExeCommand` (type 34) | No `FileRef` for an element the payload glob generates; Windows runs the line with the install folder as its working directory |
| `Execute="deferred"`, `Impersonate="no"` | `sc.exe`, `icacls` and the Event Log source need elevation; the package is perMachine, so this point is already elevated |
| `Return="check"` on install | A failed registration rolls the install back instead of leaving an app that looks installed with no service — the failure mode the v1.1 acceptance found in the Inno path |
| `Return="ignore"` on uninstall | Removing the product must not fail because the service is already gone (D-11-32) |
| `After="InstallFiles"` | The executable has to exist before it is run |
| `Before="RemoveFiles"` | The executable has to still exist when the uninstall action runs |
| `Condition="NOT Installed"` | Runs on a fresh install and on the new product of a major upgrade; a repair does not re-run it |
| `REMOVE~="ALL"` | Uninstall only — a version-to-version upgrade removes the old product through its own `RemoveFiles`, but the surviving upgraded product re-registers via `NOT Installed`, so one service record is carried forward (confirmed: `service instances: 1`) |

No `InstallUISequence` entry was added: registration does not need to run in the UI sequence, and the
execute sequence covers silent and UI installs alike.

## Verification

From [install-acceptance.txt](../../../artifacts/phase-9/install-acceptance.txt), a silent elevated
`msiexec /i … /qn` (exit 0) of the rebuilt package:

```
SERVICE_START_NAME : NT AUTHORITY\LocalService
START_TYPE         : 2 AUTO_START
FAILURE_ACTIONS    : RESTART --Delay= 60000 milliseconds.  (×3, reset period 86400)
stdout             : account: NT AUTHORITY\LocalService  state: Running  startmode: Auto
```

`install-acceptance.txt` also confirms the service binary is the *installed* one:
`BINARY_PATH_NAME : C:\Program Files\Synapic\Synapic.Indexer.exe`.

The `ProgramData` half the service depends on is not the package's job and was not changed: the MSI does
not create `%ProgramData%\Synapic`. `--install` does (via `ProgramDataSetup`), so the same command that
registers the service also establishes the folder and its ACLs. On this machine `%ProgramData%\Synapic`
already existed from the v1.1 acceptance — its queue's last-write, `05:57`, predates the `07:50` install —
so the run does not by itself prove the folder is created on a bare machine. That path belongs to
`ProgramDataSetup` (Phase 5) and has no test of its own; it was not re-tested here.

Rollback on a failed registration was **not** exercised — forcing `--install` to fail needs a
deliberately broken ACL or a missing exe, and either would have to be induced inside a live MSI
transaction. The `Return="check"` behaviour is therefore argued from the WiX semantics above, not
observed. This is stated as a limit in the phase summary too.

## Test changes

`tests/Synapic.Indexer.Tests/ConfigDocumentationTests.cs` — the documentation gate that asserted the
guide says the MSI *does not* register the service now asserts the opposite
(`The_guide_says_the_msi_registers_the_service_and_that_uninstall_keeps_data`), plus `--uninstall` and the
kept-data sentence. It was already failing against the updated guide, which is the gate doing its job.
