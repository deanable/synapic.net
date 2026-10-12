# Phase A — metadata safety verification

**Date:** 2026-10-12

**Scope:** A1/A2 of the [remediation plan](remediation-plan-2026-10-12.md); local metadata only. R01/R02/R16 are addressed by the writer and its regressions. No catalog mutation semantics, dedup move/delete integration, G0 test-tier changes, UI layout, or historical milestone completion is claimed. No commit was requested or created.

## Delivered behavior

- [MetadataWriterService](../src/Synapic.Main/Services/MetadataWriterService.cs) preserves omitted fields and unrelated XMP. Legacy nonempty keyword writes merge; nonempty category/description replace their owned values. Empty model fields are unchanged, not an implicit clear.
- `PatchAsync` and `MetadataPatch` provide explicit Unchanged/Replace/Clear and keyword-only Merge. Description writes/clears change `x-default` while preserving translations. Clear produces an authoritative empty XMP property, so stale IPTC/EXIF fallbacks do not resurrect it in the reader; original EXIF/IPTC bytes are not removed.
- JPEG APP1 limits include the XMP header and two-byte length. Oversized basic packets, malformed/multiple/extended packets, unsupported RDF subjects, invalid XML and unsupported patch modes fail without mutating originals. Extended XMP is intentionally not implemented.
- JPEG/PNG copies stream to a unique same-directory staging file. Staged XMP/container structure and full lazy pixel decoding are validated before atomic `File.Replace`. A new TIFF sidecar uses non-overwriting `File.Move`; no unsafe rewrite fallback exists. Creation time/attributes and Unix permission mode are retained where supported. Pixel bytes are not re-encoded.
- PNG checks chunk boundaries/CRCs; one owned XMP iTXt chunk is replaced. Compressed XMP is decoded with a 16 MiB limit. Repeated identical updates do not grow the file.
- TIFF uses adjacent `image.tif.xmp`/`image.tiff.xmp`, reads it back, and leaves the image byte-identical. Embedded TIFF XMP seeds a new sidecar. `AssociatedArtifacts` defines image-plus-sidecar ownership for later action integration; **dedup move/delete is not yet sidecar-aware**.
- Cancellation is checked before commit and propagates; owned staging is cleaned. No post-commit cancellation check misreports a committed write as uncommitted. Read-only/symlink artifacts are refused.

## Reproduction and exercised coverage

[Baseline test log](../artifacts/phase-a/baseline-tests.log): **five new regressions failed, two existing tests passed** before the fix: JPEG preservation, PNG keyword merge, TIFF readback, repeated PNG growth, oversized JPEG acceptance.

Permanent coverage is in [metadata tests](../tests/Synapic.Main.Tests/MetadataWriterTests.cs) and [field-selection tests](../tests/Synapic.Main.Tests/TagFieldSelectionTests.cs). The focused suite includes existing [compiled checkbox/key input tests](../tests/Synapic.Main.Tests/TagFieldCheckboxTests.cs).

- All eight selected-field combinations across JPEG/PNG/TIFF through the **real tagging orchestrator and real writer**, using a deterministic fake sidecar and disposable images.
- Seeded category/keywords/default description, translated description, creator/copyright and custom XMP attribute preservation.
- Merge/replace/clear, keyword deduplication, Unicode/XML escaping and exact APP1 byte-size boundary.
- IO failure or cancellation at staging/validation/pre-commit boundaries across all formats; corrupted staged output; malformed/multiple source packets; invalid PNG CRC/truncated images; staging cleanup and prior bytes preserved.
- Actual Windows sharing-based atomic replacement refusal; competing new-sidecar creation; read-only sidecar and directory destination conflicts.
- Successful pixel-byte equality, file-attribute preservation, TIFF original-byte equality and associated-artifact discovery; embedded TIFF XMP seeds the sidecar without losing creator/copyright or changing image bytes; extended JPEG XMP is explicitly refused.

## Check results

| Check | Result |
|---|---|
| Focused metadata/selection/compiled-checkbox suite | **89 passed, 0 failed, 0 skipped**; [log](../artifacts/phase-a/focused-tests.log) |
| Final Release solution build | **Passed, 0 warnings/errors**; [log](../artifacts/phase-a/build.log) |
| Final full .NET solution suite | **559 reported passed** (Main 548, Shared 6, Integration 5), 0 failures; [log](../artifacts/phase-a/full-tests.log), [TRX results](../artifacts/phase-a/results/) |
| Help link/content structure | **Passed:** 30 topics, 31 listed files, 138 Contents/Index entries; [log](../artifacts/phase-a/help-check.log) |
| Edited-document references and whitespace | Checked after final documentation edits; links resolve and `git diff --check` has no whitespace diagnostics. |

The TRX records confirm the real local sidecar-to-JPEG round trip executed and
passed in **30.52 seconds**. Live Daminion environment variables were explicitly
removed for this run to prevent catalog writes; its existing early-return test
is reported as Passed in 3 ms, **not exercised coverage**. The suite total must
be read with that qualification.

Build/test invocations retain the real exit status when capturing logs. Initial compile errors (namespace ambiguity, required serialization options and an obsolete NetVips test call) were corrected directly; no suppressions or weakened assertions were added.

## Limitations and follow-on gates

- Tested on Windows in this checkout; Linux/macOS atomic replacement/permission behavior remains unverified. Unsupported atomic replacement fails safely rather than falling back to an in-place write.
- Fault injection proves handled IO failure/cancellation paths, not power-loss durability on every filesystem. Same-directory staging and flush do not establish a multi-file transaction or directory-fsync guarantee.
- Full staged decoding uses the existing NetVips dependency and adds IO/CPU work; no performance speedup or collection-scale timing is claimed. Blocking native decode cannot be interrupted mid-call; cancellation is observed immediately before/after it.
- Avoid concurrent edits by other applications. The input read lease reduces local races but is not a cross-process transaction across closing the input and replacement. Symbolic-link ancestors and all filesystem ACL variants are not exhaustively tested.
- Other tools must support the exact TIFF sidecar naming convention. Sidecar-aware move/delete remains Phase B; arbitrary external sidecar associations are not inferred.
- Existing environment-gated tests can report early returns as passes; G0/R22 remain open. Live Daminion must not be inferred from the full-suite count. Production data and normal user settings are untouched.
