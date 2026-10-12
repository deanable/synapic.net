# Synapic.NET — repository code review

**Review date:** 2026-10-12
**Baseline:** `main` at `db5bbf01` (`Download-first sidecar; remove in-app build`)
**Scope:** repository-wide structural inventory; detailed inspection of processing, metadata writing, deduplication, Daminion, settings, MVVM composition, sidecar API/model lifecycle, representative tests, documentation, and CI/packaging. The tracked application source comprises **100 C#/Python/AXAML files and 22,582 lines**. This is a risk-focused review, not a claim that every line or platform was exhaustively exercised.

**Status: review only.** No application source or existing tests were changed. No commits, pushes, deployments, production catalog writes, or normal settings edits were performed. Isolated probes used disposable image copies, a loopback stub catalog, and a unique temporary registry key; the temporary data/key were removed. Review probes/logs are retained under the ignored [artifacts/code-review directory](../artifacts/code-review/).

## Executive assessment

The project has a sound starting point: a useful desktop/sidecar separation, strongly typed wire contracts, a shared workflow kernel, meaningful headless UI tests, warning-as-error compilation, DPAPI-protected Windows credentials, and checksum-verified staged sidecar downloads.

However, **a green test run currently overstates safety and completeness**. There are reproducible data-preservation, state-consistency, persistence, and recovery defects. Several advertised operations either do nothing or behave differently from their labels. I would fix these before undertaking a broad OO cleanup.

The main design problem is not insufficient inheritance. It is **unclear ownership**: settings have multiple writers and stores; view models construct services and coordinate other view models; results lack durable identity; operation state is split across unrelated flags; and infrastructure exposes success/failure through booleans that lose important information.

**Recommendation:** incremental stabilization, then targeted composition-based refactoring. Do not rewrite the application or introduce an elaborate framework.

## Verification performed

| Check | Result | Qualification |
|---|---|---|
| `dotnet build Synapic.Net.sln -c Release --nologo` | Passed: 0 warnings, 0 errors | .NET SDK 10.0.401; clean baseline. |
| `dotnet test Synapic.Net.sln -c Release --no-build --nologo` | **482 passed** | Main 471, Shared 6, Integration 5. The real local sidecar-to-JPEG round trip ran and passed in approximately 30 seconds. Live Daminion tests returned early because credentials were absent; they are misleadingly reported as passed. |
| Python suite with CI-style lightweight dependencies | **254 passed, 6 skipped**, 1 dependency deprecation warning | The six torch-dependent upscale tests were skipped. No new real-model/GPU benchmark was performed. |
| `python build/generate-protocol-doc.py --check` | Passed | Generated protocol document matches its current generator inputs. This does not establish semantic correctness of the API. |
| `python docs/help/check-help.py` | Passed | 30 topics, 31 listed files, 138 Contents/Index entries; links/anchors resolve. It does not verify that explanatory prose matches implementation. |
| NuGet vulnerability audit, including transitive packages | No known vulnerable packages reported by configured source | Not a complete security audit; Python dependency advisories were not audited. |
| `uv build --wheel ... src/Synapic.Inference` | **Failed** | Declared package directory `synapic_inference` does not exist. See R21. |
| Isolated C# probes | Completed and reproduced findings | Metadata loss, no-op Tag, missing failure rows, false success, timeout mismatch, CUDA default, operation gating, source changes, persistence split, filtered paging, and checkout rollback. |
| FastAPI `TestClient` probes | Completed and reproduced findings | RGBA→JPEG failure, explicit overwrite bypass, unauthenticated shutdown, model-switch loading stall, mutation before validation. |

Logs: [build](../artifacts/code-review/dotnet-build.log), [.NET tests](../artifacts/code-review/dotnet-tests.log), [Python tests](../artifacts/code-review/python-tests.log), [C# probes](../artifacts/code-review/csharp-probes.log), [Python probes](../artifacts/code-review/python-probes.log), [wheel build failure](../artifacts/code-review/python-wheel-build.log), [NuGet audit](../artifacts/code-review/nuget-audit.log).

**Not verified:** live Daminion behavior/permissions, production catalog operations, installed-app update behavior under standard-user ACLs, full Windows GUI interaction, Linux/macOS runtime behavior, browser exploitation of the local sidecar, fault injection during a real disk write, or performance at collection scale. Findings below distinguish reproduced behavior from source-derived risks. An initial probe candidate about extensionless catalog downloads was **disproved**: the upscale code correctly derives a JPEG extension from the source format. It is not a finding.

## Prioritized findings

Priority denotes remediation order: **P1 High** = data safety, materially broken operation, or serious trust-boundary issue; **P2 Medium** = capability/quality gap or important robustness problem. These are not all equally urgent within a priority.

### R01 — P1: partial JPEG tagging deletes unrelated existing metadata

**Evidence:** [MetadataWriterService.cs:135](../src/Synapic.Main/Services/MetadataWriterService.cs#L135), [replacement at line 192](../src/Synapic.Main/Services/MetadataWriterService.cs#L192), [BuildXmp](../src/Synapic.Main/Services/MetadataWriterService.cs#L287).

The writer replaces an existing XMP packet with a new packet built only from the supplied fields. A keywords-only update is therefore not a patch: it removes existing XMP category/description and other XMP properties such as creator/copyright when they reside in the replaced packet. The interface also claims existing keywords are merged, but they are replaced.

**Reproduced:** write category + description + keyword `existing`, then write only keyword `new`; category and description read back absent, and only `new` remained. Field-selection tests use a capturing fake and do not exercise this destructive interaction.

**Recommendation:** read/merge the existing XMP document; explicitly distinguish unchanged, replace, merge, and clear. Preserve all unowned namespaces/properties. Add seeded-metadata preservation tests through the real writer. Even if replacing selected keywords is intentional, unselected fields must remain unchanged.

### R02 — P1: metadata writes can corrupt an original and accept invalid JPEG segment sizes

**Evidence:** [MetadataWriterService.cs:192-239](../src/Synapic.Main/Services/MetadataWriterService.cs#L192-L239), [PNG write path](../src/Synapic.Main/Services/MetadataWriterService.cs#L244).

JPEG/PNG edits modify the original file in place, writing a new packet and then shifting the remainder. An interruption, disk-full condition, or cancellation during the PNG rewrite can leave the original inconsistent. This is a source-derived crash-consistency risk; no crash was injected into a real original.

JPEG APP1 lengths are stored in 16 bits, but there is no size check before truncating the calculated length into two bytes.

**Reproduced:** a 70,000-character description returned write success, read back with description length 0, and libvips emitted `Corrupt JPEG data: 65536 extraneous bytes before marker 0xe0`. Pixel decode still succeeded in this probe; the file structure and metadata were nevertheless invalid.

**Recommendation:** stream into a unique same-directory staging file, validate packet/segment sizes and output readability, then atomically replace the original while preserving required attributes. Reject oversized basic XMP or implement extended XMP deliberately. No in-place mutation before complete validation.

### R03 — P1: dedup results are not bound to the source/catalog that produced them

**Evidence:** [source-change handler](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs#L196), [Apply](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs#L1046).

Changing folder, source type, connection, or catalog does not invalidate the old groups. Apply obtains the **current** connection but uses IDs from the **old** scan. After reconnecting to a different catalog with overlapping IDs, a delete can target unrelated records. A confirmation showing a count does not establish the identity of the catalog that was scanned.

**Reproduced:** old catalog groups remained present and Apply stayed enabled after configuring a local source. Cross-catalog deletion itself was not attempted.

**Recommendation:** attach an immutable source identity and scan generation to results. Invalidate/dispose review groups on relevant source changes and verify the exact source identity again before mutations. Snapshot targets and action semantics before confirmation.

### R04 — P1: operation exclusivity omits dedup and report work

**Evidence:** [OperationShellViewModel.cs:63-93](../src/Synapic.Main/ViewModels/Operations/OperationShellViewModel.cs#L63-L93), [CanScan](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs#L631), [navigation commands](../src/Synapic.Main/ViewModels/MainWindowViewModel.cs#L300-L335).

The shell's running predicate covers only tagging and upscaling. Dedup Scan/Apply and report Retry/Verify are excluded. Dedup CanScan does not check IsApplying; Apply can be followed by Scan while its work is in flight. Navigation is disabled in selected XAML controls rather than enforced in the navigation commands; Alt+Home executes an always-enabled command.

**Reproduced:** navigation was not locked during dedup Scan or Apply, and Scan remained executable during Apply. Source inspection establishes the command-level bypass and report-work omission.

**Recommendation:** centralize a small operation-activity coordinator/lease covering every mutating or result-producing operation, with explicit busy/cancelling states. Enforce guards inside commands and service entry points, not just UI IsEnabled bindings. Prevent Scan/Apply overlap and source changes during destructive work.

### R05 — P1: a failed catalog upscale leaves the item checked out

**Evidence:** [UpscaleItemHandler.cs:38-101](../src/Synapic.Main/Services/Processing/UpscaleItemHandler.cs#L38-L101).

Successful checkout is undone for cancellation and explicit check-in failure, but not for a missing download, an upscale returning null, or another ordinary exception before check-in.

**Reproduced against a stub:** checkout count 1, injected upscale failure, undo-checkout count 0.

**Recommendation:** use a checkout lease/finally with a committed flag; attempt a bounded rollback on **every** uncommitted exit. Distinguish unknown check-in outcome from confirmed refusal so rollback/retry cannot compound an uncertain write. Add failure-path handler tests, not just request-shape tests.

### R06 — P1: advertised inference timeouts are defeated by HttpClient's 100-second timeout

**Evidence:** [InferenceApiClient.cs:16-23](../src/Synapic.Main/Services/InferenceApiClient.cs#L16-L23), [transport creation](../src/Synapic.Main/Services/InferenceSidecarService.cs#L873).

Tag requests have a five-minute linked timeout and upscale requests a thirty-minute timeout, but the actual HttpClient retains its default 100-second timeout. The shorter transport timeout wins. The sidecar can wait 240 seconds for loading, so a cold or slow operation can be cancelled before its documented allowance.

**Reproduced:** the owned transport reports 100 seconds. No thirty-minute operation was run.

**Recommendation:** set the shared transport timeout intentionally (often infinite when every call has its own bounded CTS), then define per-endpoint deadlines for health/config as well as inference. Test timeout precedence with an injected transport and shortened budgets. Separate user cancellation from deadline expiry.

### R07 — P1: client-side filtering breaks catalog pagination

**Evidence:** [DaminionApiClient.cs:496-518](../src/Synapic.Main/Services/Daminion/DaminionApiClient.cs#L496-L518), [WorkflowRunner.cs:136-176](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs#L136-L176).

Collection/saved-search pages are filtered locally, then the runner advances the server offset by the **filtered** count and treats a filtered empty page as end-of-data. This overlaps raw pages, stops too early, and can lose qualifying records. The adjacent-page repeat guard cannot restore the lost cursor information.

**Reproduced:** a four-record raw collection with approved IDs 1 and 4 returned only ID 1; requested offsets were 0 and 1, and the filtered-empty page stopped pagination before ID 4.

**Recommendation:** return a page result carrying raw count/next cursor separately from eligible items. Advance by raw server pagination semantics, continue across empty filtered pages, and deduplicate by durable item identity. Test sparse filters, short capped pages, overlaps, and repeated pages.

### R08 — P1: registry migration/persistence is incomplete and conflicts with JSON

**Evidence:** [AppSettingsRegistryStore.cs:89-103](../src/Synapic.Main/Services/AppSettingsRegistryStore.cs#L89-L103), [SettingsViewModel.cs:80](../src/Synapic.Main/ViewModels/SettingsViewModel.cs#L80), [JSON preference persistence](../src/Synapic.Main/ViewModels/SettingsViewModel.cs#L288), [shell persistence](../src/Synapic.Main/ViewModels/MainWindowViewModel.cs#L374-L432), [startup](../src/Synapic.Main/App.axaml.cs#L30-L145).

- First load returns null immediately if the registry key is absent, so the claimed first-run JSON migration is not entered.
- General app preferences still read/write ConfigService, not the registry store passed to the shell. Dedup/upscale edits create a registry snapshot with hardcoded general defaults, and later startup switches to that snapshot.
- Local source type is not represented in AppSettingsState; source/limit values restored into AppConfig are not consistently applied to Session.
- Startup applies theme and constructs telemetry before overlaying restored registry settings.
- Ctrl+S reconstructs UiSettings without ShowDiagnostics/default device/default threshold/default max items, resetting those known fields.

**Reproduced:** a theme edit created no registry state; changing an upscale parameter created a registry snapshot whose theme was `system`. Ctrl+S changed diagnostics true→false, device cuda→empty, confidence 0.6→0, and max items 250→0.

**Recommendation:** one typed settings repository, one effective startup snapshot, and explicit schema-versioned migrations. Choose registry on Windows with an intentional file backend elsewhere. Apply the snapshot before logging/theme/telemetry/session creation. Use field-scoped updates instead of multiple whole-state writers and duplicated positional defaults. Add restart/migration tests with injected paths and registry keys.

### R09 — P1: failed tagging items disappear, and retry uses filenames instead of identities

**Evidence:** [TaggingItemHandler](../src/Synapic.Main/Services/Processing/ProcessingOrchestrator.cs#L240-L350), [result contract](../src/Synapic.Main/Services/Processing/ProcessingOrchestrator.cs#L7), [Step4ResultsViewModel.cs:90-114](../src/Synapic.Main/ViewModels/Steps/Step4ResultsViewModel.cs#L90-L114).

A thrown download/inference failure is counted by the kernel but never appended as a result row. Retry therefore cannot locate many actual failures. ProcessItemResult lacks a local path or run/source identity. Retry matches FileName, refetches the current selection, and removes old entries by filename alone—including successful records with the same basename. It removes entries before establishing replacement outcomes.

**Reproduced:** an injected tag failure produced `0 ok, 1 failed` but **zero result rows**. Duplicate-name retry consequences are source-derived.

**Recommendation:** durable WorkItemId + original source snapshot/path + typed outcome for every attempted item, including failure/cancel. Retry original failed identities, update rows transactionally per item, and preserve original errors if retry is cancelled or selection changes.

### R10 — P1: success reporting is inconsistent and sometimes false

**Evidence:** [ProcessingOrchestrator.cs:339](../src/Synapic.Main/Services/Processing/ProcessingOrchestrator.cs#L339), [DaminionApiClient.cs:967-1022](../src/Synapic.Main/Services/Daminion/DaminionApiClient.cs#L967-L1022), [version-control response handling](../src/Synapic.Main/Services/Daminion/DaminionApiClient.cs#L1149-L1251).

Metadata write failure is deliberately returned as a successful workflow outcome, disagreeing with the results row. Daminion BatchChange discards the response body, so an HTTP 200 application-level refusal can be reported as success. Missing GUIDs can silently drop requested fields while other operations still succeed. Checkout/check-in similarly equate HTTP success with operation success; their actual application-level response shapes need confirming against the supported server versions.

**Reproduced:** failed writer → row `Write Failed` but batch `1 ok, 0 failed`; stub BatchChange `{success:false,error:"denied"}` → true.

**Recommendation:** typed outcomes representing requested/applied fields and explicit partial failure; parse server envelopes and report unknown states honestly. Count a failed required write as failure everywhere. Use verification where an API acknowledges requests without proving application.

### R11 — P1: installed sidecar updates target protected application files and cannot keep variants separate

**Evidence:** [SidecarInstallPath](../src/Synapic.Main/Services/InferenceSidecarService.cs#L242-L273), [installer directory/privileges](../build/installer-windows.iss#L36-L48), [download move](../src/Synapic.Main/Services/SidecarDownloadService.cs#L145).

Outside a checkout, every RID resolves to the same executable beside the app. The default installer puts it under Program Files; an ordinary user cannot stage/replace it there. CPU and CUDA downloads also map to the same filename, so variants overwrite each other and detection cannot reliably distinguish them.

**Source-derived; not reproduced in an installed standard-user session.**

**Recommendation:** writable per-user versioned directories per RID, with a small manifest identifying variant, version, protocol compatibility and checksum. Keep the bundled binary as an immutable fallback. Test the installed ACL path, not only writable test folders. Prefer recorded build/release identity over filesystem modification time for update decisions.

### R12 — P1: the local sidecar has no caller authentication or host/origin checks

**Evidence:** [FastAPI application](../src/Synapic.Inference/service.py#L337), [shutdown endpoint](../src/Synapic.Inference/service.py#L720), [host option](../src/Synapic.Inference/service.py#L752), [file-path endpoints](../src/Synapic.Inference/service.py#L470).

The API can infer over caller-supplied local paths, download models, mutate configuration, write upscale outputs, and terminate the process without authentication. Loopback binding reduces exposure but does not identify a trusted caller. A manual `--host=0.0.0.0` broadens this substantially. No CORS middleware is needed for local-native callers; browser exploitation is a separate, environment-dependent concern that was not verified.

**Reproduced in TestClient:** an unauthenticated shutdown request with a foreign Origin returned 200; the actual exit function was safely replaced for the probe.

**Recommendation:** random per-launch capability token conveyed through a private startup channel and required on every privileged route; reject untrusted Host/Origin, bind loopback by policy, constrain write destinations as appropriate, and document the local trust model. Test missing/wrong tokens, foreign origin/host, and output-path abuse. Do not claim checksums from the same release source provide publisher authentication; consider signing/attestation as a separate hardening step.

### R13 — P2: model config switching leaves a permanent loading state

**Evidence:** [service.py:703-711](../src/Synapic.Inference/service.py#L703-L711), [unload_model](../src/Synapic.Inference/model_loader.py#L731), [tag readiness wait](../src/Synapic.Inference/service.py#L470-L500).

Changing model_id via PUT /config unloads and sets status to loading, but starts no load. The next /tag waits for loading to end **before** calling the loader that would end it. An invalid combined model/task update can unload first and then return 422, mutating state on rejection. Cached pipelines also lose their resolved task: cache hits return the requested task instead of the task used when construction adapted it.

**Reproduced:** valid config switch → loading with no load in progress; rejected task update → 422 but still loading. Cache task mismatch is source-derived.

**Recommendation:** validate the entire proposed config before effects, represent idle/unloaded separately from loading, and store resolved task alongside pipeline. Encapsulate config/cache/state in a small runtime object with coherent transitions and an explicit concurrency policy.

### R14 — P2: local duplicate Tag is a no-op presented as an available action

**Evidence:** [DedupService.cs:249-253](../src/Synapic.Main/Services/Processing/DedupService.cs#L249-L253), [offered actions](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs#L460), [help acknowledgement](help/dedup.html).

Tag only logs a message and returns success. Apply then removes affected review groups as though something happened. The help now honestly says it is unimplemented, but the enabled default UI action still gives a misleading completion signal.

**Reproduced:** true return, identical before/after bytes.

**Recommendation:** disable/remove the option until implemented, or implement a clearly specified reversible marker through the real metadata writer. Never report a placeholder as a successful mutation.

### R15 — P2: dedup algorithms/options do not consistently implement their advertised semantics

**Evidence:** [hash preparation/options](../src/Synapic.Main/Services/Processing/DedupService.cs#L282-L300), [hash implementations](../src/Synapic.Main/Services/Processing/DedupService.cs#L380-L430), [threshold conversion](../src/Synapic.Main/Services/Processing/DedupService.cs#L563), [algorithm help](../docs/help/dedup.html).

- Grayscale conversion preserves alpha; hashing assumes one byte per pixel. PHash writes more than 1,024 values into a 1,024-value matrix for RGBA input; AHash/DHash consume interleaved channel bytes as pixels.
- ColorMoment dispatches to grayscale AHash while help advertises a colour-specific algorithm.
- MaxDimension is calculated into an unused local; a constant 64-pixel preparation is used instead.
- Math.Round can accept distances outside the requested similarity (e.g. threshold 0.91 → distance 6 → similarity 0.90625). Define the boundary deliberately and test it.
- Claimed compatibility with a Python hash implementation lacks golden cross-implementation vectors; the pHash comment's claimed DC treatment should be checked against the exact intended reference.

**Reproduced:** an RGBA PNG was unhashable with default PHash; AHash/DHash returned values but are channel-misaligned by inspection.

**Recommendation:** normalize orientation, colour space, sample type, and alpha explicitly; remove unsupported algorithms/options or implement them truthfully. Add real fixture/golden-vector and threshold-boundary tests before changing grouping behavior.

### R16 — P2: PNG/TIFF metadata lifecycle is incomplete

**Evidence:** [PNG write path](../src/Synapic.Main/Services/MetadataWriterService.cs#L244), [TIFF sidecar write](../src/Synapic.Main/Services/MetadataWriterService.cs#L152-L163), [reader](../src/Synapic.Main/Services/MetadataWriterService.cs#L32), [dedup file actions](../src/Synapic.Main/Services/Processing/DedupService.cs#L211-L245).

PNG writes insert another XMP chunk on every run rather than replacing/merging the existing one. TIFF writes a sidecar, but the application's reader does not consult it; move/delete actions operate on only the image and leave a sidecar detached or orphaned.

**Reproduced:** second PNG write grew the file by another 720 bytes; TIFF write created `.xmp`, but readback returned no category. Sidecar move/delete consequences are source-derived.

**Recommendation:** make format adapters share a documented metadata patch contract; replace the owned PNG chunk without duplicates, read TIFF sidecars, and treat image plus associated sidecars as one managed artifact where appropriate.

### R17 — P2: bounded execution is not bounded scheduling; scans are not fully cancellable

**Evidence:** [WorkflowRunner.cs:104-121](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs#L104-L121), [task fan-out](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs#L233-L289), [grouping](../src/Synapic.Main/Services/Processing/DedupService.cs#L467), [local dedup enumeration](../src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs#L709).

The semaphore bounds active handlers, but one Task.Run is created per item and all are materialized. All work items are fetched before execution. Dedup grouping is O(n²), has no cancellation token, and local dedup enumeration happens synchronously on the command/UI path, always recursively, ignoring the shared recursion choice. Some grouping continuations run on the UI thread. These are source-derived scaling/responsiveness risks, not measured throughput regressions.

**Recommendation:** a bounded worker loop/channel or Parallel.ForEachAsync; cancellable enumeration and grouping off the UI thread; explicit memory and collection-size budgets. Profile representative 1k/10k/100k workloads before choosing an index. The naive four-loop DCT and repeated cosine/table work are candidates to measure, not grounds to promise a speedup. Specify whether connected-component/transitive duplicate grouping is intentional before optimizing it.

### R18 — P2: cancellation and resource ownership are inconsistent

**Evidence:** [broad Daminion catches](../src/Synapic.Main/Services/Daminion/DaminionApiClient.cs#L626-L664), [owned HttpClient](../src/Synapic.Main/Services/Daminion/DaminionApiClient.cs#L55-L79), [disconnect](../src/Synapic.Main/ViewModels/Steps/Step1DatasourceViewModel.cs#L642-L654), [sidecar start](../src/Synapic.Main/Services/InferenceSidecarService.cs#L422-L468), [stop](../src/Synapic.Main/Services/InferenceSidecarService.cs#L795), [shutdown](../src/Synapic.Main/App.axaml.cs#L221-L238).

OperationCanceledException is swallowed by several broad catches and converted to null/false/network failure. DaminionApiClient creates an owned HttpClient but is not disposable; disconnect starts logout without awaiting it. Sidecar start checks state under a lock but reserves Starting later, leaving a double-start race. Startup/stop races, untracked fire-and-forget work, disposable CTS instances, event subscriptions, the DI container, and platform-specific orphan cleanup lack one explicit lifecycle owner. Job-object crash cleanup is Windows-only despite broader comments.

**Source-derived; no live lifecycle fault campaign performed.**

**Recommendation:** propagate cancellation unless explicitly handling a bounded cleanup; await logout; dispose owned clients/CTS/subscriptions; serialize start/stop through a lifecycle gate and cancellation generation; perform orderly shutdown of active operations before infrastructure. Use an explicit single-flight reauthentication policy and ensure session recovery also examines HTTP-response-returning Refit methods, which do not necessarily throw on 401/403.

### R19 — P2: upscale output conversion and overwrite policy have holes

**Evidence:** [image saves](../src/Synapic.Inference/upscaler.py#L240), [fast save](../src/Synapic.Inference/upscaler.py#L270), [output resolver](../src/Synapic.Inference/upscaler.py#L316-L345).

RGBA input requested as JPEG is not converted/composited and fails. overwrite_existing=False is enforced only for autogenerated output paths; an explicit existing path is overwritten. Saves are direct rather than staged. Image orientation, ICC/EXIF preservation, metadata loss when replacing a catalog original, and the distinction between model-input capping and requested output scale need documented policies and tests.

**Reproduced:** RGBA→JPEG returned 500; explicit existing path with overwrite disabled changed from 3×3 to 16×16.

**Recommendation:** normalize pixels/alpha for target format, enforce overwrite policy for all destinations, prohibit accidental input replacement, stage output atomically, and define metadata/orientation preservation. Manage model residency/device policy across tagging and upscaling deliberately rather than retaining every upscaler model indefinitely.

### R20 — P2: CUDA preference is unreachable for an unspecified default

**Evidence:** [default selection](../src/Synapic.Main/ViewModels/SettingsViewModel.cs#L254-L267), [empty-value mapping](../src/Synapic.Main/ViewModels/SettingsViewModel.cs#L341-L347).

An empty persisted device maps to option index 0 (CPU), whose Value is never empty. The subsequent `device.Length == 0` CUDA-seeding branch cannot run. This contradicts the method documentation and the earlier requirement to prefer CUDA when available.

**Reproduced:** fresh settings + injected CUDA capability still seeded `cpu`.

**Recommendation:** represent Auto/Unspecified explicitly rather than conflating it with CPU. Preserve an explicit CPU choice, prefer supported acceleration only when unspecified, and use deterministic capability probes in tests. Capability detection itself should not label every macOS machine MPS-capable solely from OS family.

### R21 — P2: the declared Python package cannot be built

**Evidence:** [pyproject.toml](../src/Synapic.Inference/pyproject.toml).

Setuptools is told to package `synapic_inference`, and the entry point imports `synapic_inference.service`, but sources are a flat directory with flat imports. There is no such package directory. Lower-bound dependency metadata also diverges from the frozen build pins.

**Reproduced:** wheel build failed with `package directory 'synapic_inference' does not exist`. This does **not** prove that the separately tested PyInstaller build is broken.

**Recommendation:** either support a real installable package with a tested entry point and one import identity, or remove misleading distribution metadata and document the frozen/script-only interface. Keep one dependency/version source with repeatable resolution for the chosen build path.

### R22 — P2: test/CI coverage makes missing verification look like success

**Evidence:** [early-return live tests](../tests/Synapic.Main.Tests/EndToEndRoundTripTests.cs#L115-L128), [metadata tests](../tests/Synapic.Main.Tests/MetadataWriterTests.cs), [Linux-only .NET test job](../.github/workflows/build.yml#L75-L89), [release workflow](../.github/workflows/release.yml).

- Live Daminion and platform/environment gates return from Facts instead of reporting real skips. This review's live Daminion round trip was reported Passed in about 1 ms despite absent credentials.
- Metadata coverage has only two JPEG tests; no seeded-metadata preservation, repeated PNG, TIFF sidecar readback, oversized packet, or interrupted-write tests.
- No existing tests reference AppSettingsRegistryStore; no full restart/migration test catches the persistence split.
- Navigation-lock coverage checks tagging, not dedup Apply/Scan/report work.
- Six AI-upscale tests are skipped in the lightweight Python suite. The real local sidecar test here serializes inference, so it does not validate the app's normal four-way shared-pipeline execution.
- The build workflow runs .NET tests on Linux; Windows registry tests return early there. Packaging Windows is not equivalent to running those tests.
- The release workflow has packaging guards and bundle smoke tests, but does not itself run the full .NET/Python suites or depend on a successful test job for the exact release commit.

**Recommendation:** explicit skips with reasons; separate hermetic, platform and opt-in live tiers; cheap Windows registry tests in CI; release gating on tests of the exact commit; behavior-focused failure-path tests. Convert the disposable review probes into permanent regression tests when fixing their associated issues. Do not inflate test counts with early returns or weaken assertions to get green builds.

## Object-oriented design: what to keep and what to change

### Keep the useful abstractions

- [WorkflowRunner / IWorkflowItemHandler](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs) is a strong composition point. Keep a shared execution policy; improve scheduling and outcome contracts rather than copying loops back into each workflow.
- [RunStateViewModel](../src/Synapic.Main/ViewModels/Operations/RunStateViewModel.cs) captures a genuinely shared lifecycle. Do not turn it into a service locator or a giant feature base class.
- [wire contracts](../src/Synapic.Shared/Contracts/Contracts.cs) and source-generated serialization separate protocol from implementation effectively.
- [IMetadataWriter](../src/Synapic.Main/Services/MetadataWriterService.cs) and IDedupService are test seams worth keeping, after their contracts become accurate.

### Recommended boundaries

A small target architecture:

```text
Views → focused presentation view models → application use cases
                                          ↓
                  typed domain requests/results + source identities
                                          ↓
               infrastructure adapters (catalog / files / sidecar / settings)
```

**1. Keep construction at the composition root.**

[MainWindowViewModel](../src/Synapic.Main/ViewModels/MainWindowViewModel.cs) has 12 constructor parameters, constructs SettingsViewModel/operation graphs, falls back to concrete services, and resolves App.Services. [OperationShellViewModel](../src/Synapic.Main/ViewModels/Operations/OperationShellViewModel.cs) constructs the rest. Move factories/registrations to application startup. Inject the dependencies actually used; do not add an interface for every trivial class.

**2. Split by responsibilities, not line counts.**

The largest files are DaminionApiClient (1,434 lines), StepDedupViewModel (1,183), model_loader (1,160), MainWindowViewModel (1,104), InferenceSidecarService (948), Step1DatasourceViewModel (880), and Step2EngineViewModel (836).

Suggested focused collaborators:

- catalog session/auth + querying/paging + metadata + version control/download leases;
- sidecar process lifecycle separate from executable discovery/update installation;
- dedup scan use case + review selection model + action execution;
- source configuration/connection coordinator separate from presentation/count display;
- one settings repository with platform adapters and migrations.

Do not replace a large class with many pass-through abstractions. Each extraction should own a state/invariant or isolate an external side effect.

**3. Replace primitive state ambiguity with small typed models.**

Use stable source/work-item identities, typed outcomes and action options, immutable per-run requests, and page results with a cursor. Avoid persisted dropdown indices, giant positional settings records, status strings used as business logic, and records that allow both local path and catalog ID to be absent or simultaneously present. Plain immutable records plus enums are sufficient; a deep inheritance hierarchy is unnecessary.

**4. Avoid view-model-to-view-model service access.**

Results currently reaches Step3.Sidecar and constructs an orchestrator; dedup/upscale depend on Step1 for infrastructure. Share a source/session application abstraction, not another screen's implementation. Keep Avalonia bitmap creation and presentation formatting in the presentation boundary while scan/action semantics stay independent.

**5. Make runtime state explicit in Python.**

A small application-scoped inference runtime can own session config, active pipeline/resolved task, downloads and load state. Inject it into routes and lifecycle hooks; minimize module-global cross-test state. Split route DTOs from execution without losing flat/frozen entry-point compatibility until packaging is intentionally repaired. Agree and test the shared-pipeline concurrency policy before introducing GPU parallelism changes.

**6. Simplify adapters only after checking their value.**

[operation adapters](../src/Synapic.Main/ViewModels/Operations/OperationAdapters.cs) broadly re-raise property changes and largely wrap existing view models. They may remain useful for the shared template, but prune properties/events that have no consumers. Prefer one current operation and one activity model over another set of mirrored flags. Do not add a generic repository, mediator, CQRS layer, or service-locator framework solely for appearances.

## Documentation assessment

There are many comments and docs; the issue is **accuracy and duplication**, not volume.

- [csharp-reference.md](csharp-reference.md) describes removed WizardViewModel/navigation/dialog interfaces and files. [codebase-guide.md](codebase-guide.md) begins with the newer shell but retains old wizard startup/concurrency/persistence descriptions. The current tracked tree no longer contains WizardViewModel or EngineSettingsDialog.
- [MainWindowViewModel's class summary](../src/Synapic.Main/ViewModels/MainWindowViewModel.cs#L33) still describes Build controls and adoption of running servers, conflicting with download-first/owned-server behavior.
- [MetadataWriterService's contract](../src/Synapic.Main/Services/MetadataWriterService.cs#L15) claims merging, embedded TIFF writes and compatible EXIF fields that the implementation does not provide.
- [WorkflowRunner interface documentation](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs#L41-L49) opens a summary and closes remarks. XML documentation generation is disabled in [Directory.Build.props](../Directory.Build.props), so this defect is not caught by normal compilation.
- Help link checks pass even when semantics are wrong: ColorMoment is one example. Local Tag's unimplemented behavior is documented, which is good, but should also be reflected in availability.
- Comments repeatedly recount past phases, user requests, old bugs and deleted flows. Those histories belong in concise ADRs/issue references, not every public member's XML summary.

**Recommendation:** one short current architecture guide, an authoritative persistence contract, an operation safety/trust model, and a test-tier guide. Keep generated protocol reference. Archive obsolete UI/phase references clearly; remove contradictory current documentation. XML summaries should state behavior and invariants, with parameters/returns/exceptions/ownership/cancellation/threading where meaningful—not narrate every implementation line.

Enable documentation generation and relevant diagnostics gradually, fixing actual defects rather than globally suppressing them or adding boilerplate to every accessor. Add formatting/analyzer conventions, Python linting/type checks, and CI enforcement incrementally; there is currently no tracked .editorconfig, Ruff or mypy/pyright configuration. The existing warning-as-error baseline is a useful foundation.

## Additional hardening opportunities (not independently ranked blockers)

- Protect spreadsheet exports from formula interpretation and handle all CSV edge cases (CR, invariant numeric formatting). [Csv](../src/Synapic.Main/ViewModels/Steps/Step4ResultsViewModel.cs#L78) only escapes quotes/comma/LF and exports model-controlled text.
- Daminion defaults scheme-less URLs to HTTP and sends login credentials in query parameters per its API convention. Explain insecure transport visibly, encourage HTTPS, and redact URLs/query credentials from diagnostics.
- [temporary downloads](../src/Synapic.Main/Services/Daminion/DaminionApiClient.cs#L643) are named by item ID/kind in a shared directory; different catalogs/app instances can collide. Use unique per-client/run owned paths and narrowly scoped cleanup.
- Mutation APIs return one bool for an entire partial batch. Per-target outcomes would let the review UI remove only completed rows and make cancellation honest.
- Dedup review loses SimilarityScores when constructing view models, and transitive connected components can contain pairs below the chosen threshold. Show the actual relationship/score and retain a safe keeper invariant before destructive actions.
- Moving to a newly typed folder is blocked by Directory.Exists even though the implementation promises to create it. Destination changes do not explicitly notify ApplyCommand's gate. Path containment uses string normalization, not symlink/junction identity, and trims root separators; test volume-root and case-sensitive paths.
- Runtime provisioning is mixed into GUI startup. Avalonia targets .NETCore rather than requiring WindowsDesktop; keep genuine launch prerequisites in the installer/bootstrap and validate supported framework major/architecture. A higher major version alone is not proof the required runtime will roll forward.
- [release workflow](../.github/workflows/release.yml) gives broad write permissions; narrow them to publishing jobs, pin important actions/provenance, and separate nightly from stable-channel policy.

## Streamlining plan

### Stage 1 — establish safety and truthful outcomes

Fix R01/R02 metadata preservation and staging; R03/R04 source-bound dedup and mutual exclusion; R05 rollback; R06 deadlines; R07 paging; R09/R10 result identity and success semantics. Disable no-op Tag until real. Introduce regression tests alongside each repair. These are small bounded slices; avoid bundling unrelated refactors.

**Acceptance gate:** seeded metadata survives partial updates, failed originals remain untouched, an unsuccessful checked-out item is released or clearly reported unresolved, all attempted items have truthful outcomes, no cross-source mutations, no overlapping scan/apply, sparse paging returns the correct IDs, and documented timeouts are effective.

### Stage 2 — one settings truth and one installed-update truth

Repair migrations/backends/startup order, durable identities/defaults, writable variant installation, and restart behavior. Use typed schemas and recorded release identity. Add Windows registry/restart/installed-ACL tests. Address CUDA Auto semantics here.

**Acceptance gate:** a configuration survives a restart without JSON/registry disagreement or implicit resets; downloaded CPU/CUDA variants are independently discoverable and runnable as a normal installed user.

### Stage 3 — simplify the application boundary

Extract focused use cases/collaborators from shell/source/dedup/catalog classes and wire through DI at startup. Give operations a coherent state machine and lifecycle ownership; repair Python config/load transitions and caller authentication. Preserve the shared kernel and existing UI interface while moving side effects out of view models.

**Acceptance gate:** core operation tests do not need a global App service provider, another screen's view model, actual registry, or real GPU; lifecycle transitions and invalid states are directly testable.

### Stage 4 — documentation and quality gates

Rewrite the current architecture/persistence references, retire obsolete references, fix XML comments, support or remove Python packaging, enable format/lint/type/docs diagnostics gradually, make skips honest, and gate releases on exact-commit tests. Add a supported OS/format/failure-mode matrix.

### Stage 5 — measure scale before optimizing

Benchmark enumeration, task scheduling, DCT, grouping, memory, and inference on representative collections/devices. Optimize the measured bottlenecks while checking output equivalence and cancellation. No speedup is claimed by this review.

## First pause

Stop here before application changes. My recommended first implementation slice is **metadata safety plus regression tests**, followed by **source-bound destructive operations and checkout recovery**. The full architectural cleanup should follow those safety fixes rather than precede them.
