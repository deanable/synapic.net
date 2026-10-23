# 10-03 SUMMARY — A real outage pauses the run, and it resumes by itself

**Status:** Complete — proven against a live catalog on 2026-10-23 (DAM-03).

**Requirements:** DAM-03

## What was built

| File | Change |
|------|--------|
| `src/Synapic.Indexer/Pipeline/BatchExecutor.cs` | The resume is now observable and named: `PauseCount`, `ResumeCount`, and the log line `The catalog is available again; the run resumes after being paused N minutes.` A resume is counted by the next batch that runs after the window, not by the clock alone |
| `tests/Synapic.Indexer.Tests/ResilienceTests.cs` | The pause fact now asserts the counters as well as the queue state: pause → advance → observing the window end is not a resume → the next batch is |
| `build/block-daminion.ps1` | New elevated watcher: waits for `block.request`, applies one outbound TCP block rule **per resolved address** to the catalog's host, proves a new connection now fails **by hostname**, writes `block.ack` (including `open_addresses_after_apply=`), waits for `unblock.request`, proves the server answers again, removes the rules in a `finally`, writes `unblock.ack`. The server URL comes from `-ServerUrl`, `SYNAPIC_TEST_DAMINION_URL`, or the saved connection (only the URL is read) |
| `tests/Synapic.Indexer.Tests/DaminionLiveAcceptanceTests.cs` | `Live_outage_pauses_and_the_queue_completes`: enqueues six items, requests the block, proves the outage with a fresh client (no session, no pooled connection), runs a batch, asserts the pause with no Failed/InFlight row, lifts the block, proves recovery, advances the clock past the pause window and runs until every row is `Completed`, then asserts the pause and resume lines in the log. Scope resolution now accepts a saved search only once a page of it comes back non-empty |

## The live run

Server: `http://damserver.local/daminion` (the first catalog, `192.168.60.7`, went offline at 14:39 and
never returned). Transcript: [10-ACCEPTANCE.md](10-ACCEPTANCE.md#dam-03--a-real-outage-pauses-the-run-and-the-queue-completes-by-itself).

```
block ack: blocked_after_apply=True, open_addresses_after_apply= (none)
           addresses=192.168.0.60,fe80::80a3:f2e4:29ab:5a30
outage proven: a fresh client cannot reach the catalog.
during the outage: finished=0 paused=True pending=6 transient=0 failed=0 inflight=0 completed=0
recovery proven: a fresh client authenticates again.
after the outage: completed=6 transient=0 failed=0 pending=0 inflight=0 pauses=1 resumes=1
log: Catalog unavailable; the run is paused until 2026-01-01 09:05:00Z.
log: The catalog is available again; the run resumes after being paused 6,0 minutes.
```

Every queued row survived the outage: none failed, none was dropped, and all six completed after the
block lifted without intervention.

## What the attempt exposed

Two holes in the *evidence*, both fixed:

1. **The block was not the outage it claimed.** The first attempt reported `blocked_after_apply=True` and
   then a fresh client authenticated anyway — the host answers on a link-local IPv6 address as well, the
   rule covered only the A record, and the client resolves both families. The helper now blocks every
   resolved address, proves the block by hostname (the client's own path) and reports any address that is
   still open. The fact refused to pass that attempt, which is why it was caught.
2. **A saved search can exist and hold nothing.** The second catalog has its own saved search 16
   ("No Description", zero records), so the id alone would have been accepted as the milestone's scope.
   Resolution now accepts an id or a name only when a page of it returns records, and otherwise falls back
   to the catalog with the reason printed.

## What is verified

- `dotnet test tests/Synapic.Indexer.Tests -c Release --filter "FullyQualifiedName~ResilienceTests"`
  → 5 passed, including `A_catalog_outage_pauses_the_run_and_a_batch_resumes_after_the_window`, which
  pins pause counter, resume counter and the no-claim-inside-the-window rule.
- The live fact itself, once, end to end, through a real firewall block (above).
- The helper's refusal path (unelevated → exit 4), its address resolution, its cleanup (no rule left
  behind, server reachable again afterwards).

## Limit stated plainly

The outage is modelled as this machine losing its route to the catalog — the failure the client can
observe — not as the server process stopping. The hermetic `ResilienceTests` covers the seam; this fact
covers the client's real timeout-and-resume behaviour.
