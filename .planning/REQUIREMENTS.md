# Requirements: Synapic.NET — v1.2 Release Integrity

**Defined:** 2026-10-23
**Core Value:** Correct, reviewable results written back — and claims about them that are actually proven.
**Scope:** close what is already claimed but unverified. No new product capability.

Three gaps motivated this milestone, each recorded honestly in the v1.1 archive rather than papered over:

1. The **MSI channel** was never built or exercised (WiX was absent), while `artifacts/msi` still holds
   Oct-4/Oct-14 packages that predate the indexer — one of which is the root cause recorded in `03-UAT.md`
   for "no dashboard on launch".
2. **Daminion** ran only against fake network seams. A real server is available, so the fault table can
   be proven against reality instead of a mock.
3. **v1.0 never shipped.** Phase 3 executed all five plans but its gate has been short of §10 criterion 6
   since 2026-10-09; criterion 8 was actually closed by the 2026-10-13 addendum and the roadmap text was
   never updated.

## v1.2 Requirements

### MSI as a supported channel (MSI)

- [x] **MSI-01**: The MSI and its Burn bundle build from one command against a freshly published `win-x64` payload, and the build fails by name if that payload is missing `Synapic.Indexer.exe`, `Synapic.Core.dll` or `synapic-inference.exe` (the Inno path's existing check, applied to this one)
- [x] **MSI-02**: A silent MSI install (`msiexec /i … /qn`) produces a working install whose launched app opens on the dashboard, with its shortcut resolving to the installed executable rather than an older publish
- [x] **MSI-03**: The installed service state matches the Inno path — registered as `NT AUTHORITY\LocalService` with auto-start and the restart-on-failure recovery actions — either performed by the MSI or by one documented elevated command that the acceptance runs and verifies
- [x] **MSI-04**: Upgrade over an existing install replaces it in place at the same product version (no second Programs entry, no side-by-side copy), and uninstall removes files and service while leaving `%ProgramData%\Synapic` and its queue intact
- [x] **MSI-05**: `docs/packaging.md` and the release documentation state the MSI's role, its exact commands, and its parity with (and differences from) the Inno channel; a check fails if the MSI payload drifts from the Inno payload

### Live Daminion verification (DAM)

- [x] **DAM-01**: A credential-gated run connects to a real Daminion server and reports the server, catalog scope and item counts it is working against, with no interactive prompt (credentials from `SYNAPIC_TEST_DAMINION_URL`/`_USERNAME`/`_PASSWORD` or the saved Windows credential)
- [x] **DAM-02**: In a real catalog scope, items are tagged and checked back in, and a read-back from the catalog shows the written keywords, category and description — with a re-run producing no duplicated keywords. The "selected fields are empty" selection is not available on the provided server (its untagged clause is ignored); the round trip snapshots real items instead and asserts the written values land exactly once while nothing already there is lost
- [x] **DAM-03**: A real outage is handled as the failure table specifies: with the server stopped the run pauses without dropping queued items, and when it returns the queue completes; the log names the pause and the resume. Proven with a real firewall block against a live catalog on 2026-10-23 (six rows paused, six completed, `pauses=1 resumes=1`)
- [x] **DAM-04**: The live run is repeatable and opt-in — an automated test gated on those variables plus a documented procedure and a recorded acceptance transcript; with no credentials it skips with a stated reason instead of passing silently

### Closing v1.0's gate (GATE)

- [ ] **GATE-01**: `docs/ui-design.md` §10 criterion 6 is restated as a finite, checkable claim — every empty-result and report branch the shipped UI can reach is inventoried and each has a named behavioral test — with the reason for the change recorded beside it
- [ ] **GATE-02**: Every row of that inventory names a test that exists, and a drift check fails when a row names a test that does not exist (the pattern `ConfigDocumentationTests` already uses for configuration keys)
- [ ] **GATE-03**: The branches still lacking a view-model-level behavioral test (Tag cancellation and fetch-failure presentation, plus any inventory row without one) gain one, and the §10 matrix records criterion 6 ✓ and criterion 8 ✓ with the 2026-10-13 remap visible to the gate
- [ ] **GATE-04**: The 03-05 gate re-runs clean — build 0/0, full solution suite green with no test weakened or removed, layout audit at four sizes × four routes — and ROADMAP and STATE record Phase 3 complete

### Shipping v1.0 (UAT)

- [ ] **UAT-01**: The six pending `03-UAT.md` scenarios are executed against the installed build and recorded pass/fail with what was observed, replacing the `[pending]` placeholders
- [ ] **UAT-02**: The original complaint is closed on the record: the installed application opens on the dashboard, and the stale-artifact root cause (an installer older than the UI) is retired by the MSI phase
- [ ] **UAT-03**: v1.0 ships — phases 1–3 archived to `.planning/milestones/`, a MILESTONES.md entry, tag `v1.0`, and PROJECT.md/STATE.md advanced to the next milestone

## Deferred (carried forward, not in this milestone)

- **BG-01 / BG-02**: background deduplication and upscaling on the indexer scheduler and queue
- **UI-01**: in-app indexer status/history surface
- **DIST-01**: indexer self-update alongside the app's update check
- **Help corpus naming**: the compiled help still names the old wizard steps (`HelpTopics.ForStepIndex` maps 0–5)

## Out of Scope

| Feature | Reason |
|---------|--------|
| New tagging, dedup or upscale capability | This milestone proves and ships what exists; capability work is v1.3+ |
| A second installer technology | Inno and MSI already exist; the work is making the MSI honest, not adding a third |
| Daminion server administration | The milestone exercises the client against a server the user provides |
| Reworking §10's other eight criteria | They are already ✓ with recorded evidence; only criterion 6's wording is unsound |
| Signing the installers | Needs a certificate and a key-management decision the project has not made |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| MSI-01 | Phase 9 | Complete |
| MSI-02 | Phase 9 | Complete |
| MSI-03 | Phase 9 | Complete |
| MSI-04 | Phase 9 | Complete |
| MSI-05 | Phase 9 | Complete |
| DAM-01 | Phase 10 | Complete |
| DAM-02 | Phase 10 | Complete |
| DAM-03 | Phase 10 | Complete |
| DAM-04 | Phase 10 | Complete |
| GATE-01 | Phase 11 | Pending |
| GATE-02 | Phase 11 | Pending |
| GATE-03 | Phase 11 | Pending |
| GATE-04 | Phase 11 | Pending |
| UAT-01 | Phase 12 | Pending |
| UAT-02 | Phase 12 | Pending |
| UAT-03 | Phase 12 | Pending |

**Coverage:** 16 total, 16 mapped, 0 unmapped.

---
*Requirements defined: 2026-10-23*
