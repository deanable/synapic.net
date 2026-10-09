---
phase: 03-collapse-navigation-and-retire-settings-dialogs
plan: 04
subsystem: ui
tags: [settings, ui-design-5, dashboard, persistence, config, d3, acceptance-record]

# Dependency graph
requires:
  - phase: 03-collapse-navigation-and-retire-settings-dialogs
    provides: 03-01 (the settings dialogs retired; parameters inline in Region B, which freed the dashboard's Settings panel of the source form and the model picker); 03-02 (the shell the panel binds for the server it operates)
provides:
  - An owning plan and acceptance record for the app-wide settings view (ui-design §5) — five sections, the two-places rule, per-setting reach, persistence
  - The citations 03-05's gate uses for §5 instead of sweeping delivered work as an unowned "extra"
affects: [03-05 final gate (§5 item), any future settings work, the help corpus's topic names]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "A retro-record plan is written after delivery when work lands without one: it states the must-haves the delivered code is judged against and records the evidence, so the phase gate cites a plan instead of an exception"
    - "Settings persistence is proven on a private temp config path, so a test can never read or write the user's real config.json"

key-files:
  modified:
    - .planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-04-PLAN.md
  created:
    - .planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-04-SUMMARY.md
  recorded:
    - src/Synapic.Main/ViewModels/SettingsViewModel.cs
    - src/Synapic.Main/Views/Dashboard/SettingsPanel.axaml
    - src/Synapic.Main/Views/Dashboard/SettingsPanel.axaml.cs
    - tests/Synapic.Main.Tests/SettingsViewTests.cs

key-decisions:
  - "§5 gets its own plan rather than an extra item in the gate: the gate verifies, it does not own — the open question STATE recorded is answered here, and 03-05 depends on this record"
  - "The record states what is NOT covered (the wide section-nav layout, a health-details surface) rather than counting a nearby control as the missing one"
  - "No source or test file was touched by this plan — the delivered code is recorded as it stands, so the gate verifies the same content the earlier summaries describe"

patterns-established:
  - "A section-by-section verdict table: each ui-design §5 row gets present-or-✗ with the control or fact that proves it, so a partially delivered section cannot read as done"

requirements-completed: []

# Metrics
duration: 20min
completed: 2026-10-09
---

# Phase 3 Plan 04: App-wide settings view (§5) Summary

**ui-design §5 now has an owning plan and a cited record: the panel is the five-section app-wide settings view, nothing is configured twice, every setting reaches what it configures on a private temp config — 8 §5 pins and 22 audit facts pass, with two §5 rows recorded ✗ rather than assumed**

## Performance

- **Duration:** 20 min (the acceptance record; the code itself was delivered earlier — by `MainWindowViewModel`'s requirement rather than by a plan — and was carried by 03-02's commit `d79917c`)
- **Tasks:** 3
- **Files changed:** 0 source, 0 test, 2 planning files (this record and the gate's renumbering)
- **Cases:** 460 → 460 (0 new — the 8 §5 pins were already inside the 03-02 count; this plan added no test)

## What the §5 work is, and how it reached the tree

The dashboard's Settings panel is the app-wide settings view (ui-design §5, decision D3). It replaced Phase 2's placeholder, which had hosted the source form and the model picker — both moved to their one home (Region A, and Tagging's Parameters region) when 03-01 retired the dialogs. The delivered pieces:

- [SettingsViewModel.cs](src/Synapic.Main/ViewModels/SettingsViewModel.cs) — the panel's view model: theme and log level live over `config.json`, the inference-server surface (delegating to the shell it is given), the Defaults section with `ApplyDefaultsToNewSession()`, and About.
- [SettingsPanel.axaml](src/Synapic.Main/Views/Dashboard/SettingsPanel.axaml) — one scrolling column of five `settingsSection` blocks.
- [SettingsViewTests.cs](tests/Synapic.Main.Tests/SettingsViewTests.cs) — eight facts over the real compiled window.
- `ConfigService.UiSettings` defaults, `SynapicLog.SetMinimumLevel`/`CurrentMinimumLevel`, and the persisted theme applied at startup.

They were written as untracked files that `MainWindowViewModel` then depended on, so 03-02's commit carried them. No plan in the roadmap owned them: 03-01's must-haves cover the dialog retirement and the inlining, and the Phase 3 goal only implies §5 through its source list. This plan adopts that work and records the evidence; it writes no code.

## §5 swept item by item

§5's table, row by row, with the control or fact that proves it (`grep -n 'settingsSection' src/Synapic.Main/Views/Dashboard/SettingsPanel.axaml` → lines 33, 136, 153, 176, 203 for `SectionInferenceServer`, `SectionAppearance`, `SectionLogging`, `SectionDefaults`, `SectionAbout`):

| §5 section | Item | Verdict | Evidence |
|---|---|---|---|
| Inference server | status (stopped/starting/ready/error) | ✓ | `Shell.ServerBrush` + `Shell.StatusText` |
| Inference server | Start/Stop Server | ✓ | `StartServerCommand`/`StopServerCommand` — one button each, asserted by `Inference_server_section_operates_the_server_through_the_shell` |
| Inference server | auto-launch toggle | ✓ | the `Start the server with the app` checkbox, persisted (`Ui.AutoLaunchSidecar`) and asserted |
| Inference server | Build/Download sidecar variants | ✓ | one row per `Shell.SidecarVariants` entry, rendered from the shell's own collection (the same fact asserts `Assert.Same`) |
| Inference server | device fallback warning | ✓ | `Shell.DeviceNotice` gated on `IsDeviceNoticeVisible` |
| Inference server | health details | ✗ | the panel shows the device `/health` reports and the fallback warning; a fuller health listing (uptime, port, loaded model id) has no surface — recorded, not assumed |
| Appearance | theme (system/light/dark) | ✓ | the theme combo; `Appearance_theme_applies_to_the_running_app_and_persists` |
| Appearance | density notes | ✓ | the inline note "Density is fixed: the same 2/4/8/12/16 rhythm everywhere." |
| Logging | log level | ✓ | `Logging_level_applies_to_the_running_logger_and_persists` (live sink, then the file) |
| Logging | open log folder | ✓ | `OpenLogFolderCommand` button |
| Logging | diagnostics drawer toggle | ✓ | `Diagnostics_toggle_shows_and_hides_the_shell_log_view` (off by default, shows and hides the shell's log list, persisted) |
| Defaults | a new session's values | ✓ | `Defaults_seed_a_session_that_has_no_saved_engine_state`; device, tagging confidence, max items |
| Defaults | no defaults configured | ✓ | `A_settings_file_without_defaults_leaves_the_shipped_values_alone` |
| About | version, links | ✓ | `About_reports_the_version_and_the_links` (help home + repository) |
| — | app-wide settings live **only** here | ✓ | `Settings_panel_is_the_five_section_app_wide_settings_view`: the panel holds no `DatasourceSourcePanel` and no `Step2Engine`, and `grep -rn "DatasourceSourcePanel\|Step2Engine" src/Synapic.Main/Views/Dashboard/` → no matches |
| — | own layout: section nav left, **or** a single scrolling column on narrow windows | partial | the single scrolling column (the form §5 allows) is what ships at every size; the wide form with a section nav on the left is not implemented — see Deviations |

## Verification

- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsViewTests"` → **8 passed, 0 failed**
- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --filter "FullyQualifiedName~UiLayoutAuditTests"` → **22 passed, 0 failed**
- Both filters together → **30 passed, 0 failed, exit 0**
- `dotnet test Synapic.Net.sln -c Release` → Shared **6/6**, Integration **5/5**, Main **460/460**, exit 0 — the suite the §5 pins live in
- Layout: the dashboard (settings panel and its five sections in the audited tree) audits clean at **1600×900, 1280×800, 1024×700, 900×600** via `Home_screen_has_no_overlapping_or_clipped_controls` (four `InlineData` rows), plus `Dashboard_panels_read_in_order_and_audit_clean` at **1024×700 and 900×600** (two rows) for the Settings-above-the-three-panels order
- `grep -n 'settingsSection' src/Synapic.Main/Views/Dashboard/SettingsPanel.axaml` → the five `x:Name`s in reading order
- `grep -rn "DatasourceSourcePanel\|Step2Engine" src/Synapic.Main/Views/Dashboard/` → **no matches** (nothing configured in two places)

## Decisions Made

- **§5 becomes a plan, not a gate item.** STATE left it open ("whether §5 gets swept by 03-04 or becomes a plan of its own"); the answer is a plan, with the gate depending on it. A gate that owns work cannot honestly verify it.
- **The record is written after delivery and says so.** The alternative — pretending the plan preceded the code — would have made every timestamp in the phase a lie and hidden the fact that the work reached the tree through another plan's commit.
- **Two §5 rows are recorded ✗ / partial rather than mapped onto something nearby.** A health-details surface and §5's wide section-nav layout do not exist; counting the status line and the narrow column as them would have closed §5 on a technicality and left the next reader unable to tell what is missing.

## Deviations from Plan

1. **[Rule 1 — the gate was renumbered]** The gate was already `03-04` when this record was written; it moved to `03-05` instead, so this record takes `03-04`, plan order matches execution order, and the gate stays the phase's last plan (as in `01-04` and `02-05`). The gate file was renamed, its frontmatter renumbered (`plan: 05`, `wave: 4`, `depends_on` + `03-04`, threat IDs `T-03-05`), a §5 task added, and the "03-04 final gate" references in ROADMAP, STATE, PROJECT.md and the three earlier summaries repointed. Verification: `grep -rn "03-04 final gate" .planning/` returns nothing; every remaining `03-04` reference names this settings record.
2. **[Rule 1 — the plan writes no source]** The plan's `files_modified` lists only this summary. That is deliberate: the §5 code is delivered, and re-touching it to make the plan look like it produced it would invalidate the earlier summaries' "suite green at this commit's content" claims.
3. **[Rule 2 — the audit sizes are stated per fact]** The temptation was to write "audited at four sizes" of the whole dashboard. Only `Home_screen_has_no_overlapping_or_clipped_controls` runs at four; `Dashboard_panels_read_in_order_and_audit_clean` runs at two. The summary states each fact's own rows.

**Total deviations:** 3 auto-fixed (one structural renumber, two honesty-preserving phrasings). **Impact:** §5 is owned and verifiable, and the gate's item is now "verify a record" instead of "verify work no plan describes".

## Issues Encountered

- **A device chosen before the shell is built gets rewritten.** `ApplyDefaultsToNewSession` writes the configured default device into any session whose engine settings were not loaded from disk, which is why the two `ServerDetectionTests` device facts had to state the device after construction. Harmless for a real first run (a fresh session), already logged as a STATE decision; recorded again here because it is the kind of ordering coupling a §5 change could trip over.
- **The help corpus still numbers the old steps.** The panel's `HelpScope` topics point at `step3-log.html` and `settings-reference.html` by old-step file names, so §5's help wiring is correct by mapping rather than by name. A docs pass, not this phase's — but the gate should not read the topic names as evidence of a stale UI.

## Next Phase Readiness

- **03-05's gate has a plan to cite for §5.** Its Task 3 verifies this record rather than re-deriving it; the citations it should use are listed below.
- The gate's other items are unaffected: this plan changed no source, no test and no view.

**What 03-05's gate cites from this summary:**

- Structure and the two-places rule: `Settings_panel_is_the_five_section_app_wide_settings_view` — five sections in order, each effectively visible, no `DatasourceSourcePanel`/`Step2Engine` inside the panel.
- Per-section reach: `Appearance_theme_applies_to_the_running_app_and_persists`, `Logging_level_applies_to_the_running_logger_and_persists`, `Diagnostics_toggle_shows_and_hides_the_shell_log_view`, `Defaults_seed_a_session_that_has_no_saved_engine_state`, `A_settings_file_without_defaults_leaves_the_shipped_values_alone`, `Inference_server_section_operates_the_server_through_the_shell`, `About_reports_the_version_and_the_links`.
- Layout: `Home_screen_has_no_overlapping_or_clipped_controls` (four sizes) and `Dashboard_panels_read_in_order_and_audit_clean` (two sizes).
- Open items the gate must not count as done: §5's health-details row and its wide section-nav layout.

---
*Phase: 03-collapse-navigation-and-retire-settings-dialogs*
*Completed: 2026-10-09*

## Self-Check: EVIDENCE VERIFIED; §5 GAPS REMAIN

- Key files exist on disk: `SettingsPanel.axaml` (five `settingsSection` blocks at lines 33/136/153/176/203), `SettingsViewModel.cs`, `SettingsViewTests.cs`, `SettingsPanel.axaml.cs`.
- §5 filter 8/8, audit filter 22/22, the five section names in reading order, the no-two-places grep and both audit facts' stated sizes are verified. This does not mean every §5 requirement is implemented: the health-details row is ✗ and the wide section-nav layout is not implemented; see the table above.
- Plan-level verification: full solution suite 6 + 5 + 460 passed / 0 failed, exit 0.
