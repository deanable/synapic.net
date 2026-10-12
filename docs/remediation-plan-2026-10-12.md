# Synapic.NET — remediation implementation plan

**Date:** 2026-10-12

**Input:** [repository review](code-review-2026-10-12.md), baseline `main` at `db5bbf01`

**Status:** proposed, not implemented; no release, commit, or catalog mutation authorized by this document.

## 1. Objective and scope

Make Tag, Dedup and Upscale safe, truthful, recoverable and maintainable without rewriting the application or changing the approved dashboard/operation layout.

This plan covers **all 22 ranked review findings**, the targeted OO cleanup, documentation reconciliation, and the outstanding verification limitations. Deliver this plan as `docs/remediation-plan-2026-10-12.md`. Implementation is a separate task.

The existing [UI roadmap](../.planning/ROADMAP.md) and [state](../.planning/STATE.md) remain unchanged. Their Phase 3 acceptance gaps are still open; the remediation phases below use letters to avoid silently renumbering or closing that milestone. The previous presentation-only scope fence remains valid for that milestone; service changes belong to this separately authorized remediation effort.

### Working rules

- Repair observable behavior before extracting architecture. Each slice includes the regression test, fix, and affected user-interface check.
- Use disposable image copies, isolated settings stores, loopback catalog stubs and temporary directories by default. Do not touch the user's normal settings or production catalog.
- Preserve current UI routes, shared execution kernel, wire field names, repaired-reply markers and existing useful tests unless a documented behavior change requires a corresponding test change.
- Prefer immutable records/enums and composition. Do not add a generic repository framework, mediator, service locator, deep inheritance tree or pass-through interface for every class.
- Document error, cancellation, partial-success and uncertain-write behavior explicitly. An HTTP 200 or a log message is not proof of a successful mutation.
- Adapt the planning skill's scope/dependency/verification discipline manually. This is not an executed GSD phase or a claim that unavailable GSD planner/checker agents ran.

## 2. Priority and dependency map

| Phase | Outcome | Primary findings | Dependency |
|---|---|---|---|
| A | Preserve original images and metadata | R01, R02, R16 | None |
| B | Bind destructive operations to their source and recover checkout | R03, R04, R05, R14 | None; reuse A for sidecar-aware actions |
| C | Correct paging, deadlines, results and success accounting | R06, R07, R09, R10 | B's source identity/activity contracts for C3 |
| D | Protect sidecar calls and make runtime/output transitions coherent | R12, R13, R19 | Security boundary starts immediately; C1 for deadline integration |
| E | One effective settings snapshot and normal-user sidecar updates | R08, R11, R20 | D1 capability handshake for newly launched variants |
| F | Simplify ownership and make dedup/scheduling correct and cancellable | R15, R17, R18 | A–E behavior contracts; baseline measurement before scheduling changes |
| G | Honest test tiers, packaging, documentation and release gates | R21, R22 | Test-tier foundation begins immediately; final gate after A–F |

**Recommended serial execution:** G0 → A1 → A2 → B1 → B2 → B3 → C1 → C2 → C3 → D1 → D2 → D3 → E1 → E2 → E3 → F1 → F2 → F3 → G1 → G2 → G3.

**Risk-first exception:** implement D1 alongside A/B, not after a long cleanup, if the sidecar is exposed beyond loopback or used with untrusted local callers. Until then, run it loopback-only and do not treat loopback as authentication.

Independent slices may run in parallel only with disjoint file ownership. Serialize work touching the metadata writer, Daminion client, shell/settings wiring, or Python service/runtime. Finish each slice's verification before starting dependent work.

## 3. Proposed contracts and decision boundaries

These are implementation recommendations, not already-existing APIs. Confirm any user-visible policy before its slice starts; use existing behavior where it is safe and unambiguous.

| Concern | Proposed invariant |
|---|---|
| Metadata patch | Explicit `Unchanged`, `Replace`, `Merge` (keywords), and `Clear`; an omitted field is unchanged. Preserve unowned XMP namespaces/properties. Keywords merge by default, matching the current interface promise; intentional replacement/clear must be explicit. |
| Source identity | Local canonical root plus recursion/scope; catalog server identity plus catalog GUID and scope/filter snapshot. Use a connection/scan generation too. Never persist credentials in result identities. Normalize paths using platform semantics; textual normalization alone does not prove junction/symlink equivalence. |
| Work item/result | Run ID plus immutable source identity and item identity (local full path or catalog item ID). Retain requested/applied fields and a typed outcome: succeeded, failed, partial, cancelled or unknown. |
| Catalog page | Eligible items plus raw page count or explicit next cursor and end-of-data indication. Never advance using the filtered item count. |
| Operation activity | One atomic activity lease covering Tag, Upscale, Dedup Scan/Apply and report Retry/Verify; busy/cancelling stays active through cleanup. Guard commands and application entry points. |
| Checkout | Owned lease with confirmed commit flag; every uncommitted exit receives bounded cleanup. An uncertain check-in requires reconciliation, not blind rollback/retry. |
| Settings | Schema-versioned effective snapshot; registry backend on Windows, intentional file backend elsewhere; field-scoped updates, deterministic legacy migration, one startup application order. |
| Sidecar capability | Per-launch unpredictable token over a private startup channel, explicit protocol compatibility, loopback binding and Host/Origin validation. No token in command-line arguments, URLs, logs or persisted settings. |
| Python runtime | Application-scoped owner of effective config, pipeline plus resolved task, load/cache state and serialization policy. Validate before side effects. Distinguish unloaded, loading, ready and failed. |

**Policy checkpoints:** keyword merge/replace/clear semantics (A1); TIFF sidecar move/delete scope (A2/B3); Daminion response envelopes for supported versions (B2/C3); alpha compositing and metadata preservation for upscale (D3); settings conflict precedence (E1); transitive versus all-pairs duplicate grouping and ColorMoment availability (F1); package distribution support (G1).

Recommended low-risk defaults: disable local duplicate Tag until implemented; remove unsupported ColorMoment rather than silently alias it; keep bundled sidecars immutable; preserve an explicit CPU preference and use Auto only for unspecified defaults; retain the existing flat/frozen Python launch interface unless installable-package support is deliberately approved. Record final policies in the relevant slice before changing behavior.

## 4. G0 — establish honest verification before repairs

**Findings:** R22; supports every phase. **Depends on:** nothing.

1. Convert reproducible review probes into permanent regression tests with each repair, not all at once. Use the existing xUnit/Avalonia and pytest conventions; do not introduce a new test framework merely for this plan.
2. Replace environment/platform early returns with real, reasoned skips supported by the installed runner. Separate hermetic, Windows-platform, opt-in sidecar/model and opt-in live-catalog tiers. Absence of a required release-tier environment is a blocked gate, not a pass.
3. Remove the conflicting early-return recommendation from [testing guidance](testing.md) when this behavior lands. Record actual tier execution in verification evidence.

**Acceptance:** an absent Daminion server or unavailable Windows registry produces a visible skip reason; hermetic tests still exercise failures without network/model dependencies; required jobs cannot quietly pass by skipping their entire purpose.

**Verification:** default test run with live credentials unset; Windows/platform and lightweight Python runs; a deliberately unavailable environment proves skip reporting. Do not globally skip tests that previously executed. Model/GPU coverage remains explicitly separate.

## 5. Phase A — metadata preservation and crash-safe writes

**Files:** [metadata writer](../src/Synapic.Main/Services/MetadataWriterService.cs), [metadata tests](../tests/Synapic.Main.Tests/MetadataWriterTests.cs), [field-selection tests](../tests/Synapic.Main.Tests/TagFieldSelectionTests.cs).

### A1 — patch existing JPEG metadata safely

**Findings:** R01, R02. **Depends on:** G0 conventions only.

- Seed JPEG fixtures with category, description, existing keywords, creator/copyright and an unrelated custom namespace. Test every field-selection combination through the real writer.
- Implement an explicit patch contract and merge into the existing XMP document. Treat malformed/multiple packets deliberately; fail without modifying the original when preservation cannot be guaranteed.
- Validate basic APP1 payload size, including header and segment-length constraints, before writing. Initially reject oversized packets with a clear failure rather than adding extended XMP accidentally.
- Write to a unique same-directory staging file, validate image structure/readability, and atomically replace where supported. Preserve required attributes/permissions; clean up only owned temporary files. Do not fall back to an unsafe in-place rewrite.

**Acceptance:** keywords-only updates preserve other metadata; unrelated namespaces survive; merge/replace/clear differ correctly; oversized descriptions fail before mutation; injected write/validation/replace failures and cancellation before commit leave the original byte-identical and decodable. Cancellation racing a completed commit must report the committed outcome rather than claim no write.

**Verification:** real read/write/read fixtures; fault injection at staging and replacement boundaries; decode with an independent reader already available in the project; Tag checkbox selections through headless UI and a disposable local tagging round trip.

### A2 — complete PNG and TIFF metadata lifecycle

**Findings:** R16; extends R01/R02 guarantees. **Depends on:** A1 patch/atomic-write contract.

- Replace/merge the owned PNG XMP chunk instead of appending duplicates. Preserve other chunks; validate resulting chunk structure and decode.
- Read TIFF `.xmp` sidecars, apply the same patch semantics and stage sidecar writes atomically. Keep embedded TIFF versus sidecar ownership explicit; do not claim embedded support that is not implemented.
- Define image/associated-sidecar artifacts for later move/delete operations. A multi-file operation is not a single filesystem transaction: specify recovery and partial outcomes rather than claiming atomicity across both files.

**Acceptance:** repeated identical PNG patches are metadata-idempotent with no additional XMP chunks; TIFF sidecar write/read works; failed writes retain the original image and previous sidecar; ownership/destination conflicts produce explicit failures.

**Verification:** JPEG/PNG/TIFF fixture matrix, repeated writes, unrelated metadata preservation, missing/malformed sidecars, and cancellation/failure tests. Full metadata suite after the final edit.

## 6. Phase B — safe destructive operations and checkout recovery

**Files:** [dedup view model](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs), [dedup service](../src/Synapic.Main/Services/Processing/DedupService.cs), [operation shell](../src/Synapic.Main/ViewModels/Operations/OperationShellViewModel.cs), [main-window view model](../src/Synapic.Main/ViewModels/MainWindowViewModel.cs), [upscale handler](../src/Synapic.Main/Services/Processing/UpscaleItemHandler.cs).

### B1 — source-bound results and one operation activity lease

**Findings:** R03, R04. **Depends on:** none.

- Capture immutable source identity/scan generation with dedup groups; invalidate groups and dispose thumbnails on relevant source, catalog, scope or connection changes.
- Snapshot action/targets/source before confirmation; revalidate identity and acquire the activity lease immediately before mutation. A stale confirmation cannot authorize a new source or different target set.
- Cover Scan/Apply, Tag/Upscale and report Retry/Verify with one coordinator. Prevent conflicting starts, source edits, disconnects and route changes while active; retain Stop and keep the lease until cleanup finishes.
- Enforce guards in navigation commands, including Alt+Home, and application entry points, not just XAML enabled states. Normal navigation remains free when idle.

**Acceptance:** catalog A results cannot act on catalog B with overlapping IDs; switching to local source invalidates catalog groups; simultaneous start attempts acquire at most one lease; Scan and Apply never overlap; exceptions/cancellation release activity after cleanup; keyboard navigation cannot bypass the busy guard.

**Verification:** stub catalogs with overlapping IDs, delayed confirmations, concurrent entry attempts, source-change notifications, headless commands/key dispatch, and busy-state bindings for every operation/report action.

### B2 — release checkout on every uncommitted exit

**Findings:** R05; response uncertainty also belongs to R10. **Depends on:** explicit outcome semantics drafted with C3.

- Own checkout via a lease/finally path. Cover missing download, null upscale result, transport failure, exception, cancellation and confirmed check-in refusal.
- Cleanup uses its own bounded token, not the already-cancelled run token. Retain the primary failure plus rollback failure details.
- Reconcile unknown check-in outcomes against supported server behavior before rollback or retry; retain an actionable unresolved item if reconciliation is unavailable.

**Acceptance:** exactly one bounded undo attempt on each confirmed-uncommitted path; no undo after confirmed check-in; unknown outcomes are not labelled success and are not blindly retried; temporary downloads/outputs are cleaned according to ownership.

**Verification:** extend [version-control request tests](../tests/Synapic.Main.Tests/DaminionVersionControlRequestTests.cs) with handler-level failure injection at every boundary. Live validation is opt-in against a disposable catalog item with explicit write authorization.

### B3 — stop offering no-op actions and preserve artifact ownership

**Findings:** R14; integrates A2/R16. **Depends on:** B1; A2 for TIFF-associated file handling.

- Disable/remove local duplicate Tag with an honest reason; retain supported catalog Tag. Do not remove groups or report applied changes for unsupported actions.
- Move/delete only the captured target set with a safe keeper invariant. Treat associated sidecars deliberately; expose per-target partial/cancelled outcomes and remove only completed review rows.
- Cover destination changes, newly created destination folders, destination conflicts and volume-root/path semantics. Do not infer path containment from string prefixes alone.

**Acceptance:** every enabled action performs a defined mutation; unsupported local Tag leaves files and groups unchanged; keeper is never accidentally targeted; image/sidecar failures are surfaced rather than orphaned silently.

**Verification:** real disposable file actions plus headless Apply state/selection tests; no production deletes.

## 7. Phase C — paging, deadlines and truthful result identity

**Files:** [workflow runner](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs), [Daminion client](../src/Synapic.Main/Services/Daminion/DaminionApiClient.cs), [inference client](../src/Synapic.Main/Services/InferenceApiClient.cs), [sidecar service](../src/Synapic.Main/Services/InferenceSidecarService.cs), [tag orchestrator](../src/Synapic.Main/Services/Processing/ProcessingOrchestrator.cs), [results view model](../src/Synapic.Main/ViewModels/Steps/Step4ResultsViewModel.cs).

### C1 — make request deadlines effective

**Findings:** R06. **Depends on:** none.

- Set the owned HTTP transport timeout intentionally; if infinite, bound every endpoint independently, including health/config/models/shutdown.
- Keep the current five-minute Tag and thirty-minute Upscale allowances unless explicitly revised. Separate caller cancellation, endpoint deadline and transport failure in outcomes/logs.

**Acceptance:** the default 100-second HttpClient deadline cannot preempt the intended inference allowance; short health/config budgets stay bounded; user Stop promptly cancels an in-flight request.

**Verification:** injected delayed transport with shortened test budgets; assert timeout precedence and cancellation classification without waiting minutes. Inspect actual production transport configuration and exercise Stop through the operation interface.

### C2 — return raw paging information

**Findings:** R07. **Depends on:** none; coordinate Daminion client ownership with B2/C3.

- Separate eligible items from raw server count/cursor; advance by documented server semantics and continue through filtered-empty pages.
- Deduplicate by stable catalog identity; use explicit no-progress/repeated-page detection that does not silently truncate legitimate data. Honor eligible MaxItems separately from raw fetch count.

**Acceptance:** the review's IDs 1 and 4 are both returned; sparse/empty filtered pages, short server-capped pages and overlaps cause neither lost nor duplicate processed items; malformed non-advancing pagination reports a problem rather than pretending completion.

**Verification:** deterministic HTTP stub datasets with expected full ID lists and requested offsets; test both collections and saved searches plus cancellation and MaxItems.

### C3 — one typed outcome per attempted item and identity-based retry

**Findings:** R09, R10. **Depends on:** B1 identity/activity contract; C2 corrected selection.

- Record a terminal result for every attempted item, including thrown download/inference/write failures. Distinguish never-started queued items from attempted cancelled ones.
- Retry the original failed identities/source snapshot, not current selection or basename. Replace rows only after a replacement outcome exists; preserve errors when retry is cancelled.
- Count required metadata write failure consistently in rows and summaries. Record applied/requested fields and partial results.
- Parse Daminion application envelopes, missing field GUIDs and version-control outcomes; verify acknowledgements where needed. Support observed server versions explicitly; classify unrecognized success responses as unknown, not success.

**Acceptance:** failed inference produces a visible failure row; same-named files in different folders/catalogs remain distinct; retry never removes successful rows; cancellation retains original failures; denied HTTP-200 writes are failures everywhere; summaries reconcile exactly with terminal item outcomes.

**Verification:** fake sidecar/writer failures, duplicate basenames, changed selection, interrupted retry, partial fields and HTTP-200 refusal stubs; headless Retry/Verify bindings and report counts. Run repaired-reply regression tests too.

## 8. Phase D — sidecar trust, runtime state and output safety

**Files:** [Python service](../src/Synapic.Inference/service.py), [model loader](../src/Synapic.Inference/model_loader.py), [upscaler](../src/Synapic.Inference/upscaler.py), [wire contracts](../src/Synapic.Shared/Contracts/Contracts.cs), [JSON serializer context](../src/Synapic.Shared/JsonSerializerContext.cs), [sidecar HTTP tests](../tests/Synapic.Integration.Tests/SidecarHttpContractTests.cs).

### D1 — authenticate the desktop/sidecar boundary

**Findings:** R12. **Depends on:** none; begin early where exposure warrants it.

- Generate a per-launch token in the lifecycle owner; transfer via a private inherited channel, require it on privileged endpoints and use constant-time comparison. Protect diagnostics as needed to avoid exposing paths/config.
- Enforce loopback bind policy and strict accepted Host/Origin rules. Native requests with no Origin can work only with the token; a foreign browser Origin must be rejected even with otherwise valid credentials.
- Constrain output-path behavior, reject input overwrite unless deliberately supported, and define the limitation that a local capability is not a sandbox against a fully compromised same-user process.
- Version the capability handshake and update the C# client, Python tests, packaged startup/smoke checks and generated protocol reference together. Reject old unauthenticated binaries clearly; do not add a silent legacy-auth bypass.

**Acceptance:** missing/wrong/stale token cannot mutate config, download, infer/write or shut down; foreign Host/Origin fails; a valid owned desktop launch supports the full workflow; tokens never appear in logs, settings, URLs or process arguments.

**Verification:** TestClient negative matrix, C# HTTP request/header checks, real loopback launch/tag/config/shutdown with temporary files, and captured-log redaction checks. A browser-origin probe must use only the isolated local test server.

### D2 — validate config before effects; own model transitions

**Findings:** R13. **Depends on:** D1 client/server baseline.

- Validate the full proposed model/task/device config before unload or mutation. Use explicit unloaded/loading/ready/failed states, retaining the resolved task with each cached pipeline.
- Ensure a valid model switch starts a load or leaves a state that a subsequent request can load; readiness waiting cannot block the only code that initiates loading.
- Define shared-pipeline concurrency conservatively, defaulting to serialized execution until real-model concurrency is shown safe. Prevent warmup, config updates, downloads and requests from creating contradictory state.

**Acceptance:** rejected config leaves prior config/pipeline/status intact; valid switch can tag successfully within its deadline; cache hits use the resolved task; concurrent load/switch/cancel paths terminate deterministically without a stuck loading state.

**Verification:** lightweight fake-pipeline transition tests, warmup/load/cache tests, then opt-in cold/warm real-model checks. No accuracy or GPU throughput claims from mocks.

### D3 — safe upscale conversion, destination policy and staging

**Findings:** R19. **Depends on:** D1; reuse A's preservation principles.

- Normalize orientation/pixels/alpha for target format. Confirm the alpha-to-JPEG background policy; preserve alpha for compatible targets.
- Enforce overwrite_existing for explicit and automatic paths; reject unintended source replacement. Use staged outputs and deterministic destination-conflict handling, without an unchecked exists-then-write race.
- Specify supported ICC/EXIF/XMP preservation and catalog-original replacement policy; preserve metadata explicitly or report unsupported behavior, not silent loss.
- Bound model residency/device ownership together with D2; do not keep every upscale pipeline indefinitely.

**Acceptance:** RGBA→JPEG succeeds with the agreed compositing policy; overwrite disabled never changes an existing explicit target; interrupted/failed save leaves prior target/input untouched; dimensions/orientation and preservation policy hold for fast and AI paths.

**Verification:** real Pillow fast-path fixtures, mocked AI-path tests and an opt-in torch/model tier that actually executes the six previously skipped tests. JPEG/PNG/TIFF output, EXIF orientation, collision and cancellation cases.

## 9. Phase E — settings and installed-update truth

**Files:** [startup](../src/Synapic.Main/App.axaml.cs), [settings view model](../src/Synapic.Main/ViewModels/SettingsViewModel.cs), [config service](../src/Synapic.Main/Services/ConfigService.cs), [app registry store](../src/Synapic.Main/Services/AppSettingsRegistryStore.cs), [engine store](../src/Synapic.Main/Services/EngineSettingsStore.cs), [sidecar downloader](../src/Synapic.Main/Services/SidecarDownloadService.cs), [installer](../build/installer-windows.iss).

### E1 — one versioned settings repository and startup snapshot

**Findings:** R08. **Depends on:** G0 Windows tier; B1 source identity integrates with session restoration.

- Inventory existing JSON/registry/source/engine schemas and define field-by-field precedence. Preserve unknown fields and credential protections. A missing registry key must not bypass legacy migration.
- Use one schema-versioned effective snapshot and field-scoped updates; remove competing whole-state writers and positional-default reconstruction.
- Restore source type/scope/limits into Session consistently. Apply settings before theme, telemetry, logging behavior and dependent view-model construction; retain only minimal bootstrap logging needed to diagnose restore failure.
- Migrate idempotently with recoverable prior data; unsupported future schema, corrupt values and write refusal must not silently reset preferences.

**Acceptance:** JSON-only first run migrates; subsequent restart has no store disagreement; general/upscale/dedup edits preserve unrelated fields; Ctrl+S preserves diagnostics/device/threshold/limits; source and startup side effects match the restored snapshot; cross-culture thresholds remain correct.

**Verification:** injected registry keys/file paths; separate process or independently reconstructed launch round trips; Windows actual registry plus non-Windows file backend; corrupt/missing/conflicting/future-version fixtures; settings controls through compiled XAML.

### E2 — explicit Auto versus explicit CPU

**Findings:** R20. **Depends on:** E1 durable defaults.

- Represent unspecified/Auto explicitly; detect supported acceleration via injected capability facts. Never replace a user's explicit CPU choice because CUDA is available.
- Persist device values, not dropdown indices; distinguish MPS capability from merely running on macOS.

**Acceptance:** fresh Auto settings choose supported acceleration; explicit CPU survives restart; absent/unusable GPU has a deterministic fallback and truthful explanation.

**Verification:** CPU/CUDA/MPS capability matrix and installed-machine check; deterministic tests must not depend on a developer's GPU.

### E3 — writable, independent sidecar variants

**Findings:** R11. **Depends on:** D1 handshake/protocol policy.

- Install downloaded sidecars into per-user versioned directories by RID; record release/build identity, checksum and protocol compatibility in a small manifest. Keep the bundled executable as an immutable fallback.
- Stage/validate downloads, atomically activate a variant, retain the previous known-good executable until launch health/compatibility succeeds, and roll back activation on failure.
- Separate CPU/CUDA discovery; do not let timestamps or one shared filename represent both variants. Retain existing checksum and multipart-asset protections.

**Acceptance:** CPU and CUDA coexist; normal installed users update without writing Program Files or elevation; interrupted/bad/incompatible downloads do not replace the active good variant; discovery and launch use the same manifest-selected identity.

**Verification:** existing downloader/update tests, temp-directory manifests, packaged normal-user ACL test and real launch handshake per available variant. If standard-user installed testing is unavailable, the installed-update gate remains blocked even if temp tests pass.

## 10. Phase F — focused OO ownership, correct dedup and measured scaling

**Files:** [dedup service](../src/Synapic.Main/Services/Processing/DedupService.cs), [dedup view model](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs), [workflow runner](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs), [source view model](../src/Synapic.Main/ViewModels/Steps/Step1DatasourceViewModel.cs), [main-window composition](../src/Synapic.Main/ViewModels/MainWindowViewModel.cs), [process job](../src/Synapic.Main/Services/ProcessJob.cs).

### F1 — accurate duplicate algorithms and options

**Findings:** R15. **Depends on:** B3 safe action contract.

- Normalize orientation, colour/sample layout and alpha before hashing. Implement supported algorithms truthfully or remove unsupported choices; honor MaxDimension or remove the misleading option.
- Use exact threshold-boundary math; establish golden fixtures for intended aHash/dHash/pHash compatibility and intentional pHash DC behavior.
- Decide/document transitive connected components versus stricter pairwise groups before changing grouping. Preserve scores in review and enforce the keeper invariant.

**Acceptance:** RGBA PNG hashes safely; ColorMoment is genuinely colour-specific or unavailable; boundary similarity never falls below the requested threshold due to rounding; algorithm options affect defined behavior; fixture groups/scores are reproducible.

**Verification:** real image golden vectors, transparency/orientation/colour fixtures, exact threshold tests, group/keeper review tests and existing generated-image suite. Record any intentionally changed group output.

### F2 — bounded scheduling and responsive cancellation, measured first

**Findings:** R17. **Depends on:** C2 paging, C3 outcomes, F1 correctness baseline.

- Capture baseline enumeration/scheduling/DCT/grouping timing, memory and cancellation on deterministic 1k/10k/100k workloads. Use lightweight work-item/hash workloads at 100k to isolate scheduling/grouping; separately measure representative real images and inference.
- Replace one-task-per-item scheduling with bounded worker/streaming enumeration; keep the queue bounded and effective parallelism explicit. Apply pause/cancel to producer and workers.
- Move local enumeration/grouping off the UI path, honor recursion, and add cancellation checkpoints throughout. Preserve agreed group semantics.
- Optimize DCT/indexing only when profiling identifies a bottleneck; compare output equivalence against the baseline before claiming improvements.

**Acceptance:** no O(n) pending task allocation or unbounded work queue; concurrency never exceeds configured capacity; non-recursive sources stay non-recursive; cancellation returns control without continuing queued work; correct IDs/outcomes/groups match the reference.

**Proposed resource targets:** queued work at most twice configured parallelism; cancellation acknowledgement within one second on hermetic enumeration/grouping benchmarks, with blocking native/HTTP cleanup separately bounded and reported. Record machine/fixtures/RSS/elapsed distributions; revise targets openly if measurements justify it, never hide failures. Do not add noisy wall-clock assertions to unrelated unit tests.

**Verification:** correctness tests plus repeated before/after benchmarks with warmup, fixed inputs and identical hardware/runtime. At 100k, a stalled baseline is a recorded timeout, not a fabricated timing; complete small-scale equivalence checks and label large-scale limitations.

### F3 — explicit infrastructure lifecycle and composition root

**Findings:** R18; OO recommendations. **Depends on:** A–E contracts and F2 worker ownership.

- Propagate caller cancellation rather than converting it to network failure; bound cleanup independently. Dispose owned clients/CTS/subscriptions and await logout.
- Serialize sidecar start/stop with a generation-aware lifecycle gate; prevent duplicate starts and stale callbacks. Order shutdown: cancel operations → await bounded cleanup → logout/dispose clients → stop owned sidecar → release UI/DI resources.
- Split sidecar discovery/install from process lifecycle; catalog auth/session from paging, mutation and version-control ownership; dedup scan/action use cases from review presentation.
- Construct graphs at startup/DI, not inside view models or via App.Services. Share source/session abstractions instead of reaching through another screen; keep Avalonia bitmaps/formatting in presentation.
- Implement single-flight session recovery for both throwing and HTTP-response-returning calls. Document platform-specific crash/orphan cleanup honestly.

**Acceptance:** repeated start/stop/connect/disconnect cannot leak owned processes/clients or resurrect old work; cancellation is recognizable; active checkout cleanup precedes infrastructure teardown; core operation tests need neither a global App provider nor real registry/GPU/another screen's view model.

**Verification:** lifecycle race/failure matrix, cancellation and reauthentication stubs, headless close/disconnect during delayed work, process ownership checks and available OS runtime tests. No broad process-kill patterns or assumptions that Windows job behavior is portable.

## 11. Phase G — packaging, documentation and release closure

### G1 — choose and verify the supported Python distribution interface

**Findings:** R21. **Depends on:** D2/F3 runtime boundaries stabilizing.

Recommended minimum: retain the documented script/PyInstaller interface and remove misleading installable-package metadata if wheels are not a product requirement. If installable wheels are required, create a real package, repair imports/entry point and preserve exactly one module identity; do not half-support both layouts.

**Files:** [Python project metadata](../src/Synapic.Inference/pyproject.toml), [requirements](../src/Synapic.Inference/requirements.txt), [pytest bootstrap](../tests/Synapic.Inference.Tests/conftest.py), [sidecar spec](../src/Synapic.Inference/synapic-inference.spec).

**Acceptance:** the declared supported interface is launchable from a clean environment; dependencies resolve reproducibly for CPU/CUDA build paths. If wheel support is kept, wheel build, clean install and installed entry-point smoke must all pass; if removed, documentation/CI no longer advertise or invoke an unsupported wheel interface. Frozen bundle launch remains tested in either case.

### G2 — current documentation and incremental quality checks

**Findings:** documentation/OO recommendations; supports R01–R22. **Depends on:** repaired behavior above.

- Reconcile [architecture guide](codebase-guide.md), [C# reference](csharp-reference.md), [sidecar reference](sidecar-reference.md), [testing guide](testing.md), [README](../README.md) and affected [help topics](help/README.md). Remove or clearly archive obsolete wizard/dialog/build/adopt-server descriptions.
- Establish authoritative metadata-patch, settings/migration, source/result identity, lifecycle and local-trust contracts. Explain supported formats/platforms and partial/unknown outcomes.
- Repair malformed XML comments; gradually enable documentation/format/analyzer diagnostics for touched code. Introduce Python lint/type checks narrowly with an explicit existing-debt plan, not blanket suppressions or boilerplate.
- Regenerate the protocol reference after coordinated wire/security changes; keep structural help checks and add behavior-backed checks where prose can drift from options.

**Acceptance:** documented routes/options/persistence/trust model match code; all local links resolve; protocol/help checks pass; newly enabled quality checks pass without weakened assertions or global suppressions. Historical review findings remain a baseline record, with a separate closure status rather than rewriting evidence away.

### G3 — exact-commit CI and final user-interface verification

**Findings:** R22 final closure. **Depends on:** all phase gates; G0 foundation.

- Run Windows registry/restart tests in CI, not merely package Windows. Keep lightweight Python tests and add an explicit torch/real-model tier with stated hardware/model availability.
- Gate release packaging/publishing on successful required tests for the exact release SHA. Keep unavailable live/model tests visible; determine which are required for each release capability instead of requiring production credentials in default CI.
- Verify through the actual desktop interface: configure source → Tag/Dedup/Upscale → report/retry/verify → Stop → restart/settings restore → normal-user sidecar update. Preserve layout/F1 checks at the four existing sizes.
- Reconcile, but do not automatically close, the old UI Phase 3 empty-result/all-failed branches and feature-preservation claims using new evidence.

**Acceptance:** required tests cannot be bypassed by triggering release directly; empty/all-failed operations have honest UI states; performed/skipped/blocked checks are listed individually; no historical milestone is marked complete without its own item-by-item gate.

**Verification:** local build/test/protocol/help checks, CI workflow review and separately authorized CI/release dry run if needed. Production publishing remains outside this plan's authorization.

## 12. Verification commands and evidence

Run from the repository root. Use the project's current bootstrap/lockfiles; do not install full ML dependencies merely to validate documentation. During implementation, choose focused filters first, then the affected suites and phase-wide regression gate.

```bash
# Preserve the real command status if output is captured through tee.
set -o pipefail

dotnet build Synapic.Net.sln -c Release --nologo
dotnet test Synapic.Net.sln -c Release --no-build --nologo

# Existing lightweight convention; run in a suitable prepared environment.
python -m pytest tests/Synapic.Inference.Tests -q
python build/generate-protocol-doc.py --check
python docs/help/check-help.py
```

The lightweight Python environment needs pytest, httpx, fastapi, uvicorn, pillow and huggingface_hub per the existing [testing guide](testing.md). A full torch/model tier is separate and must not be inferred from the lightweight run. Recheck package/launch commands against G1's chosen interface.

For each slice, record baseline failing reproduction, final edited-file checks, exact commands/exit codes, fixture/source identity, exercised interface, and any skipped/blocked platform/model/live checks. Keep credentials, tokens and real catalog/private path data out of evidence. Store temporary logs under a dedicated ignored artifacts directory; maintain a concise linked phase verification summary when implementation begins.

**Definition of done for a slice:** regression fails on the old behavior; repair passes; affected tests/build/type checks pass after the last code edit; the user-facing operation is exercised; cleanup is verified; docs reflect changed policy; unresolved external gates are explicitly recorded. A blocked gate is not a completed phase. Commit only when separately requested; do not combine unrelated slices into one large change.

**Known baseline, not a new test run for this plan:** review reported Release build 0 warnings/errors, 482 .NET passes (some environmental early returns), Python 254 passes/6 skips, protocol/help passes, and a failed wheel build. Those figures are not acceptance evidence for future repairs.

## 13. Optional follow-on backlog

These unranked review opportunities do not displace the safety work:

- Spreadsheet-safe CSV export, complete escaping and invariant numeric formatting; validate model-controlled cells in a disposable spreadsheet fixture.
- HTTPS guidance and credential/query redaction for catalog connections; preserve required server API compatibility.
- Unique per-run/client temporary downloads across catalogs/app instances, with ownership-scoped cleanup (integrate into B2/F3 where practical).
- Narrow release-job permissions and provenance/signing policy; checksum verification alone is not publisher authentication.
- Move true runtime prerequisites into installer/bootstrap and verify required architecture/framework rather than assuming higher-major roll-forward.
- A recovery dashboard for unresolved checkout/partial file actions, and preview-only metadata/dedup changes before destructive application.

## 14. First implementation handoff

Start with **G0's honest test foundation and A1's JPEG patch/atomic-write slice**, then A2 and B's source-bound operations/checkout recovery. D1 security is an independent early priority, not a reason to postpone safety or wait for a full OO refactor.

The first slice is accepted only when a keywords-only update preserves seeded unrelated metadata, oversized packets are rejected without mutation, and injected failure/cancellation cannot leave an invalid original. Stop after that verified slice for review unless broader execution is explicitly requested.
