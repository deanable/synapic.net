# Phase 10 — live Daminion round trip, acceptance transcript

> The raw console logs this transcript quotes (`artifacts/phase-10/live-*.log`, the per-fact reports)
> are written by `build/acceptance-daminion.ps1` and are deliberately not committed: `artifacts/` is
> ignored. This file is the record; the command in each section regenerates the raw output.
Run date: 2026-10-23, from the development machine (Windows, .NET 10).
Server:   http://researchserver.juicefilm.local/daminion (192.168.60.7)
Account:  the saved Windows connection in HKCU\Software\Synapic\Daminion (user "dean", DPAPI);
          build/acceptance-daminion.ps1 unprotected it and handed it to the test process's
          environment only. No secret appears in this file or in any captured log.

How the run is made:
  pwsh build/acceptance-daminion.ps1 -Filter "FullyQualifiedName~Live_..."

It writes metadata (category, one dated keyword, description) to catalog items and deletes nothing.
The tags stay: they are the product's output on a real catalog, not test residue.

## DAM-01 — the run knows which server, catalog, scope and counts it is working against
Evidence: artifacts/phase-10/dam-01-report.txt

  server:        http://researchserver.juicefilm.local/daminion
  catalog GUID:  7791b6d4-df38-405c-9f03-601365a253f2
  saved searches enumerated: 47

  saved search 16 ("city street") is not on this server: it enumerates 47 saved searches, none
  with that id, and its structured query answers an unknown id with the whole catalog. Falling back
  to the catalog filtered to items whose keywords, category and description are all empty — the
  same selection the desktop makes when no scope is chosen.
  scope:          catalog (47 saved searches, none matching the requested scope)
  items in scope: 1,241,404
  untagged:       200 (the client's fallback number — see the finding below)

What the scope probe established:
  * An unknown saved-search id is NOT refused. `saved_search 16` returned items 1..5 of the catalog
    (1,241,404 items). Saved search 219 ("Untagged Images - Keyword") does narrow — 3 items — so the
    mechanism works; the id the milestone named simply does not exist here.
  * `keywords:none categories:none description:none` is ignored by both `MediaItems/Get` and
    `MediaItems/GetCount` on this build: the count is 1,241,404 with and without the clause, and the
    item page behind the clause returns tagged records. The "untagged: 200" above is the client's own
    fallback for "the count equals the catalog size", not a count of untagged items.

Consequence (recorded as D-13-07/08 in 10-CONTEXT.md): the scope is resolved against what the server
holds and printed with the reason; and the round trip does not depend on pre-empty items — it
snapshots the items it writes to and asserts against that.

## DAM-02 — tag, read back, run again; nothing duplicated, nothing lost
Evidence: artifacts/phase-10/dam-02-round-trip.txt

  selection: 25 item(s) from catalog (47 saved searches, none matching the requested scope)
             (skipped 25 already carrying synapic-live-2026-10-23)
  Writing to 25 item(s): 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43,
                        44, 45, 46, 47, 48, 49, 50
  run 1: batch: completed=25 transient=0 failed=0 pending=0 inflight=0 paused=False
  run 2: batch: completed=25 transient=0 failed=0 pending=0 inflight=0 paused=False

Written to each item: category "Synapic Live Acceptance", keyword "synapic-live-2026-10-23",
description "Synapic live acceptance 2026-10-23".

Read back from the catalog after run 1 and after run 2 (ItemData/GetAll, tag-name/value pairs):

  item 26: keywords=[Transport, Rail, Railway Station, Interior, Train, Electric Locomotive,
           Passenger Car, Coach, Carriage, Bullet Train, Bullet, Freight Car, Streetcar, Tram,
           Tramcar, Trolley, Trolley Car, Prison, Prison House, Pole, Window Screen, Railway,
           Train Station, Station, synapic-live-2026-10-23]
           categories=[winter, Train Station, Synapic Live Acceptance]
           description="Synapic live acceptance 2026-10-23"
  item 50: keywords=[Transport, Rail, Railway Station, Interior, Architecture, Archways, Vault,
           Palace, Monastery, Triumphal Arch, Mosque, Caldron, Cauldron, Throne, Architectural
           Design, Architectural Space, Architecture Hallway, synapic-live-2026-10-23]
           categories=[Architecture, winter, Synapic Live Acceptance]
           description="Synapic live acceptance 2026-10-23"

  (items 27..49 read back the same way; the report lists every one of them.)

Assertions that carried the run, per item:
  * the product verifier (`VerifyItemMetadataAsync`, "Verify Daminion writes") reports Ok;
  * the written keyword appears EXACTLY ONCE and the written category exactly once, after run 1 and
    again after run 2;
  * every keyword and category the item carried before the run is still there (23 and 16
    pre-existing keywords on items 26 and 50 respectively);
  * the description is the written text (descriptions are single-valued: the write replaces, it does
    not append).

Three defects the live catalog exposed, all fixed and pinned by unit tests:
  1. The verifier matched the payload's own member names, so on the 11.x shape it could only find
     "propertyName"/"propertyValue" and reported every written field missing.
  2. Multi-value tags carry their values as objects ({text,id,tagName,…}); the reader accepted only
     strings, so a written keyword/category was invisible to the read-back.
  3. A raw-value `ItemData/BatchChange` add is not idempotent: a second write of the same value
     attaches a second copy. `UpdateItemMetadataAsync` now reads the item first and never sends a
     value it already holds; "nothing new to add" is a success.

Residue, stated plainly: items 1-25 were written by earlier failing attempts, before fix 3. They
carry two copies of "synapic-live-2026-10-23" and of "Synapic Live Acceptance". They are left in
place — deleting catalog values to tidy a test is the more destructive mistake — and this line is
the record of it. The acceptance's own items are 26-50.

## DAM-03 — a real outage pauses the run and the queue completes by itself
STATUS: NOT YET PROVEN on this run.

The catalog became unreachable at 14:39 (network, not a firewall rule: `netsh advfirewall firewall
show rule name="Synapic live outage"` reports no rule, and the machine has no route to 192.168.60.7
from 192.168.0.181). The outage fact needs the server reachable immediately before and after the
block, so it could not be executed. Every other part of the fact is in place and hermetically
verified (ResilienceTests: pause → advance → resume counters and log wording; the unit suite is
green).

To run it when the catalog is reachable:

  # 1) elevated shell (it adds and removes one outbound firewall rule)
  build/block-daminion.ps1 -WorkDir artifacts/phase-10/block -TimeoutSeconds 900

  # 2) the acceptance (it starts in ~3 minutes: selection reads each candidate's metadata)
  build/acceptance-daminion.ps1 -Filter "FullyQualifiedName~Live_outage" -BlockDir artifacts/phase-10/block

The fact proves, in this order: the block applied (`blocked_after_apply=True`), a fresh client cannot
authenticate, the run pauses with no row Failed and none InFlight, the block is removed
(`reachable_after_unblock=True`), a fresh client authenticates again, and the queue completes every
row after the clock passes the pause window (also asserted: the log names "the run is paused" and
"resumes after being paused").

## Limits of this transcript
  * One catalog's server build (the shapes observed: nested ItemData tags, value objects for
    multi-value tags, duplicate on repeat raw add, `field:none` clauses ignored).
  * The outage is modelled as this machine losing the route to the catalog — the same client-visible
    failure the indexer handles — not as the server process stopping.
  * damserver.local (admin) was also checked: reachable, authenticates, catalog EMPTY (0 items), so
    it cannot host a write round trip until something is imported into it.
