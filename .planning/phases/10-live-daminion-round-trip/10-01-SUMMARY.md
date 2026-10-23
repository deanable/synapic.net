# 10-01 SUMMARY — A live run that knows what it is connected to

**Status:** Complete
**Requirements:** DAM-01, DAM-04 (procedure half)

## What was built

| File | Change |
|------|--------|
| `build/acceptance-daminion.ps1` | New: resolves `SYNAPIC_TEST_DAMINION_*` or the saved connection (`HKCU\Software\Synapic\Daminion`, DPAPI, the app's entropy), hands the value to the child `dotnet test` through its environment only, writes a utf-8 console log, exits 3 naming the missing piece when neither source is complete |
| `tests/Synapic.Indexer.Tests/DaminionLiveAcceptanceTests.cs` | New: `[Trait("Category","Live")]` + `[SkippableFact]` facts, the shared connect helper (auth failure = fail, unreachable = skip), and the DAM-01 report fact |
| `docs/testing.md` | The live tier: the script, the credential sources, what a run writes, the outage helper, and the evidence path |

## What the run actually reported

`artifacts/phase-10/dam-01-report.txt` (server `http://researchserver.juicefilm.local/daminion`, catalog
`7791b6d4-df38-405c-9f03-601365a253f2`):

- 47 saved searches enumerated. **Saved search 16 is not one of them** — the ids jump 1-11, then 110-220,
  and no name contains "city street".
- A structured saved-search query naming id 16 **is not refused**: the server answers it with the whole
  catalog (`1,241,404` items, first page = items 1-5 of the catalog). Saved search 219 does narrow (3 items),
  so the scope mechanism works; an unknown id is simply ignored.
- The untagged clause (`keywords:none categories:none description:none`) is **ignored by both**
  `MediaItems/Get` and `MediaItems/GetCount`: the count with and without the clause is identical
  (1,241,404), and the item page returns tagged records. The "untagged: 200" the client printed is its own
  fallback for "the count equals the catalog", not a count of untagged items.

## The decision this forced

The scope is resolved against what the server holds (named saved search → saved search by name → catalog),
printed with the reason, and never taken from the constant alone. The round trip does not depend on
pre-empty items: 10-02 selects real items and snapshots them instead (see its summary).

## Verification

- `pwsh build/acceptance-daminion.ps1 -Filter "FullyQualifiedName~Live_catalog_reports"` → 1 passed.
- With no credentials the same filter reports a skip naming the variables, not a pass.
- Neither the script's output nor the test's output contains a password value.
