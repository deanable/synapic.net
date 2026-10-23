# Phase 6: Work detection and durable job queue - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning
**Source:** `docs/windows-indexer.md` §4, §5.2, §6 (scan → enqueue), §8

<domain>
## Phase Boundary

Detect work and remember it. Local interval scans, a debounced filesystem watcher and Daminion untagged queries
all produce rows in one SQLite queue, which survives restarts and knows which items are pending, running,
transient or permanently failed. Processing those rows is phase 7 — this phase proves the queue fills correctly
and never double-enqueues.

</domain>

<decisions>
## Implementation Decisions

### Queue (spec §5.1 state, §8 recovery)
- **D-11-06 (locked):** SQLite via `Microsoft.Data.Sqlite` 10.0.12, database at
  `%ProgramData%\Synapic\state\indexer.db`, WAL mode, `PRAGMA busy_timeout=5000`. One connection per operation
  (pooled), writes serialized behind a `SemaphoreSlim`, and a transactional `BEGIN IMMEDIATE` for claiming.
- **Schema v1:** `jobs(id INTEGER PK, key TEXT UNIQUE NOT NULL, source TEXT, path TEXT, daminion_id INTEGER,
  file_name TEXT NOT NULL, size INTEGER, mtime_utc TEXT, content_hash TEXT, state TEXT NOT NULL, attempts INTEGER
  NOT NULL DEFAULT 0, last_error TEXT, enqueued_at TEXT NOT NULL, updated_at TEXT NOT NULL, next_attempt_at TEXT
  NULL)` + `job_events(id, job_id, at, from_state, to_state, detail)` appended on every transition. States:
  `Pending`, `InFlight`, `Completed`, `TransientFailure`, `Failed`. A `JobStateMachine` permits only:
  Pending→InFlight, InFlight→Completed|TransientFailure|Failed|Pending(recovery), TransientFailure→Pending|Failed,
  Failed→Pending (only when the fingerprint changed). Everything else throws in tests.
- **Key/idempotency:** `key = "local|" + canonicalPath` or `"daminion|" + id`. `EnqueueAsync` upserts by key:
  no-op for Pending/InFlight; no-op for Completed with an unchanged fingerprint; Completed-changed → `Pending`;
  Failed with unchanged fingerprint → no-op (a corrupt file must not be retried forever), Failed-changed → `Pending`.
  The unique key is the guarantee; the scheduler never holds an in-memory copy.
- **Fingerprint:** size + last-write UTC + SHA-256 of content. The scan compares size/mtime first; the hash is read
  only when they differ, so an unchanged file costs one stat, not a hash. If the hash matches, the row's
  mtime is updated and nothing is enqueued.
- **Startup recovery:** `UPDATE jobs SET state='Pending', updated_at=... WHERE state='InFlight'` runs once before
  any scan; `JobStateMachine` records the transition in `job_events` with detail `"service restart"`.

### Detection
- **D-11-15:** Local scan uses `Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories
  = watchRecursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })` and a fixed
  extension set `{ .jpg, .jpeg, .png, .tif, .tiff }` (case-insensitive) — the spec's list, not a config key.
  Access-denied subtrees are logged at Warning and skipped; one unreadable folder must not end a scan.
- **D-11-16:** Watcher: one `FileSystemWatcher` per root, `NotifyFilter = FileName | LastWrite | Size`,
  `InternalBufferSize = 64 KB`, `IncludeSubdirectories = watchRecursive`. Events go into a `DebounceBuffer`
  keyed by canonical path with a per-path due time (`now + debounceSeconds`, default 5, injected clock). A path
  releases only when no newer event arrived and `FileStabilityProbe` can open it `FileShare.None`; a failed probe
  re-arms for 2 s (max 30 attempts, then logs and leaves the file to the next interval scan).
  Buffer overflow (`Error` event) triggers a full scan instead of pretending no files changed.
- **D-11-17:** Daminion work is **not** a new query implementation: build a `DatasourceSelection` from config
  (`IsDaminion = true`, `DaminionClient = new DaminionApiClient(serverUrl, username, decryptedPassword)`,
  `Scope`, `UntaggedFields`, `StatusFilter = "all"`, `MaxItems = 0`) and call
  `WorkflowRunner.FetchItemsAsync` — the same path the desktop uses, so untagged semantics cannot drift.
  Scope `"all"`/saved search/collection ids map exactly as the desktop maps them.
- **D-11-18:** `ISidecarActivity` (phase 5) is implemented here by the queue: `HasPendingWork` = any
  Pending/InFlight/TransientFailure row, `LastWorkAt` = the newest `updated_at` of those rows. This is what the
  idle timeout reads; no separate heartbeat file.

### Scheduling and observability
- **D-11-19:** `ScanScheduler : BackgroundService` runs an immediate scan at startup, then every
  `intervalMinutes`; watch mode answers events directly. A scan and a watcher flush share one enqueue path
  (`IWorkIntake.EnqueueAsync(IEnumerable<JobCandidate>)`), so idempotency has one implementation.
- **D-11-20:** `Synapic.Indexer.exe --status` opens the database read-only and prints counts per state plus the
  five most recent failures (id, key, error). It must not start the host, scan, or write.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Specification
- `docs/windows-indexer.md` §4.1–4.2 — interval scan, watcher debounce 5 s, exclusive-read probe
- `docs/windows-indexer.md` §5.1 — storage expectations, §5.2 schedule block
- `docs/windows-indexer.md` §8 — the failure table the state machine encodes (lock → transient, corrupt → failed,
  reboot → requeue InFlight)
- `docs/windows-indexer.md` §9.2 — unattended detection is the acceptance criterion

### Code
- `src/Synapic.Core/Services/Processing/WorkflowRunner.cs` — `FetchItemsAsync` (L165+), Daminion paging,
  `IsLocalImage`, `CountLocalImages`
- `src/Synapic.Core/Services/Processing/ProcessingOrchestrator.cs` — `DatasourceSelection` and `ProcessWorkItem`
- `src/Synapic.Core/Services/Daminion/DaminionApiClient.cs` — ctor L55, `IsAuthenticated`, `GetItemsFilteredAsync`,
  `GetItemsPageAsync`
- `src/Synapic.Indexer/Hosting/ISidecarActivity.cs` (phase 5)
- `src/Synapic.Main/Services/Daminion/DaminionConnectionStore.cs` — how the desktop decrypts credentials
  (the indexer's `DpapiCredentialProtector` is the service-side equivalent)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `WorkflowRunner.FetchItemsAsync(DatasourceSelection)` already implements the Daminion untagged/enumeration query,
  pagination and item mapping — using it is the entire point of phase 4.
- `ProcessWorkItem` already carries `LocalPath`, `DaminionId`, `FileName` and the server content hash when present.
- `DatasourceSelection` carries `Scope`, `SavedSearchId`, `CollectionId`, `UntaggedFields`, `StatusFilter`.

### Established Patterns
- Time is injected in this codebase's tests (dedup/ETA tests avoid wall-clock sleeps); follow that for debounce
  and retry timing.
- Windows paths are compared case-insensitively; `StringComparer.OrdinalIgnoreCase` is the project's norm.
- SQLite is new to this repo: no EF, no ORM — raw parameterized SQL in one repository class, schema created with
  `CREATE TABLE IF NOT EXISTS` plus a `schema_version` pragma/user_version for migrations.

### Integration Points
- Phase 7's executor claims from this queue (`ClaimBatchAsync`) and writes results back through
  `MarkCompleted/MarkTransient/MarkFailed`.
- Phase 5's idle timer consumes `ISidecarActivity` implemented here.
- Phase 8's acceptance drops a file into a watched folder and then reads `--status` to prove detection.

</code_context>

<deferred>
## Deferred Ideas

- Multiple local roots with different settings each (v1.1 shares one schedule block)
- A watched-folder filter (include/exclude globs) — no requirement yet
- Daminion saved-search/collection config beyond the existing scope fields
- Queue compaction/retention policy; `job_events` grows until a later milestone adds pruning
- Priority ordering by file age/size — FIFO by `id` is sufficient

</deferred>

---

*Phase: 06-work-detection-and-durable-job-queue*
*Context gathered: 2026-10-22*
