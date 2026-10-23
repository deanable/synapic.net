# 09-01 SUMMARY — One payload definition, one guard

**Status:** Complete
**Requirements:** MSI-01, MSI-05

## What was wrong

`package-windows.ps1` (Inno) writes `Synapic-Setup-win-x64.exe` — 302 MB — into the very folder
`artifacts/win-x64` both installers glob. The `.iss` excludes it; `Synapic.Msi.wxs` did not, because
it had never been built against a payload that contained the other installer's output. A release-day
MSI would have installed a 302 MB copy of Inno's setup exe into `Program Files\Synapic`, and nothing
would have failed: the file exists, the glob matched it, the package installs.

Both absence-of-drift arguments lived in comments, which is why the drift happened.

## What changed

| File | Change |
|------|--------|
| `build/wix/Synapic.Msi.wxs` | `<Exclude Files="$(var.PayloadDir)\Synapic-Setup-*.exe" />`, with the comment stating the three exclusion classes and why each is there |
| `build/check-installer-payloads.py` | New guard: parses both sources and compares them |
| `build/package-windows.ps1` | Runs the guard before publishing/compiling |
| `build/package-windows-msi.ps1` | Runs the same guard before compiling |

## The guard

It reads `build/installer-windows.iss` and `build/wix/Synapic.Msi.wxs` as text, resolves the Inno
`#define`s the way ISCC would, and refuses to build unless all four agree:

1. the payload root folder (`{#ArtifactsDir}` vs `$(var.PayloadDir)`),
2. the exclusion patterns as sets — order and duplicate-free spelling do not matter, membership does,
3. neither list still contains an unresolved define,
4. each packaging script still names the three files it refuses to ship without
   (`Synapic.Indexer.exe`, `Synapic.Core.dll`, `synapic-inference.exe`).

Item 4 is there because the guard compares definitions, and a definition can agree perfectly while the
script that checks it has lost the check.

Failure names the file and the rule, e.g. an exclusion removed from one side prints the pattern that is
only in the other.

## Verification

- Guard passes on the changed tree: `Installer payloads agree (same folder, same exclusions, same service files checked)`.
- Drift was induced on purpose against temporary copies of the four inputs (the guard takes `--iss`,
  `--wxs`, `--inno-script`, `--msi-script`), so the repository was never in a half-edited state:

  | Mutation | Result |
  |----------|--------|
  | `Synapic-Setup-*.exe` removed from the `.wxs` | exit 1 — *the .iss excludes 'synapic-setup-*.exe' but the MSI source does not - the MSI would install that file* |
  | `Synapic-Setup-*.exe` removed from the `.iss` | exit 1 — *the MSI source excludes 'synapic-setup-*.exe' but the .iss does not - Inno would install that file* |
  | `synapic-inference.exe` renamed in `package-windows.ps1`'s check list | exit 1 — *package-windows.ps1 never names synapic-inference.exe; a payload missing it would package silently* |
  | unmodified copies | exit 0 |

- The build the guard gates is the one `artifacts/phase-9/msi-build.log` records: `Verified: 87 files in
  the MSI match …\artifacts\win-x64 (service files present, exclusions respected)`, with no
  `Synapic-Setup-*.exe` and no `*.pdb` in `Program Files` after install
  (`install-acceptance.txt`: `stray installer exe in Program Files: 0`, `pdbs in Program Files: 0`).

## Deliberately not done

No second implementation of "which files ship": the guard compares the two existing definitions rather
than generating either. `package-windows-msi.ps1`'s File-table read-back (plan 09-03) is the other half —
that one compares the compiled package with the folder, this one compares the two sources with each other.
