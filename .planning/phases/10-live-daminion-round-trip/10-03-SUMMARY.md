# 10-03 SUMMARY — A real outage pauses the run, and it resumes by itself

**Status:** Incomplete — built and hermetically verified; the live half is blocked on the catalog's
network, not on the code (see "What is not proven").

**Requirements:** DAM-03

## What was built

| File | Change |
|------|--------|
| `src/Synapic.Indexer/Pipeline/BatchExecutor.cs` | The resume is now observable and named: `PauseCount`, `ResumeCount`, and the log line `The catalog is available again; the run resumes after being paused N minutes.` A resume is counted by the next batch that runs after the window, not by the clock alone |
| `tests/Synapic.Indexer.Tests/ResilienceTests.cs` | The pause fact now asserts the counters as well as the queue state: pause → advance → observing the window end is not a resume → the next batch is |
| `build/block-daminion.ps1` | New elevated watcher: waits for `block.request`, applies one outbound TCP block rule to the catalog's address, proves a new connection now fails, writes `block.ack`, waits for `unblock.request`, proves the server answers again, removes the rule in a `finally`, writes `unblock.ack`. The server URL comes from `-ServerUrl`, `SYNAPIC_TEST_DAMINION_URL`, or the saved connection (only the URL is read) |
| `tests/Synapic.Indexer.Tests/DaminionLiveAcceptanceTests.cs` | `Live_outage_pauses_and_the_queue_completes`: enqueues six items, requests the block, proves the outage with a fresh client (no session, no pooled connection), runs a batch, asserts the pause with no Failed/InFlight row, lifts the block, proves recovery, advances the clock past the pause window and runs until every row is `Completed`, then asserts the pause and resume lines in the log |

## Why a fresh client proves the outage

A firewall rule does not usefully break an HTTP connection that is already established, and the run's
own client is authenticated. "Fresh client cannot connect" is therefore the only honest proof that the
catalog is gone *from this machine*; the fact refuses to call the pause an outage test until it sees it,
and lifts the block again before reporting a skip.

## What is verified now

- `dotnet test tests/Synapic.Indexer.Tests -c Release --filter "FullyQualifiedName~ResilienceTests"`
  → 5 passed, including `A_catalog_outage_pauses_the_run_and_a_batch_resumes_after_the_window`, which
  pins pause counter, resume counter and the no-claim-inside-the-window rule.
- The block helper was exercised end to end for its refusal path (unelevated → exit 4) and its
  address resolution (it reads the saved connection when no URL is supplied).

## What is not proven

The live outage fact has not run: the catalog (`192.168.60.7`) stopped answering at 14:39 and this
machine (on `192.168.0.181`) had no route to it for the rest of the session. Nothing about the code is
in question — the fact needs a reachable server immediately before and after the block. The command is
recorded in [10-ACCEPTANCE.md](10-ACCEPTANCE.md); running it is the only step left.

DAM-03 stays **pending** until that run passes.
