# 12-01 SUMMARY — UI acceptance executed against the installed build

**Status:** Complete (UAT-01 ✓, UAT-02 ✓; UAT-03 — the ship — is the remaining work of this phase)
**Requirements:** UAT-01 ✓, UAT-02 ✓, UAT-03 pending

## What this plan did

12-01-PLAN.md set up the v1.2 UI acceptance criteria and began UAT verification. Execution:

| Step | What ran | Result |
|------|----------|--------|
| MSI dry run | `build/package-windows-msi.ps1` from the current tree | Both artifacts built (MSI 234.5 MB with 87 payload files verified against the publish folder; bundle 292.8 MB), identities and payload checks green |
| release.yml guard | The MSI leg's `continue-on-error: true` removed | The guard's own note said to drop it once a dry run produced both files; it did |
| Silent install | `msiexec /i Synapic-win-x64.msi /qn /norestart` | First attempt unelevated → 1603/Error 1925 (insufficient privileges, logged); retrun through a UAC-approved elevated process → **exit 0** |
| Service parity | `sc.exe query/qc/qfailure SynapicIndexer` | RUNNING, `NT AUTHORITY\LocalService`, AUTO_START, restart-on-failure (2 × RESTART/60 s, reset 86400 s) |
| Shortcut | `Synapic.lnk` target | `C:\Program Files\Synapic\Synapic.exe`, last write 2026-10-23T23:59:34 — the stale-shortcut root cause cannot recur from this channel |
| UAT sweep | `artifacts/phase-12/uat-sweep.ps1` (UIA over the installed window, launched via the shortcut) | **All seven 03-UAT.md scenarios passed with recorded observations** — transcript in [12-ACCEPTANCE.md](12-ACCEPTANCE.md), results written back to [03-UAT.md](../03-collapse-navigation-and-retire-settings-dialogs/03-UAT.md) |

## What the observations established

- **UAT-01**: all seven scenarios executed (not inferred) — dashboard/Settings cold start via the shortcut with five sidebar routes and no retired chrome; each operation route opens one window with its run action and Output region, parameters editable on the Settings view's per-mode tabs, no dialog at any point; a real scan showed live progress (`0/40 hashed`) with Stop enabled and an idle return after stopping; the empty-source scan rendered the exact guidance sentence; F1 opened the compiled help (`hh.exe`, 'Synapic Help') from all four routes; the theme change survived a restart; the build reports itself as `Synapic 1.0.0.0` / product version `1.0.0+dd86760fc40ff4ac79a35beb373b66e03cd390c0` (this tree's commit).
- **UAT-02**: the original complaint ("no dashboard on launch — the old UI") is closed on the record: the installed application, launched from the Start-menu shortcut, opens on the dashboard/Settings view, and the 2026-10-07 stale MSI that caused it is superseded by the channel that now builds from the same payload as the app (Phase 9's D-12-01).
- The 03-UAT gap record is marked closed with the closure evidence; the deferred-items pointer (phase 03 `03-UAT.md` in STATE.md) is resolved.

## Notes for the record

- Two wording drifts between the 2026-10-09 scenarios and the shipped UI are recorded where they were
  observed (scenario 2: parameters live on the Settings view's per-mode tabs; scenario 6: the
  inference-server controls are the Connection/Inference tabs). The scenario intents — no dialogs,
  everything editable in place — held; the observations quote what the screen showed.
- The sweep leaves the machine as it found it: temp fixtures removed, theme back to System, only
  sidecars it started are stopped (the service's sidecar is left alone).

## Verification

- `artifacts/phase-12/uat-sweep.txt` → `RESULT: PASS - all seven scenarios executed against the
  installed build with the observations above`, exit 0.
- Install/service evidence quoted in [12-ACCEPTANCE.md](12-ACCEPTANCE.md).
