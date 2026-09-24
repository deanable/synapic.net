#!/usr/bin/env python3
"""Generate ``docs/sidecar-protocol.md`` from the code that actually defines the contract.

Two sources of truth are combined:

* the **live FastAPI OpenAPI schema** of ``src/Synapic.Inference/service.py``
  (routes, request bodies, validation constraints, documented status codes), and
* the **C# contract DTOs** in ``src/Synapic.Shared/Contracts/Contracts.cs``
  (the response shapes the host deserializes).

The generator also *cross-checks* the two sides: a request DTO that exists in
both languages must have the same wire field set, and every response DTO named
here must exist in ``Contracts.cs``. Any mismatch exits non-zero, so the
document cannot silently drift away from either side.

Usage::

    python build/generate-protocol-doc.py            # rewrite the document
    python build/generate-protocol-doc.py --check    # exit 1 when stale (CI)

The sidecar modules are imported flat (``import service``), exactly like the
PyInstaller entry point; no torch/transformers is needed to read the schema.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
SIDECAR_SRC = REPO_ROOT / "src" / "Synapic.Inference"
CONTRACTS_FILE = REPO_ROOT / "src" / "Synapic.Shared" / "Contracts" / "Contracts.cs"
OUTPUT_FILE = REPO_ROOT / "docs" / "sidecar-protocol.md"

# Importing the app must never touch the network or download a model.
os.environ.setdefault("SYNAPIC_DISABLE_AUTO_DOWNLOAD", "1")
if str(SIDECAR_SRC) not in sys.path:
    sys.path.insert(0, str(SIDECAR_SRC))

import service  # noqa: E402  (flat import: matches the PyInstaller entry point)


# ---------------------------------------------------------------------------
# Cross-checks
# ---------------------------------------------------------------------------
# Request DTOs that exist on BOTH sides: (OpenAPI schema, C# record, label).
REQUEST_CHECKS = [
    ("TagRequestModel", "TagRequest", "POST /tag body"),
    ("TagOptionsModel", "TagOptions", "POST /tag options"),
    ("DownloadRequestModel", "DownloadRequest", "POST /models/download body"),
    ("ConfigModel", "ConfigDto", "PUT /config body"),
]

# Response DTOs rendered from the C# contract, keyed by (method, path).
RESPONSE_DTOS = {
    ("get", "/health"): "HealthResponse",
    ("get", "/models/list"): "ModelInfo",
    ("get", "/config"): "ConfigDto",
    ("put", "/config"): "ConfigDto",
    ("get", "/prompt"): "PromptDefaultsDto",
    ("post", "/tag"): "TagResponse",
}

# Endpoints whose response has no C# DTO (the host only inspects the body
# opportunistically), so their shape is declared inline here.
INLINE_RESPONSES = {
    ("post", "/models/download"): [
        ("status", "string", "`download_started` / `already_downloading` / `downloaded`"),
        ("model_id", "string", None),
    ],
    ("post", "/shutdown"): [
        ("status", "string", 'always `"shutting_down"`'),
    ],
}

# Responses whose body is an array of the mapped DTO rather than one object.
RESPONSE_ARRAYS = {("get", "/models/list")}

ROUTE_DESCRIPTIONS = {
    ("get", "/health"): "Readiness probe, polled up to 120 s at startup.",
    ("get", "/models/list"): "Models present in the HF cache (`HF_HOME`).",
    ("post", "/models/download"): "Start a background model download.",
    ("post", "/tag"): "Run inference on one image and return its tags.",
    ("get", "/prompt"): "The tag instruction built into this sidecar, which `/tag` uses when the request has no `user_prompt`.",
    ("get", "/config"): "Read the session inference config.",
    ("put", "/config"): "Update the session inference config (a changed `model_id` unloads the model).",
    ("post", "/shutdown"): "Graceful exit; the host also kills the process tree after a grace period.",
}

# ---------------------------------------------------------------------------
# C# contract parsing
# ---------------------------------------------------------------------------

_RECORD_RE = re.compile(r"public\s+(?:sealed\s+)?record\s+(\w+)")
_PROP_RE = re.compile(
    r'\[JsonPropertyName\("([^"]+)"\)\]\s*'
    r"public\s+(.+?)\s+([A-Za-z_]\w*)\s*\{\s*get;\s*init;",
    re.DOTALL,
)


def parse_csharp_contracts(path: Path) -> dict[str, list[dict]]:
    """Return ``{record name: [{wire, cs_type, property}, ...]}``."""
    text = path.read_text(encoding="utf-8")
    starts = [(m.start(), m.group(1)) for m in _RECORD_RE.finditer(text)]

    records: dict[str, list[dict]] = {}
    for index, (start, name) in enumerate(starts):
        end = starts[index + 1][0] if index + 1 < len(starts) else len(text)
        segment = text[start:end]
        props = []
        for wire, cs_type, prop in _PROP_RE.findall(segment):
            props.append(
                {"wire": wire, "cs_type": cs_type.strip(), "property": prop.strip()}
            )
        records[name] = props
    return records


def wire_type(cs_type: str) -> tuple[str, bool]:
    """Map a C# type to ``(json-ish type, optional)``."""
    token = cs_type.strip()
    optional = token.endswith("?")
    if optional:
        token = token[:-1].rstrip()
    if token.endswith("[]"):
        inner, _ = wire_type(token[:-2])
        return f"array of {inner}", optional
    if token.startswith("Dictionary<"):
        return "object (string → number)", optional
    mapping = {
        "string": "string",
        "int": "integer",
        "long": "integer",
        "double": "number",
        "float": "number",
        "decimal": "number",
        "bool": "boolean",
        "JsonElement": "any",
    }
    return mapping.get(token, token), optional


# ---------------------------------------------------------------------------
# OpenAPI helpers
# ---------------------------------------------------------------------------


def schema_ref(node: dict) -> str | None:
    ref = node.get("$ref")
    return ref.rsplit("/", 1)[-1] if ref else None


def describe_openapi_prop(prop: dict) -> tuple[str, str]:
    """Return ``(type label, constraints label)`` for an OpenAPI property."""
    if (ref := schema_ref(prop)) is not None:
        label = f"`{ref}`"
        nullable = False
    else:
        any_of = prop.get("anyOf") or prop.get("oneOf")
        if any_of:
            parts = []
            nullable = False
            for option in any_of:
                if option.get("type") == "null":
                    nullable = True
                    continue
                parts.append(describe_openapi_prop(option)[0].strip("`"))
            label = label = " | ".join(parts) if parts else "any"
        else:
            label = prop.get("type", "any")
            nullable = False
    if nullable:
        label = f"{label} (nullable)"

    constraints = []
    for key, fmt in (("minimum", "min {v}"), ("maximum", "max {v}")):
        if key in prop:
            constraints.append(fmt.format(v=prop[key]))
    if "pattern" in prop:
        constraints.append(f"pattern `{prop['pattern']}`")
    if "enum" in prop:
        constraints.append("enum: " + ", ".join(f"`{v}`" for v in prop["enum"]))
    if "default" in prop:
        constraints.append(f"default `{prop['default']}`")
    return label, "; ".join(constraints)


def render_openapi_table(schema: dict) -> list[str]:
    props = schema.get("properties") or {}
    required = set(schema.get("required") or [])
    if not props:
        return ["_No fields._"]
    lines = ["| field | type | required | constraints |", "|-------|------|----------|-------------|"]
    for name in props:
        label, constraints = describe_openapi_prop(props[name])
        lines.append(
            f"| `{name}` | {label} | {'yes' if name in required else 'no'} | {constraints or '—'} |"
        )
    return lines


def render_csharp_table(props: list[dict]) -> list[str]:
    if not props:
        return ["_No fields._"]
    lines = ["| field | type | optional |", "|-------|------|----------|"]
    for prop in props:
        label, optional = wire_type(prop["cs_type"])
        lines.append(f"| `{prop['wire']}` | {label} | {'yes' if optional else 'no'} |")
    return lines


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------


def validate(openapi_schemas: dict, contracts: dict[str, list[dict]]) -> list[str]:
    errors: list[str] = []

    for schema_name, record_name, label in REQUEST_CHECKS:
        schema = openapi_schemas.get(schema_name)
        if schema is None:
            errors.append(f"{label}: OpenAPI schema '{schema_name}' is missing")
            continue
        if record_name not in contracts:
            errors.append(f"{label}: C# record '{record_name}' is missing from Contracts.cs")
            continue
        python_fields = set(schema.get("properties") or {})
        csharp_fields = {p["wire"] for p in contracts[record_name]}
        if python_fields != csharp_fields:
            only_py = sorted(python_fields - csharp_fields)
            only_cs = sorted(csharp_fields - python_fields)
            errors.append(
                f"{label}: field mismatch between {schema_name} and {record_name} "
                f"(sidecar-only: {only_py or 'none'}; C#-only: {only_cs or 'none'})"
            )

    for dto in sorted(set(RESPONSE_DTOS.values())):
        if dto not in contracts:
            errors.append(f"response DTO '{dto}' is missing from Contracts.cs")

    return errors


# ---------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------


def render(openapi: dict) -> str:
    components = openapi.get("components", {}).get("schemas", {})
    contracts = parse_csharp_contracts(CONTRACTS_FILE)

    errors = validate(components, contracts)
    if errors:
        raise SystemExit(
            "Contract drift detected:\n  - " + "\n  - ".join(errors)
        )

    lines: list[str] = []
    add = lines.append

    add("<!-- GENERATED FILE - DO NOT EDIT BY HAND. -->")
    add("<!-- Regenerate: python build/generate-protocol-doc.py -->")
    add("<!-- Verify in CI: python build/generate-protocol-doc.py --check -->")
    add("")
    add(f"# Sidecar HTTP Protocol (v{openapi.get('info', {}).get('version', '1.0.0')})")
    add("")
    add("The contract between the Avalonia host and the Python sidecar. This file is")
    add("**generated** from the code and verified in CI, so it cannot drift:")
    add("")
    add("- Routes, request bodies, validation constraints and documented status codes")
    add("  come from the live FastAPI OpenAPI schema of")
    add("  `src/Synapic.Inference/service.py`.")
    add("- Response shapes come from the C# contract DTOs the host deserializes in")
    add("  `src/Synapic.Shared/Contracts/Contracts.cs`.")
    add("- The generator cross-checks request DTOs across both languages and fails on")
    add("  any field mismatch (see [Drift checks](#drift-checks)).")
    add("")
    add("## Transport")
    add("")
    add("- **Base URL**: `http://127.0.0.1:{port}` (loopback only).")
    add("- **Port**: OS-assigned at launch (`--port=0`) and announced through the port")
    add("  file `%TEMP%/synapic_port_{pid}.txt`, which contains `port\\npid\\n`. The host")
    add("  reads it (validating the pid) before polling `/health`.")
    add("- **Encoding**: JSON with `snake_case` keys. The host serialises with the")
    add("  source-generated `SynapicJsonContext`; the sidecar validates with Pydantic.")
    add("- **Timeouts**: the host allows 5 minutes per `/tag` and retries once on `503`")
    add("  (model loading). `/health` is polled until `ready`, max 120 s.")
    add("")

    # ---- Routes ----
    add("## Routes")
    add("")
    paths = openapi.get("paths", {})
    routes = sorted(
        ((method.upper(), path, spec) for path, methods in paths.items() for method, spec in methods.items()),
        key=lambda item: (item[1], item[0]),
    )
    for method, path, spec in routes:
        key = (method.lower(), path)
        add(f"### `{method} {path}`")
        add("")
        description = ROUTE_DESCRIPTIONS.get(key)
        if description:
            add(description)
            add("")

        request_body = spec.get("requestBody")
        if request_body:
            for media, media_spec in request_body.get("content", {}).items():
                schema = media_spec.get("schema", {})
                ref = schema_ref(schema) or "(inline)"
                required = "required" if request_body.get("required") else "optional"
                add(f"**Request** (`{media}`, {required}) — `{ref}`")
                add("")
                target = components.get(ref, schema)
                lines.extend(render_openapi_table(target))
                add("")

        dto = RESPONSE_DTOS.get(key)
        inline = INLINE_RESPONSES.get(key)
        responses = spec.get("responses", {})
        if responses:
            add("**Responses**")
            add("")
            for status in sorted(responses, key=lambda s: (s != "200", s)):
                response = responses[status]
                desc = response.get("description", "")
                add(f"- `{status}` — {desc}")
            add("")

        if dto:
            if key in RESPONSE_ARRAYS:
                add(f"**`200` body** — array of `{dto}` (C# `Synapic.Shared.Contracts.{dto}[]`)")
            else:
                add(f"**`200` body** — `{dto}` (C# `Synapic.Shared.Contracts.{dto}`)")
            add("")
            lines.extend(render_csharp_table(contracts[dto]))
            add("")
        if inline:
            add("**`200` body**")
            add("")
            add("| field | type | notes |")
            add("|-------|------|-------|")
            for field, ftype, note in inline:
                add(f"| `{field}` | {ftype} | {note or '—'} |")
            add("")

    # ---- Nested DTOs referenced by responses ----
    referenced: list[str] = []
    seen: set[str] = set()

    def collect(dto_name: str) -> None:
        if dto_name in seen:
            return
        seen.add(dto_name)
        props = contracts.get(dto_name, [])
        referenced.append(dto_name)
        for prop in props:
            token = prop["cs_type"].strip().rstrip("?").rstrip("[]").strip()
            if token in contracts:
                collect(token)

    for dto in RESPONSE_DTOS.values():
        collect(dto)
    for _, record, _ in REQUEST_CHECKS:
        collect(record)

    add("## Schemas")
    add("")
    add("Wire shapes of every DTO used above (nested DTOs included).")
    add("")
    for dtype in referenced:
        add(f"### `{dtype}`")
        add("")
        lines.extend(render_csharp_table(contracts[dtype]))
        add("")

    # ---- Drift checks ----
    add("## Drift checks")
    add("")
    add("`python build/generate-protocol-doc.py --check` runs in CI and fails when:")
    add("")
    add("- a request DTO differs between the FastAPI model and the C# record:")
    for schema_name, record_name, label in REQUEST_CHECKS:
        add(f"  - `{schema_name}` ↔ `{record_name}` ({label})")
    add("- a response DTO referenced here is missing from `Contracts.cs`;")
    add("- this document is stale (the generated text differs from the committed file).")
    add("")
    add("Behavioural guarantees (status codes, `/health` shape, `/config` merge) are")
    add("additionally pinned by `tests/Synapic.Inference.Tests/test_service_contract.py`")
    add("and `tests/Synapic.Shared.Tests/ContractsRoundTripTests.cs`.")
    add("")

    return "\n".join(lines).rstrip() + "\n"


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="exit non-zero when the committed document is out of date (CI mode)",
    )
    args = parser.parse_args()

    document = render(service.app.openapi())

    if args.check:
        current = OUTPUT_FILE.read_text(encoding="utf-8") if OUTPUT_FILE.exists() else ""
        if current != document:
            print(
                f"error: {OUTPUT_FILE.relative_to(REPO_ROOT)} is out of date.\n"
                "Run: python build/generate-protocol-doc.py",
                file=sys.stderr,
            )
            return 1
        print(f"{OUTPUT_FILE.relative_to(REPO_ROOT)} is up to date")
        return 0

    OUTPUT_FILE.write_text(document, encoding="utf-8")
    print(f"wrote {OUTPUT_FILE.relative_to(REPO_ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
