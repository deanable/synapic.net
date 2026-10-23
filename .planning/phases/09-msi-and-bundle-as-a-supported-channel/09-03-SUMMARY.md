# 09-03 SUMMARY — One command, and a package that is checked against the folder it came from

**Status:** Complete
**Requirements:** MSI-01

## What one command now does

`build/package-windows-msi.ps1` publishes the app **and** the indexer into `artifacts/win-x64`, then:

1. `check-installer-identities.py` — UpgradeCodes match `build/installer-identities.txt`, no pinned ProductCode.
2. `check-installer-payloads.py` — the `.iss` and the `.wxs` describe the same payload (plan 09-01).
3. `stage-dotnet-runtime.ps1` — the same staged runtime installer the Inno path bundles.
4. `wix build … -arch x64` — the MSI.
5. **Read-back verification** of the compiled package.
6. `wix build …` — the Burn bundle.

Steps 1-2 ran green in both builds this phase; the run's log is
[msi-build.log](../../../artifacts/phase-9/msi-build.log).

## The read-back, and why it exists

The payload checks prove the *folder* is complete. They say nothing about what the package's `<Files
Include="…\**">` actually selected: a mis-scoped `Include` or an `Exclude` that matches too much produces
a package that builds, installs cleanly and is missing the indexer — which is exactly the v1.1 defect the
Visual Studio project had with the 226 MB sidecar.

So `Get-MsiFileNames` opens the finished MSI through the Windows Installer COM API
(`OpenDatabase` → `SELECT FileName FROM File`), strips the `shortname|longname` form, and compares:

| Check | Failure it catches |
|-------|--------------------|
| `Synapic.exe`, `Synapic.Indexer.exe`, `Synapic.Core.dll`, `synapic-inference.exe` are each present, named one by one | A package that installs a UI with no indexer or no inference engine |
| No `*.pdb`, no `windowsdesktop-runtime-win-x64.exe`, no `Synapic-Setup-*.exe` | An exclusion that stopped matching; a 300 MB copy of the other installer in Program Files |
| The file set **equals** the payload folder minus the exclusions (whole-folder comparison, not just the required names) | A glob scoped one directory too shallow — a package missing 20 of 87 files still contains the four required ones |

Two PowerShell pitfalls worth recording, both hit while writing it: `InvokeMember` returns `$null` from
methods that "return nothing", and a bare `$null` statement is emitted to the pipeline — it arrived
downstream as a `File` row with an empty name. Every such call is `[void]`-cast, and the returned list is
typed `List[string]`.

## The build fails by name (ROADMAP criterion 1)

Two negative runs, each after moving one file out of `artifacts/win-x64` and restoring it afterwards:

| Removed | Result |
|---------|--------|
| `Synapic.Core.dll` | exit 1 — *"…\Synapic.Core.dll is missing - that directory is not a complete Synapic publish (app + background indexer). Run build/build-server.ps1 first, or let this script publish for you."* |
| `synapic-inference.exe` | exit 1 — *"…\synapic-inference.exe is missing - the installer would ship without the inference sidecar. Run build/build-sidecar.ps1 -Rid win-x64, or pass -SkipSidecarCheck if you are only testing the installer itself."* |

Both fail before anything is compiled, and the payload was restored to its published state
(`Synapic.Core.dll` 314 880 bytes, `synapic-inference.exe` 227 438 621 bytes).

## Results

```
Verified: 87 files in the MSI match C:\...\artifacts\win-x64 (service files present, exclusions respected).
artifacts/msi/Synapic-win-x64.msi                 234.5 MB
artifacts/msi/Synapic-Setup-win-x64-msi.exe      292.8 MB   (bundle, chained runtime + MSI)
exit=0
```

`install-acceptance.txt` independently confirms the installed result: `installed files: 87`,
`stray installer exe in Program Files: 0`, `pdbs in Program Files: 0`, and
`Synapic.dll: 1399808 bytes …, installed hash == published hash: True`.

A second build (`artifacts/msi-b`, log `msi-build-b.log`) was produced for the upgrade run. WiX generates
a fresh ProductCode each time, which is what made a real major upgrade testable without touching a source
identity ([upgrade-acceptance.txt](../../../artifacts/phase-9/upgrade-acceptance.txt) records the old and
new codes).

## Notes

- Output paths are RID-qualified (`Synapic-win-x64.msi`), so a future arm64 package cannot overwrite the
  x64 one on a release page.
- The `.wixpdb` files land beside the artefacts; they are debug symbols, not deployment inputs, and neither
  installer carries them.
- `wix msi validate` is advisory and not part of the script (it warns on the same-version upgrade by
  design, and a warning on every build trains people to ignore the checks that matter). Run by hand on the
  finished package it reports two warnings and no errors, which is what `docs/packaging.md` states:
  `ICE60` (one versioned DLL without a language) and `ICE61` (`AllowSameVersionUpgrades`), exit 0. Dropping
  `-arch x64` would add `ICE80` on every component, which is why the flag is explained rather than assumed.
