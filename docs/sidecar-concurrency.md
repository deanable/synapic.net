# Sidecar Concurrency

The question this file answers: **when `synapic-inference` is running and
multiple calls are sent to it, what runs concurrently, what gets serialized,
and where each limit lives.** It deep-dives the concurrency story that
[`codebase-guide.md`](codebase-guide.md) §6 summarizes, alongside
[`sidecar-reference.md`](sidecar-reference.md) and the wire contract in
[`sidecar-protocol.md`](sidecar-protocol.md).

```
Host (C#)                                       Sidecar (Python, one uvicorn process)
Step 3 batch                                    POST /tag  (sync def)
  ProcessingOrchestrator                          -> Starlette AnyIO threadpool
  SemaphoreSlim(4): <= 4 items in flight            (one thread per in-flight request)
       |                                             |
       +--  HttpClient POST /tag ----------------->  +- _wait_for_model_ready (poll <= 240 s)
                                                     +- model_loader.load_model
                                                     |    cache hit  -> share cached pipeline
                                                     |    cache miss -> _model_build_lock
                                                     |                 (one build at a time)
                                                     +- inference_engine.run_inference
                                                          on the shared pipeline -- NO lock
```

Reading the diagram top-down:

| Layer | Limit | It throttles |
|-------|-------|--------------|
| `ProcessingOrchestrator.SemaphoreSlim(4)` | C# | Items **entering the batch pipeline** (image fetch -> `/tag` -> metadata write), not just inference |
| AnyIO worker threadpool (default 40) | Python | How many request handlers run at once; effectively unbounded for a 4-wide fan-out |
| `model_loader` locks | Python | Pipeline **construction** only — never generation |
| (nothing) | — | Generation: concurrent `run_inference` calls share one pipeline object |

The sidecar service layer serializes nothing: `InferenceSidecarService.TagAsync`
and friends are pass-throughs to `InferenceApiClient`, each an independent
request. Every concurrency decision visible here was made elsewhere —
deliberately, in the orchestrator and in `model_loader`.

---

## 1. Process and port lifecycle (one sidecar, one port)

`InferenceSidecarService` owns exactly one child process per app instance.

- **State is guarded by a single gate.** `_process`, `_status`, `_port` and the
  liveness CTS live behind one `object _gate` lock. `StartAsync` is idempotent:
  a second call while `Starting`/`Ready` short-circuits inside the lock, so two
  UI entry points cannot race two sidecars into existence.
- **Port discovery is a handshake, not a guess.** The host launches with
  `--port=0` and `SYNAPIC_PORT_FILE=%TEMP%\synapic_port_{hostPid}.txt`; the
  sidecar pre-binds its socket (so the OS-assigned port is known before
  uvicorn serves), writes `port\npid\n`, and the host polls for the file
  every 200 ms (30 s deadline), validating the pid. Then it polls `/health`
  every 500 ms up to 120 s until `ready`; a JSON `error` status is fatal
  for startup.
- **Client rebinding.** `HttpClient.BaseAddress` cannot change after first
  request, and the wizard UI polls `/health` during `Starting`, so
  `ConfigurePort` swaps in a fresh `HttpClient` and `InferenceApiClient`
  instead of mutating them (see [rough edges](#4-rough-edges-ranked) — the
  swap itself is not synchronized).
- **Orphan-free lifetime.** On Windows the child joins a Job Object
  (`ProcessJob.AssignChild`, kill-on-job-close): app death by any means kills
  the sidecar. Normal `StopAsync` is gentler: `POST /shutdown` -> 5 s
  `WaitForExit` -> `Kill(entireProcessTree: true)` fallback, then the port
  file is deleted. A background watcher (`WatchProcessExit`) flips status to
  `Error` when the process dies while `Starting`/`Ready`.
- **Hygiene, not safety.** `SweepStalePortFiles` removes leftover port files
  whose pid is dead. Since adoption never happens, this only keeps `%TEMP%`
  and logs meaningful.

## 2. Host-side fan-out (why the host bounds itself)

The only outbound throttle lives in `ProcessingOrchestrator`:

- `items.Select(Task.Run(...))` fans out per item, gated by
  `SemaphoreSlim(_maxDegreeOfParallelism)` — **4**: hardcoded in
  `Step3ProcessViewModel`; the Step 4 "retry failed items" path constructs a
  fresh orchestrator per click and gets the constructor default, also 4.
- The semaphore slot is held across the **entire per-item pipeline** —
  Daminion download, `/tag`, metadata write — so the cap governs concurrency
  end to end, not only the inference call.
- **Pause** (`PauseToken`) blocks items before they start; running items
  finish. **Abort** (`CancellationToken`) wins over pause. The shared
  processed/failed counters and progress reporting are serialized by a
  per-run `resultsLock`.
- Timeouts/retries are the failure backstop rather than a throttle
  mechanism: `InferenceApiClient` allows **5 minutes** per `/tag` (one
  request *including* its retry) and retries once after 3 s on `503`; the
  server-side `MODEL_LOAD_WAIT_SECONDS = 240` is sized to stay under that.

### The cap is per run, not per process

Each orchestrator instance owns its own semaphore. Today the wizard sequences
runs so only one executes at a time: navigation is locked while a batch runs
(`WizardViewModel.IsNavigationLocked <- Step3.IsRunning`), and Step 4's retry
command blocks itself with `IsBusy`. Nothing structural prevents two
orchestrators from running concurrently — e.g. a future parallel workflow —
which would multiply the fan-out (2 x 4 `/tag` calls). If cross-workflow
parallelism is ever wanted, the cap needs a process-wide home.

There is no client-side connection bottleneck: `HttpClient`'s
`MaxConnectionsPerServer` default is effectively unbounded, so the semaphore
alone decides the socket count.

## 3. Inside the sidecar (what each incoming call meets)

One uvicorn process, no `workers=`; `/tag` is a **sync** `def` route, so
Starlette runs each request on its AnyIO worker threadpool. That means
concurrent HTTP calls genuinely execute **in parallel inside the server** —
the fan-out is not an illusion of overlapping sockets.

Locks it then encounters:

| Lock | Guards | Behaviour under concurrent calls |
|------|--------|---------------------------------|
| `_state_lock` (`model_loader`) | `/health` snapshot state | Read-mostly; uncontended |
| `_model_cache_lock` | cache dict `(model_id, task, device) -> pipeline` | Fast dict access |
| `_model_build_lock` | pipeline **construction** | Serializes cold loads; later loads are cache hits |
| `_hf_pipeline_import_lock` | `transformers.pipeline` import | Double-checked import; survives the frozen PyInstaller bundle |
| `_config_lock` (`service.py`) | session config | GET/PUT and the model-override writeback in `/tag` |
| `_download_lock` (`service.py`) | download-thread registry | `already_downloading` idempotence, one thread per model |
| *(none)* | `inference_engine.run_inference` | **Concurrent generation on the shared pipeline** |

### Cold start under fan-out — why the launch order is what it is

The first `/tag` batch can fan out four requests against a cold sidecar, and
two bugs taught the current design:

1. **Racing lazy imports.** Four threads importing transformers' `pipeline`
   inside the frozen bundle 503'd three of four with
   `ImportError: cannot import name 'pipeline'` -> `_hf_pipeline_import_lock`.
2. **Racing pipeline builds.** N parallel requests built N pipelines of the
   same weights; some kept float32 RMSNorm parameters that promoted
   activations to float32 and crashed the next bfloat16 linear
   (`expected ... float != BFloat16`) -> `_model_build_lock` +
   double-checked cache reuse.

Boot order now avoids the worst of it: the lifespan spawns two daemon threads
(default-model **download**, then **warm-up** load after a
`WARMUP_GRACE_SECONDS = 2` head start), so the host's readiness poll sees
`ready` only once the model can actually serve, and the first fan-out arrives
at a cache hit. Warm-up failures restore the prior status so they can never
surface as the fatal `/health` `error`; the first real `/tag` retries the
load.

`PUT /config` with a changed `model_id` calls `unload_model()`, which clears
`_model_cache` — requests already holding the old pipeline keep using it, and
the next `/tag` rebuilds (serialized by the build lock). During a Step 3
batch the wizard's navigation lock keeps the UI from reaching Step 2, so this
cannot conflict with an active run in practice.

## 4. Rough edges (ranked)

1. **Concurrent generation is unserialized.** Up to 4 `run_inference` calls
   execute at once on the same pipeline object; `inference_engine.py` holds
   no lock. The original Python app pinned local inference to
   `max_workers = 1` for this reason; 4-way fan-out is a documented port
   decision that helps on CPU. On CUDA it serializes at the driver level and
   multiplies VRAM (engine guidance: consider 1-2 for GPU), and transformers
   does not guarantee thread-safe concurrent `generate` on one shared
   instance. The loading-time races above came from this same fan-out class.
   Worth verifying; a server-side generation lock (or batching requests into
   one generate call) would be the structural fix if it misbehaves.
2. **No process-wide cap.** See the fan-out section above: wizard sequencing
   is the only guard against overlapping runs stacking 4 + 4 concurrent
   `/tag` calls.
4. **A wedged server parks the batch.** Nothing distinguishes a hung server
   from a slow one; a wedged sidecar holds each semaphore slot for the full
   5-minute request timeout, so a batch can stall ~20 minutes before all four
   items fail simultaneously.

## 5. Related

- [`codebase-guide.md`](codebase-guide.md) §6 — the short version, plus the
  GPU note and the original app's `max_workers = 1` contrast.
- [`sidecar-reference.md`](sidecar-reference.md) — module-by-module reference,
  including warm-up and the download/health machinery.
- [`sidecar-protocol.md`](sidecar-protocol.md) — the generated wire contract:
  timeouts, 503 semantics, and the status-code guarantees the concurrency
  story rides on.
