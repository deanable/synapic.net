# 10-04 SUMMARY — The evidence, and the record left true

**Status:** Complete. DAM-03 was recorded as pending when this summary was first written, then closed on a
second live catalog; that follow-up is recorded here rather than in a rewritten history.

## The transcript

[10-ACCEPTANCE.md](10-ACCEPTANCE.md) — what was written and read back (items 26-50, twice), the scope
resolution and its reasons, the three defects the catalog exposed, the residue on items 1-25, the outage
fact's status and the exact commands to finish it. The raw console output lives in `artifacts/phase-10/`
and is not committed (`artifacts/` is ignored); the acceptance script regenerates it.

## The procedure

`docs/testing.md` now carries the live tier as its own section: `build/acceptance-daminion.ps1`
(credential sources, what a run writes, how it skips), the outage helper and the directory it watches,
the scope-resolution rule, and where the evidence lives. The indexer-operations doc's outage row states
the pause and resume log lines and that no operator action is needed.

## Requirement status at this point

| Requirement | Status | Evidence |
|-------------|--------|----------|
| DAM-01 | Complete | `dam-01-report.txt`: server, catalog GUID, 47 saved searches enumerated, the requested scope absent and the reason printed, counts reported |
| DAM-02 | Complete | `dam-02-round-trip.txt`: 25 items, two runs, exact-once keyword and category, pre-existing values preserved, verifier Ok |
| DAM-03 | Complete (follow-up) | `dam-03-damserver.txt`: a real firewall block paused a six-item run with nothing failed or dropped, and the queue completed after the block lifted (`pauses=1 resumes=1`, both log lines). Run on `damserver.local` after `researchserver` went offline; the first attempt was refused because the block had only covered IPv4 and the client reached the catalog over its link-local IPv6 address |
| DAM-04 | Complete | The opt-in script, the skip-with-reason behaviour, the documented procedure and the committed transcript |

## What this phase changed in the product

Three defects that only a catalog that stores what it is told could reveal: the metadata verifier read
the payload's member names instead of its tags; the tag reader ignored object-shaped multi-values; and
the catalog write was not idempotent, so a repeated run duplicated every keyword. The third is the one
DAM-02 exists to catch, and it was caught by running.
