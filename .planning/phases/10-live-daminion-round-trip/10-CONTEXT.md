---
phase: 10-live-daminion-round-trip
created: 2026-10-23
milestone: v1.2
requirements: [DAM-01, DAM-02, DAM-03, DAM-04]
---

# Phase 10 Context — Live Daminion round trip

## Why this phase exists

Every catalog assertion so far runs against a fake: `ResilienceTests` points a real client at a closed
port, and phase 8 recorded plainly that *"Daminion was not exercised against a live server."* The
catalog path — download the original, tag it, commit the metadata, read it back — has never been
executed against a server that stores what it is told. This phase runs it, once, against the provided
catalog and commits the transcript.

## The server

| Item | Value |
|------|-------|
| Server | `http://researchserver.juicefilm.local/daminion` (HTTP 200, reachable from this machine) |
| Account | the saved Windows connection at `HKCU\Software\Synapic\Daminion` — user `dean`, password DPAPI-protected |
| Scope | saved search 16 (`city street`), items with empty keywords, category and description |
| Writes | 25 items (DAM-02), then a small outage batch (DAM-03) |
| After | tags stay in the catalog — the tags are the product's output, not test residue to erase |
| Second server | `http://damserver.local/daminion` (192.168.0.60, 51,759 items, 17 saved searches), used when the first stopped answering at 14:39: DAM-01 re-check, the DAM-02 round trip (items 55602-55627) and DAM-03 |

## Decisions

- **D-13-01 — the credential source is the saved Windows connection, not a pasted secret.** The
  acceptance script decrypts the DPAPI blob (same entropy, `Synapic.Daminion.v1`) and hands the value to
  the child process's environment only; nothing is printed, logged, committed or echoed. `SYNAPIC_TEST_DAMINION_*`
  still wins when it is set, so CI and other operators are unaffected.
- **D-13-02 — 25 items from saved search 16, chosen by the same filter the app sends.** The items must have
  empty keywords, category and description, so the write proves the product's own selection rule rather
  than a hand-picked list. The scope and count are printed by DAM-01 before anything is written.
- **D-13-03 — a deterministic sidecar, a real catalog.** The fake sidecar supplies the tag text; the
  download, the BatchChange write and the read-back all go to the live server. The model was proven
  against the live bundle in 08-03; making phase 10 depend on sampling would make the read-back
  non-deterministic without strengthening any catalog claim.
- **D-13-04 — the outage is a real firewall block, mid-batch.** The server is not on this machine, so the
  acceptance blocks this machine's outbound TCP to the server's address for a bounded window, from an
  elevated script, and removes the rule in a `finally`. The test proves the block took effect (a fresh
  client cannot authenticate) before it asserts the pause, and proves it was lifted (a fresh client
  authenticates again) before it asserts the completion.
- **D-13-05 — "check in" means the catalog write, and the read-back is the proof.** Daminion commits
  metadata through `ItemData/BatchChange`; there is no second metadata check-in step. The transcript
  records the exact write call and the read-back that followed it, so the word cannot hide a missing
  operation.
- **D-13-06 — the read-back uses the product's own verifier, and the verifier had to be fixed first.**
  `VerifyItemMetadataAsync` ("Verify Daminion writes" in the app) matched on the JSON's own member names,
  so on a server 11.x payload it could only ever find `propertyName`/`propertyValue` — it would report
  every field missing. It now reads the same tag-name/value pairs the capture-date reader uses.

## What the live server changed about the plan

These were discovered by running, and each has a decision because the plan assumed otherwise.

- **D-13-07 — the scope is resolved, never trusted.** The milestone named saved search 16 ("city street");
  this catalog enumerates 47 saved searches and none of them is id 16 or has that name. The server does not
  refuse a structured query for an unknown saved-search id — it answers with the whole catalog (1,241,404
  items) — so a run that trusted the constant would have tagged the catalog head and called it "city street".
  Resolution order is: the named saved search if the server lists it, then a saved search by name, then the
  catalog; whichever wins is printed with the reason.
- **D-13-08 — "untagged" is established per item, not by the clause.** `MediaItems/Get` and
  `MediaItems/GetCount` both ignore `keywords:none categories:none description:none` on this build (the count
  is identical with and without it, and the page returns tagged records). The client's "untagged: 200" is its
  own fallback for "count equals catalog", not a count of untagged items. The round trip therefore selects
  real items and snapshots what they carry; it does not claim to have found empty ones.
- **D-13-09 — a raw-value tag add is not idempotent, so the write must be.** Sending the same category or
  keyword twice through `ItemData/BatchChange` attaches a second copy. Writing the same items twice is part
  of what this phase proves, so `UpdateItemMetadataAsync` reads the item first and never sends a value it
  already holds; a write with nothing new to add is a success. The verifier's read-back is what makes the
  claim checkable.
- **D-13-10 — the read-back must read the server's value shape.** A multi-value tag lists its values as
  objects (`{text,id,tagName,…}`), a single-value tag as a string. The reader accepts both, and keeps the
  flat pre-11 member-name shape as a fallback so the fix for 11.x did not break older payloads.
- **D-13-11 — a saved search is a scope only if it returns records.** The second catalog *does* enumerate
  a saved search 16, and it is an unrelated default ("No Description") holding zero records. Existence of
  the id therefore is not evidence of the scope, exactly as an unknown id answering with the whole catalog
  is not. Resolution takes an id or a name only after a one-item page of it comes back non-empty, and
  prints each rejection.
- **D-13-12 — the outage must cover every address the name resolves to.** The first block covered the A
  record only; the host also answers on a link-local IPv6 address, and the client — which resolves both
  families — reached the catalog while the helper reported `blocked_after_apply=True`. The helper now adds
  one rule per resolved address (both families), proves the block by connecting by hostname (what the
  client does), and records `open_addresses_after_apply=` so a partial block is visible rather than
  inferred. The fact itself refused the first attempt, which is the behaviour that kept this from being
  recorded as a proven outage.

## Scope fence

No new product capability, no new scope picker, no catalog cleanup tool. The indexer configuration still
cannot express a saved-search scope (`DaminionSelectionBuilder` refuses it by design); the acceptance
drives the selection directly, as the desktop does, and that limitation is recorded rather than fixed.
