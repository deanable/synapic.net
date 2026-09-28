#!/usr/bin/env python3
"""Refuse to compile the WiX installers when their identities are wrong.

Usage:
    check-installer-identities.py [--identities <wxi>] [--msi <wxs>]
                                  [--bundle <wxs>] [--shipped <record>]

Why this exists
---------------
Windows Installer decides what a package *is* from two GUIDs, and they have
opposite rules. Getting either one wrong produces an installer that builds, looks
right, installs, and then silently stops being upgradeable - the damage only
shows up on a machine that had the earlier version.

``UpgradeCode`` is a product family, and must be the **same in every version**.
It is what Windows matches a package against when working out whether it is
looking at a newer version of something already installed or at a different
application. Change it and the new build stops recognising the installs it is
meant to upgrade: a second entry in Add/Remove Programs, the previous version's
files left on disk, and no upgrade path. So the values that have shipped are
recorded in ``build/installer-identities.txt`` and this script refuses to build
unless ``build/wix/Synapic.Identities.wxi`` - the single file both WiX sources
include - still agrees with them.

``ProductCode`` is a single build, and must be **new in every version**. Reusing
one across two versions tells Windows the second package is the *same product*
as the first, which turns an upgrade into a repair of what is already there: the
old files stay, the new ones may never be installed, and the version in
Add/Remove Programs never moves. WiX generates a fresh one per build as long as
the attribute is left alone, so the rule here is the opposite of the one above -
checked for being *absent* (or explicitly ``"*"``) rather than for matching a
recorded value. There is deliberately no recorded ProductCode: recording one
would be recording the bug.

The two are also checked together because they are only meaningful as a pair.
Pinning the ProductCode and letting the UpgradeCode drift are the two opposite
ways to break upgrades, and a reviewer looking at one value in isolation cannot
tell which rule applies to it.

Exit codes: 0 = the identities are correct, 1 = refuse to build, 2 = the guard
could not run (an input was missing or unreadable).
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_IDENTITIES = REPO_ROOT / "build" / "wix" / "Synapic.Identities.wxi"
DEFAULT_MSI = REPO_ROOT / "build" / "wix" / "Synapic.Msi.wxs"
DEFAULT_BUNDLE = REPO_ROOT / "build" / "wix" / "Synapic.Bundle.wxs"
DEFAULT_SHIPPED = REPO_ROOT / "build" / "installer-identities.txt"

# The preprocessor form the .wxi uses: <?define Name = "Value"?>
DEFINE = re.compile(
    r"<\?define\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*\"(?P<value>[^\"]*)\"\s*\?>"
)

# An attribute, not the word: the comments in these files talk about UpgradeCode
# constantly and must not be mistaken for the value under test.
ATTRIBUTE = {
    "UpgradeCode": re.compile(r"\bUpgradeCode\s*=\s*\"(?P<value>[^\"]*)\""),
    "ProductCode": re.compile(r"\bProductCode\s*=\s*\"(?P<value>[^\"]*)\""),
}

GUID = re.compile(r"^\{?[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?$")

# Define name -> (record key, the source file that must use it).
UPGRADECODES = {
    "MsiUpgradeCode": ("msi", "Synapic.Msi.wxs"),
    "BundleUpgradeCode": ("bundle", "Synapic.Bundle.wxs"),
}

# The one value WiX accepts instead of an omitted ProductCode, meaning "generate
# a new one for this build".
GENERATED = {"*"}


def normalise(value: str) -> str:
    """Compare identities by their digits: braces and case carry no meaning."""
    return value.strip().strip("{}").upper()


def read_defines(text: str) -> tuple[dict[str, str], list[str]]:
    """The UpgradeCodes the WiX sources use, and any problem reading them."""
    found: dict[str, str] = {}
    counts: dict[str, int] = {}
    problems: list[str] = []
    for match in DEFINE.finditer(text):
        name = match.group("name")
        if name not in UPGRADECODES:
            continue
        counts[name] = counts.get(name, 0) + 1
        found[name] = match.group("value")

    # Two definitions of the same identity means the second one wins silently,
    # which is exactly the sort of drift this guard exists to catch.
    for name, count in counts.items():
        if count > 1:
            problems.append(f"{name} is defined {count} times; keep one definition")

    for name in UPGRADECODES:
        if name not in found:
            problems.append(
                f"no {name} define found in the identities file; the sources no "
                "longer describe an identity this guard can check"
            )
    return found, problems


def read_shipped(text: str) -> tuple[dict[str, str], list[str]]:
    """The recorded identities, and any problem reading them."""
    shipped: dict[str, str] = {}
    problems: list[str] = []
    for raw in text.splitlines():
        line = raw.lstrip("\ufeff").strip()
        if not line or line.startswith("#"):
            continue
        key, sep, value = line.partition("=")
        if not sep:
            problems.append(f"record line is not 'key = value': {line!r}")
            continue
        shipped[key.strip().lower()] = value.strip()
    return shipped, problems


def attribute_values(text: str, attribute: str) -> list[str]:
    return [m.group("value") for m in ATTRIBUTE[attribute].finditer(text)]


def check_upgradecode(
    name: str, where: str, source_text: str, defines: dict[str, str], shipped: dict[str, str]
) -> list[str]:
    """Both rules for one UpgradeCode: the recorded value, and that it is used."""
    problems: list[str] = []
    key = UPGRADECODES[name][0]

    recorded = shipped.get(key)
    if not recorded:
        problems.append(
            f"the record has no '{key}' entry, so the guard cannot confirm that "
            "this build will upgrade existing installs"
        )
    elif not GUID.match(recorded):
        problems.append(f"the recorded {key} identity is not a GUID: {recorded!r}")

    defined = defines.get(name)
    if defined is not None:
        if not GUID.match(defined):
            problems.append(f"{name} is not a GUID: {defined!r}")
        elif recorded and GUID.match(recorded) and normalise(defined) != normalise(recorded):
            problems.append(
                f"{key} UpgradeCode drifted: the sources say {{{normalise(defined)}}} "
                f"but the shipped identity is {{{normalise(recorded)}}}"
            )

    # The value above is only the identity if the package actually uses it. A
    # literal in the source would leave this guard happily checking a file the
    # compiler ignores - which is the worst of both worlds, since the guard's
    # whole purpose is to fail the build.
    values = attribute_values(source_text, "UpgradeCode")
    wanted = f"$(var.{name})"
    if len(values) != 1:
        problems.append(
            f"{where} sets UpgradeCode {len(values)} times; it must set it exactly "
            f"once, to {wanted}"
        )
    elif values[0].strip() != wanted:
        problems.append(
            f"{where} sets UpgradeCode=\"{values[0].strip()}\" instead of {wanted}, "
            "so the identity this guard checks is not the one being compiled"
        )
    return problems


def check_productcode(where: str, source_text: str) -> list[str]:
    """The opposite rule: the ProductCode must not be pinned to anything."""
    values = attribute_values(source_text, "ProductCode")
    if not values:
        # How WiX is meant to be told: generate one per build.
        return []
    if len(values) > 1:
        return [f"{where} sets ProductCode {len(values)} times; it must not be set at all"]

    value = values[0].strip()
    if value in GENERATED:
        return []

    detail = (
        "a preprocessor variable, which is still one fixed value across versions"
        if value.startswith("$(")
        else "a fixed value"
    )
    return [
        f"{where} pins ProductCode=\"{value}\" ({detail}). It has to be generated "
        "per build - remove the attribute, or set it to \"*\" - or every version "
        "becomes the same product to Windows and upgrades turn into repairs of "
        "whatever is already installed"
    ]


def check(identities: str, msi: str, bundle: str, shipped_text: str) -> list[str]:
    """Problems that must stop the build; empty means the identities are correct."""
    problems: list[str] = []

    defines, define_problems = read_defines(identities)
    problems += define_problems
    shipped, shipped_problems = read_shipped(shipped_text)
    problems += shipped_problems

    sources = {"Synapic.Msi.wxs": msi, "Synapic.Bundle.wxs": bundle}
    for name, (_, where) in UPGRADECODES.items():
        problems += check_upgradecode(name, where, sources[where], defines, shipped)

    problems += check_productcode("Synapic.Msi.wxs", msi)

    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    parser.add_argument(
        "--identities",
        default=str(DEFAULT_IDENTITIES),
        help="WiX include that defines the UpgradeCodes",
    )
    parser.add_argument("--msi", default=str(DEFAULT_MSI), help="the MSI source")
    parser.add_argument("--bundle", default=str(DEFAULT_BUNDLE), help="the bundle source")
    parser.add_argument(
        "--shipped",
        default=str(DEFAULT_SHIPPED),
        help="record of the UpgradeCodes that have already shipped",
    )
    args = parser.parse_args(argv)

    try:
        identities = Path(args.identities).read_text(encoding="utf-8-sig")
        msi = Path(args.msi).read_text(encoding="utf-8-sig")
        bundle = Path(args.bundle).read_text(encoding="utf-8-sig")
        shipped = Path(args.shipped).read_text(encoding="utf-8-sig")
    except OSError as exc:
        print(
            f"error: the installer identity guard could not read its inputs ({exc})",
            file=sys.stderr,
        )
        return 2

    problems = check(identities, msi, bundle, shipped)
    if not problems:
        print("Installer identities are correct (UpgradeCodes match, ProductCode generated)")
        return 0

    print(
        "error: refusing to build the WiX installers - an identity changed.",
        file=sys.stderr,
    )
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    print(
        "\nUpgradeCode and ProductCode have opposite rules, which is what makes them\n"
        "easy to get backwards:\n"
        "\n"
        "  UpgradeCode  the product family - the same in every version, so Windows\n"
        "               recognises an upgrade instead of a second application.\n"
        "  ProductCode  one particular build - new in every version, so Windows\n"
        "               replaces the old one instead of repairing it.\n"
        "\n"
        "Neither mistake fails a build, installs anything wrong, or shows up until\n"
        "someone who has the previous version tries to move to this one.\n"
        f"\n"
        f"If an UpgradeCode change is intended, update {args.shipped} in the same\n"
        "commit so the decision shows up in review, and tell users they have to\n"
        "uninstall the previous version first.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
