# Phase 10 — live Daminion round trip, acceptance transcript

> The raw console logs this transcript quotes (`artifacts/phase-10/live-*.log`, the per-fact reports)
> are written by `build/acceptance-daminion.ps1` and are deliberately not committed: `artifacts/` is
> ignored. This file is the record; the command in each section regenerates the raw output.
Run date: 2026-10-23, from the development machine (Windows, .NET 10).
Servers:  http://researchserver.juicefilm.local/daminion (192.168.60.7) — DAM-01, DAM-02
          http://damserver.local/daminion (192.168.0.60) — DAM-01 re-check, DAM-03
          The first went offline at 14:39 and never came back; the outage fact was run
          against the second, which is the same product path on a different catalog.
Account:  researchserver: the saved Windows connection in HKCU\Software\Synapic\Daminion
          (user "dean", DPAPI), unprotected by build/acceptance-daminion.ps1 and handed to the
          test process's environment only; damserver: the credentials the operator supplied on
          the command line. Neither secret appears in this file or in any captured log.

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

Re-check on the second catalog (damserver.local, run for DAM-03):

  catalog GUID:  0aa3fe82-f8a5-4878-bb38-dd4e3b4db6b8
  saved searches enumerated: 17     items in scope: 51,759
  saved search 16 exists on this server as "No Description" but resolves to no records; it is not
  "city street" and cannot carry this acceptance.
  no saved search on this server is the requested scope ... Falling back to the catalog ...

That catalog does have a saved search numbered 16 — an unrelated default ("No Description") that
holds nothing. Existence of the id is therefore not enough to accept it as the scope; the resolution
now takes a saved search only once a page of it comes back non-empty.

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
Evidence: artifacts/phase-10/dam-03-damserver.txt, artifacts/phase-10/block/{block,unblock}.ack
STATUS: proven 2026-10-23 15:58 against http://damserver.local/daminion.

Run:

  # elevated shell (adds one outbound rule per resolved address, removes them in a finally)
  build/block-daminion.ps1 -WorkDir artifacts/phase-10/block -ServerUrl http://damserver.local/daminion -TimeoutSeconds 900

  # second shell
  SYNAPIC_TEST_DAMINION_URL=http://damserver.local/daminion \
  SYNAPIC_TEST_DAMINION_USERNAME=... SYNAPIC_TEST_DAMINION_PASSWORD=... \
  SYNAPIC_TEST_DAMINION_BLOCK_DIR=artifacts/phase-10/block \
    dotnet test tests/Synapic.Indexer.Tests -c Release --filter "FullyQualifiedName~Live_outage"

  saved search 16 exists on this server as "No Description" but resolves to no records; it is
  not "city street" and cannot carry this acceptance.
  no saved search on this server is the requested scope: it enumerates 17 saved searches, none
  usable for "city street". Falling back to the catalog ...
  selection: 6 item(s) from catalog (17 saved searches, none usable for the requested scope)
             (skipped 0 already carrying synapic-live-2026-10-23)

  block ack:
    blocked_at=2026-10-23T15:58:35.9547911+02:00
    host=damserver.local
    port=80
    addresses=192.168.0.60,fe80::80a3:f2e4:29ab:5a30
    reachable_before_block=True
    blocked_after_apply=True
    open_addresses_after_apply=
    rule=Synapic live outage
  outage proven: a fresh client cannot reach the catalog.
  during the outage: finished=0 paused=True pending=6 transient=0 failed=0 inflight=0 completed=0

  unblock ack:
    unblocked_at=2026-10-23T15:58:36.3210914+02:00
    host=damserver.local
    reachable_after_unblock=True
  recovery proven: a fresh client authenticates again.

  after the outage: completed=6 transient=0 failed=0 pending=0 inflight=0 pauses=1 resumes=1
  log: 15:58:36.147 [WARN] BatchExecutor: Catalog unavailable; the run is paused until 2026-01-01 09:05:00Z.
  log: 15:58:37.004 [INFO] BatchExecutor: The catalog is available again; the run resumes after being paused 6,0 minutes.

What the fact asserted, in this order: the block applied (`blocked_after_apply=True`), a fresh
client with no session cannot authenticate, the run pauses with nothing Failed and nothing InFlight
and every row accounted for, the block is removed (`reachable_after_unblock=True`), a fresh client
authenticates again, and every row completes after the clock passes the pause window — plus the two
log lines naming the pause and the resume. Nothing was dropped or permanently failed.

A finding the first attempt produced, now fixed in the helper: the first run reported
`blocked_after_apply=True` and then a fresh client authenticated anyway. The server answers on a
link-local IPv6 address as well (`fe80::…`), and the rule only covered the IPv4 address, so the
client — which resolves both families — went over IPv6. The helper now resolves every address,
adds one rule per address, proves the block by connecting **by hostname**, and records
`open_addresses_after_apply=` so a partial block is visible in the ack. The acceptance itself
refused to call that attempt an outage (`A fresh client still authenticated after the block was
reported applied, so the outage never reached this machine`) rather than passing on a rule that
did not hold. Earlier, the same fact skipped while the catalog was simply unreachable, and refused
to run at all: an outage must be a block that lifts, not a network that happens to be down.

## Limits of this transcript
  * One catalog's server build (the shapes observed: nested ItemData tags, value objects for
    multi-value tags, duplicate on repeat raw add, `field:none` clauses ignored).
  * The outage is modelled as this machine losing the route to the catalog — the same client-visible
    failure the indexer handles — not as the server process stopping.
  * DAM-03 ran on the second catalog (damserver.local, 51,759 items, 17 saved searches). Its scope
    resolved to the catalog fallback for the same reason as the first: no saved search there is
    "city street" and the one numbered 16 is an unrelated, empty one. The two catalogs disagree
    about saved-search numbering, which is exactly why the id is verified against records and not
    trusted.
  * An earlier note in this file said damserver.local's catalog was empty. That note was wrong: it
    came from a one-off shell probe, not the client. The client counts 51,759 items there, which is
    what the DAM-01 re-check and DAM-03 both ran against.
