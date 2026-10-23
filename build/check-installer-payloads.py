#!/usr/bin/env python3
"""Refuse to build either installer when their payload definitions disagree.

Usage:
    check-installer-payloads.py [--iss <iss>] [--wxs <wxs>]
                                [--inno-script <ps1>] [--msi-script <ps1>]

Why this exists
---------------
Both Windows installers package the same folder - ``artifacts/win-x64`` - and
they do it the same way: one wildcard plus a list of exclusions. That is the
right design (a dependency added to the app reaches both packages with no list
to regenerate), but it means the two definitions can silently drift apart, and
neither compiler objects.

The drift is not hypothetical. The Inno setup exe is written *into* that payload
folder by ``package-windows.ps1``, so the .iss excludes ``Synapic-Setup-*.exe``
to keep a rebuild from embedding the previous 300 MB installer. The WiX source
never did, because for its whole life it had only been built against a payload
that did not contain an Inno output - so the first MSI built after a release
would have installed a copy of the other installer into Program Files.

This guard compares the two definitions before either package is compiled:

* the payload root each one globs,
* the exclusion patterns, as sets,
* that both scripts name the three files without which the package would ship a
  service that cannot run (``Synapic.Indexer.exe``, ``Synapic.Core.dll``,
  ``synapic-inference.exe``).

Exit codes: 0 = the definitions agree, 1 = refuse to build, 2 = the guard could
not run (an input was missing or unreadable).
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_ISS = REPO_ROOT / "build" / "installer-windows.iss"
DEFAULT_WXS = REPO_ROOT / "build" / "wix" / "Synapic.Msi.wxs"
DEFAULT_INNO_SCRIPT = REPO_ROOT / "build" / "package-windows.ps1"
DEFAULT_MSI_SCRIPT = REPO_ROOT / "build" / "package-windows-msi.ps1"

# The files that make the difference between a package that ships the background
# indexer and one that ships an executable that cannot find its engine.
SERVICE_FILES = ("Synapic.Indexer.exe", "Synapic.Core.dll", "synapic-inference.exe")

INNO_DEFINE = re.compile(
    r"^#define\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s+"
    r"(?:\\\"(?P<quoted>[^\\\"]*)\\\"|\"(?P<plain>[^\"]*)\")",
    re.MULTILINE,
)

# The wildcard entry in [Files]: Source: "{#ArtifactsDir}\*"; ... Excludes: "..."
# The Excludes list is searched for inside the captured line rather than in the
# same pattern: as an optional group after a non-greedy match it is always
# skipped, which reads as "the .iss excludes nothing" and fails the guard for
# the wrong reason.
INNO_FILES_ENTRY = re.compile(r'^Source:\s*"(?P<source>[^"]*)"(?P<rest>[^\n]*)', re.MULTILINE)
INNO_EXCLUDES = re.compile(r'Excludes:\s*"(?P<excludes>[^"]*)"')

WXS_INCLUDE = re.compile(r'<Files\s+Include="(?P<include>[^"]+)"')
WXS_EXCLUDE = re.compile(r'<Exclude\s+Files="(?P<files>[^"]+)"')


def inno_defines(text: str) -> dict[str, str]:
    defines: dict[str, str] = {}
    for match in INNO_DEFINE.finditer(text):
        value = match.group("quoted")
        if value is None:
            value = match.group("plain")
        defines[match.group("name")] = value
    return defines


def expand_inno(value: str, defines: dict[str, str]) -> str:
    """Replace {#Name} with the value the .iss defines for it."""
    return re.sub(r"\{#([A-Za-z_][A-Za-z0-9_]*)\}", lambda m: defines.get(m.group(1), m.group(0)), value)


def patterns(value: str) -> set[str]:
    """Exclusion patterns as comparable names: lower case, no directory part."""
    names = set()
    for raw in value.split(","):
        part = raw.strip().strip('"')
        if not part:
            continue
        # WiX spells them as $(var.PayloadDir)\**\*.pdb; Inno as *.pdb.
        names.add(part.replace("\\", "/").rsplit("/", 1)[-1].lower())
    return names


def inno_payload(text: str) -> tuple[str, set[str], list[str]]:
    """Root, exclusion patterns and problems for the Inno payload definition."""
    problems: list[str] = []
    defines = inno_defines(text)
    entries = list(INNO_FILES_ENTRY.finditer(text))
    root_entries = [e for e in entries if e.group("source").strip().endswith("\\*")]
    if not root_entries:
        return "", set(), [
            "the .iss has no [Files] entry globbing the payload root "
            '(a line like Source: "{#ArtifactsDir}\\*")'
        ]
    if len(root_entries) > 1:
        problems.append(f"the .iss globs a payload root {len(root_entries)} times; expected one")

    entry = root_entries[0]
    root = expand_inno(entry.group("source"), defines)
    excludes_match = INNO_EXCLUDES.search(entry.group("rest"))
    excludes = patterns(expand_inno(excludes_match.group("excludes") if excludes_match else "", defines))

    # The runtime name is a #define; if it did not expand, the two files disagree
    # in a way this guard cannot compare and the build should stop here.
    unresolved = {p for p in excludes if "{" in p}
    if unresolved:
        problems.append(f"the .iss Excludes still contains unresolved defines: {sorted(unresolved)}")

    return root, excludes, problems


def wxs_payload(text: str) -> tuple[str, set[str], list[str]]:
    """Root, exclusion patterns and problems for the WiX payload definition."""
    problems: list[str] = []
    includes = WXS_INCLUDE.findall(text)
    if len(includes) != 1:
        problems.append(
            f"the MSI source globs a payload root {len(includes)} times; expected exactly one <Files Include=...>"
        )
    root = includes[0] if includes else ""
    excludes: set[str] = set()
    for match in WXS_EXCLUDE.finditer(text):
        excludes |= patterns(match.group("files"))
    return root, excludes, problems


def checked_files(text: str) -> set[str]:
    """The payload files a packaging script names, whichever way it tests them."""
    return {name for name in SERVICE_FILES if name in text}


def check(iss: str, wxs: str, inno_script: str, msi_script: str) -> list[str]:
    problems: list[str] = []

    inno_root, inno_excludes, inno_problems = inno_payload(iss)
    wxs_root, wxs_excludes, wxs_problems = wxs_payload(wxs)
    problems += inno_problems + wxs_problems

    # The roots are spelled through different variables ({#ArtifactsDir} vs
    # $(var.PayloadDir)), so their text cannot be compared. What matters is that
    # each one globs the folder its own script passes in, and that both scripts
    # pass the same $ArtifactsDir - checked below.
    if inno_root and not inno_root.endswith("\\*"):
        problems.append(f"the .iss payload root is not a wildcard of the folder: '{inno_root}'")
    if wxs_root and not wxs_root.endswith("\\**"):
        problems.append(f"the MSI payload root is not a recursive wildcard: '{wxs_root}'")

    # Both packaging scripts must hand their compiler the same folder: the .iss a
    # /DArtifactsDir define, the MSI a -d PayloadDir. A mismatch here is the
    # quietest possible drift - both build, and they install different things.
    if re.search(r'/DArtifactsDir=\$ArtifactsDir', inno_script) is None:
        problems.append("package-windows.ps1 no longer passes /DArtifactsDir=$ArtifactsDir to ISCC")
    if re.search(r'-d\s+"PayloadDir=\$ArtifactsDir"', msi_script) is None:
        problems.append('package-windows-msi.ps1 no longer passes -d "PayloadDir=$ArtifactsDir" to wix')

    only_inno = sorted(inno_excludes - wxs_excludes)
    only_wxs = sorted(wxs_excludes - inno_excludes)
    for name in only_inno:
        problems.append(
            f"the .iss excludes '{name}' but the MSI source does not - the MSI would "
            "install that file"
        )
    for name in only_wxs:
        problems.append(
            f"the MSI source excludes '{name}' but the .iss does not - Inno would install "
            "that file"
        )

    for script, label in ((inno_script, "package-windows.ps1"), (msi_script, "package-windows-msi.ps1")):
        missing = [name for name in SERVICE_FILES if name not in checked_files(script)]
        if missing:
            problems.append(f"{label} never names {', '.join(missing)}; a payload missing it would package silently")

    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    parser.add_argument("--iss", default=str(DEFAULT_ISS), help="the Inno Setup script")
    parser.add_argument("--wxs", default=str(DEFAULT_WXS), help="the WiX MSI source")
    parser.add_argument("--inno-script", default=str(DEFAULT_INNO_SCRIPT), help="the Inno packaging script")
    parser.add_argument("--msi-script", default=str(DEFAULT_MSI_SCRIPT), help="the MSI packaging script")
    args = parser.parse_args(argv)

    try:
        iss = Path(args.iss).read_text(encoding="utf-8-sig")
        wxs = Path(args.wxs).read_text(encoding="utf-8-sig")
        inno_script = Path(args.inno_script).read_text(encoding="utf-8-sig")
        msi_script = Path(args.msi_script).read_text(encoding="utf-8-sig")
    except OSError as exc:
        print(f"error: the installer payload guard could not read its inputs ({exc})", file=sys.stderr)
        return 2

    problems = check(iss, wxs, inno_script, msi_script)
    if not problems:
        print("Installer payloads agree (same folder, same exclusions, same service files checked)")
        return 0

    print("error: refusing to build an installer - the two payload definitions disagree.", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    print(
        "\nBoth installers glob the same publish folder, so an exclusion added to one and\n"
        "not the other produces a package that installs a file the other was careful to\n"
        "leave out. Change build/installer-windows.iss and build/wix/Synapic.Msi.wxs in\n"
        "the same commit.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
