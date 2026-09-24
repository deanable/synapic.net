#!/usr/bin/env python3
"""Refuse to compile the Windows installer when its AppId has drifted.

Usage:
    check-installer-appid.py [--iss <script>] [--shipped <record>]

Why this exists
---------------
``AppId`` is an identity, not a label. Inno stores it in the uninstall log and
compares it byte-for-byte before appending to an existing install, and it names
the Uninstall registry key after it (``<AppId>_is1``). Change the value and the
new build stops recognising the installs it is meant to upgrade: Windows lists a
second entry in Add/Remove Programs, the previous version's files are left
behind, and there is no upgrade path. Nothing in the toolchain warns you - the
installer compiles perfectly, and the damage only shows up on a machine that had
the earlier version. (This is also why the shipped value is left as the plain,
non-GUID string it is: Inno does not require a GUID for AppId, and "correcting"
it into a real GUID would break exactly those installs.)

So the AppId that has shipped is recorded in ``build/installer-appid.txt``, and
the installer script is not allowed to disagree with it. This is deliberately
not a "never changes" rule: editing the record alongside the .iss is how an
intentional change is made, which puts the decision in the diff where a reviewer
sees it.

The record is trusted as written. It is not re-derived from the last release
tag, because that needs full git history in the packaging environment and a
shallow checkout does not have it.

Exit codes: 0 = the installer matches the record, 1 = refuse to build,
2 = the guard could not run (an input was missing or unreadable).
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_ISS = REPO_ROOT / "build" / "installer-windows.iss"
DEFAULT_SHIPPED = REPO_ROOT / "build" / "installer-appid.txt"

SECTION = re.compile(r"^\[(?P<name>[^\]]+)\]$")
APPID_DIRECTIVE = re.compile(r"^AppId\s*=\s*(?P<value>.*)$", re.IGNORECASE)
# A {#...} constant has to be evaluated before it means anything, so the guard
# cannot compare it and says so instead of reporting a phantom mismatch.
PREPROCESSOR_CONSTANT = re.compile(r"\{#")


def unescape_inno_literal(value: str) -> str:
    """Inno writes a literal ``{`` as ``{{``, so compare the resolved text."""
    return "{" + value[2:] if value.startswith("{{") else value


def read_appid(iss_text: str) -> tuple[str | None, str | None]:
    """Return (effective AppId, why it could not be read) for ``[Setup]``."""
    values: list[str] = []
    section = ""
    for raw in iss_text.splitlines():
        line = raw.lstrip("\ufeff").strip()
        if not line or line.startswith(";"):
            continue
        header = SECTION.match(line)
        if header:
            section = header.group("name").strip().lower()
            continue
        if section != "setup":
            continue
        directive = APPID_DIRECTIVE.match(line)
        if directive:
            values.append(unescape_inno_literal(directive.group("value").strip()))

    if not values:
        return None, "no AppId directive found in the [Setup] section"
    if len(values) > 1:
        return None, f"AppId is set {len(values)} times in [Setup] ({', '.join(values)})"
    if PREPROCESSOR_CONSTANT.search(values[0]):
        return None, (
            f"AppId is built from a preprocessor constant ({values[0]!r}); write the "
            "literal value so the guard can compare it"
        )
    return values[0], None


def read_shipped(record_text: str) -> str | None:
    """First non-comment, non-blank line of the shipped-AppId record."""
    for raw in record_text.splitlines():
        line = raw.lstrip("\ufeff").strip()
        if line and not line.startswith("#"):
            return line
    return None


def check(iss_text: str, shipped: str | None) -> list[str]:
    """Problems that must stop the build; an empty list means the AppId is right."""
    problems: list[str] = []

    appid, appid_problem = read_appid(iss_text)
    if appid_problem is not None:
        problems.append(appid_problem)

    if not shipped:
        problems.append(
            "the shipped-AppId record is missing or empty, so the guard cannot "
            "confirm that this build will upgrade existing installs"
        )

    if appid is not None and shipped and appid != shipped:
        problems.append(
            f"AppId drifted: the installer says {appid!r} but the shipped value is "
            f"{shipped!r}"
        )

    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    parser.add_argument("--iss", default=str(DEFAULT_ISS), help="Inno Setup script to check")
    parser.add_argument(
        "--shipped",
        default=str(DEFAULT_SHIPPED),
        help="record of the AppId that has already shipped",
    )
    args = parser.parse_args(argv)

    try:
        iss_text = Path(args.iss).read_text(encoding="utf-8-sig")
        shipped = read_shipped(Path(args.shipped).read_text(encoding="utf-8-sig"))
    except OSError as exc:
        print(f"error: the AppId guard could not read its inputs ({exc})", file=sys.stderr)
        return 2

    problems = check(iss_text, shipped)
    if not problems:
        print(f"Installer AppId matches the shipped value ({shipped})")
        return 0

    print("error: refusing to build the Windows installer - the AppId changed.", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    print(
        "\nAppId is the uninstall identity: existing installs recorded it in their\n"
        "uninstall log and in the registry key named after it. A different value\n"
        "makes Windows treat this build as a different application - a second\n"
        "Add/Remove Programs entry, the old version's files left on disk, and no\n"
        "upgrade path for anyone already running it.\n"
        f"\nIf the change is intended, update {args.shipped} in the same commit so the\n"
        "decision shows up in review, and tell users they have to uninstall the\n"
        "previous version first.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
