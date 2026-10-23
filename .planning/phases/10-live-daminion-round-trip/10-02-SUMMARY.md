# 10-02 SUMMARY — Tag a real catalog, read it back, and do it twice

**Status:** Complete
**Requirements:** DAM-02

## What the live run proved

`artifacts/phase-10/dam-02-round-trip.txt` — 25 catalog items (26-50) written twice through the real
executor and a live `DaminionApiClient`:

- both batches: `completed=25 transient=0 failed=0 paused=False`;
- the product verifier (`VerifyItemMetadataAsync`) reports every item verified;
- after the second run, the marker keyword and the written category appear **exactly once** per item, the
  description is the written text, and every keyword/category the item carried before the run is still
  there (items 26 and 50 keep 23 and 16 pre-existing keywords respectively).

## Three defects the live catalog exposed (all fixed)

| Defect | Why it mattered | Fix |
|--------|-----------------|-----|
| The verifier read the payload's own member names | On the 11.x payload it could only ever find `propertyName`/`propertyValue` and reported every written field missing | `VerifyItemMetadataAsync` reads `TagPairsFromItemData` (tag name → value); the flat pre-11 shape stays as a fallback |
| Multi-value tags list their values as **objects** (`{text,id,tagName,…}`), and the reader only accepted strings | A written keyword/category was invisible to the read-back — the write looked like it never landed | `TagPairsFromItemData` reads `text`/`rawValue`/`value` from value objects |
| A raw-value add is **not idempotent** on this server | Re-running the same items doubled every keyword and category | `UpdateItemMetadataAsync` reads the item first and never sends a value it already holds; a write with nothing new to add is a success |

The third one is the one the phase exists for: the first attempt at "write twice, expect one copy" produced
two copies. The unit facts pin the decision (`DaminionVerifyMetadataTests`: the value-object shape, the flat
fallback, whole-value matching, `HasValue`).

## Deviation from the plan, and why

The plan said "fetch 25 items whose keywords, category and description are empty". That selection is not
available on this server: the untagged clause is ignored (10-01), so an "untagged page" is the catalog
head — and the catalog head is fully tagged. The round trip therefore selects real items from the resolved
scope, snapshots what they carry, and asserts the written values land exactly once while nothing already
there is lost. That is a stronger claim than writing into empty items, and it does not depend on a filter
the server does not implement.

Known residue: items 1-25 were written by the earlier, failing attempts (before the dedupe fix) and carry
duplicate copies of the marker and the category. The phase's own run is items 26-50. The duplicates are
left in place rather than deleted — deleting catalog values to tidy a test is the more destructive mistake,
and the transcript says exactly which items and values they are.

## Re-run on the second catalog

After the first server went offline, the same fact was run against `damserver.local` (51,759 items) so the
phase's write evidence and its outage evidence come from one catalog:

- `selection: 25 item(s) ... (skipped 6 already carrying synapic-live-2026-10-23)` — the six skipped items
  are the outage batch DAM-03 had just written, found by their marker keyword and left alone;
- items 55602-55627, both batches `completed=25 transient=0 failed=0`, verifier Ok per item;
- after the second run each item still carries exactly one copy of the marker keyword and the written
  category, with 20-27 pre-existing keywords and 1-2 categories preserved.

## Verification

- `pwsh build/acceptance-daminion.ps1 -Filter "FullyQualifiedName~Live_round_trip"` → 1 passed (1 min).
- The same fact against `damserver.local` → 1 passed (30 s), 25 items written twice.
- `dotnet test tests/Synapic.Core.Tests -c Release --filter "FullyQualifiedName~DaminionVerifyMetadata"` → 13 passed.
