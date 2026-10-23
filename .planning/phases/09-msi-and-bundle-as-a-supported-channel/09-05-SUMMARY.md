# 09-05 SUMMARY — The MSI is documented as a channel, not an experiment

**Status:** Complete
**Requirements:** MSI-05

## docs/packaging.md

Rewritten around what the MSI actually does now, and what a future change must keep:

- **The payload is globbed, not listed**, with the two historical defects it closed: the hand-maintained
  list that never included the 226 MB sidecar, and the missing `Synapic-Setup-*.exe` exclusion (plan 09-01).
- **The exclusion list and the File-table read-back** are stated as rules, with the failure each prevents
  (plan 09-03), so the next person adding a dependency knows which check speaks for it.
- **Service parity with Inno** — the custom actions, `Return="check"` / `Return="ignore"`, and the fact that
  both channels run the same `--install` / `--uninstall`.
- **The two identities** (`UpgradeCode` fixed, `ProductCode` generated) and the guard that enforces both
  directions, in one place beside the comment in `Synapic.Identities.wxi`.
- The `-arch x64` explanation (and the `ICE80` that appears without it), the `help` subfolder, and the
  .NET 10 split between "the bundle installs it" and "the MSI refuses without it".

Added to *Limitations of the MSI path*: registration rollback was not induced, and the bundle's own install
was not run (the machine already had .NET 10, so the chained runtime would be skipped). Both are stated in
[09-04-SUMMARY.md](09-04-SUMMARY.md) and neither is papered over in the docs.

Removed rather than reworded, because they were no longer true:

| Removed claim | Why |
|---------------|-----|
| A `smoke.log` ships in the payload | Deleted from `artifacts/win-x64`; payload hygiene belongs to the payload source (D-12-06) |
| "the release build of it has never succeeded" | Narrowed, not dropped: the claim was about CI, and local builds finishing with exit 0 does not change it. It now reads "The release workflow's MSI leg has not run on a runner" — still true, `continue-on-error` is still in `release.yml` — under *Limitations* |
| The MSI does not register the service | It does (D-12-04) |

## docs/indexer-operations.md

- The MSI paragraph now reads like the Inno one: same files, same service, the silent command, and the
  bundle's role. The instruction to run `Synapic.Indexer.exe --install` by hand after an MSI is **gone** —
  following it would now run a second, redundant registration.
- `## Limitations in v1.1` lost "The MSI does not register the service" and gained the one limitation that
  is still real: installing a perMachine package needs elevation, and a non-elevated `msiexec` fails with
  1603 rather than half-installing (observed — `artifacts/phase-9/msi-non-elevated.log`).

## README.md

Checked, not changed: it describes the architecture and the Inno artefact, and makes no MSI or service claim
that this phase contradicts.

## The gate this phase moved

`tests/Synapic.Indexer.Tests/ConfigDocumentationTests.cs` had a test asserting the guide *says* the MSI does
not register the service. Deleting that sentence without touching the test failed the suite — the gate
working as designed. It now asserts the opposite claim plus `--uninstall` and the kept-data sentence
(`The_guide_says_the_msi_registers_the_service_and_that_uninstall_keeps_data`).
