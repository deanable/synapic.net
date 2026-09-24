#!/usr/bin/env python3
"""Check the Synapic HTML Help sources before they are compiled into Synapic.chm.

HTML Help Workshop fails quietly. A topic that is missing from [FILES] is
compiled out of the .chm without a word; a link to a topic that does not exist
still ships, and only the user finds it. This script is the guard that makes
those mistakes loud, and it runs from build-chm.ps1 before every compile.

It checks:
  * every [FILES] entry exists on disk;
  * every help source on disk is either listed in [FILES] or explicitly
    excluded (so a new topic cannot silently fall out of the .chm);
  * every local link in a topic, in the Contents and in the Index resolves to a
    file that exists, including the #fragment when one is used;
  * every topic is reachable from the Contents, the Index, or another topic;
  * every source is pure ASCII (the CHM viewer is MSHTML-era: entities only)
    and every topic declares its charset;
  * the project's Contents, Index and Default topic point at real files.

Usage:  python docs/help/check-help.py
Exit:   0 when the sources are consistent, 1 when they are not.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "Synapic.hhp"

# Help sources that are deliberately NOT compiled into the .chm: the authoring
# guide, the tools, and the project files themselves (hhc.exe reads the .hhp,
# .hhc and .hhk directly; they are build inputs, not published content).
NOT_IN_CHM = {
    "README.md",
    "build-chm.ps1",
    "check-help.py",
    "Synapic.hhp",
    "Synapic.hhc",
    "Synapic.hhk",
}

SECTION_RE = re.compile(r"^\[(.+)\]$")
LINK_RE = re.compile(r"""(?:href|src)\s*=\s*["']([^"']+)["']""", re.IGNORECASE)
PARAM_RE = re.compile(
    r"""<param\s+name\s*=\s*["'](\w+)["']\s+value\s*=\s*["']([^"']*)["']""", re.IGNORECASE
)
ANCHOR_RE = re.compile(r"""(?:id|name)\s*=\s*["']([^"']+)["']""", re.IGNORECASE)
CHARSET_RE = re.compile(r"""charset\s*=\s*["']?([\w-]+)""", re.IGNORECASE)
SKIP_PREFIXES = ("http://", "https://", "mailto:", "ftp://", "javascript:", "ms-its:")
COLOR = sys.stdout.isatty()


def paint(text: str, code: str) -> str:
    return f"\033[{code}m{text}\033[0m" if COLOR else text


problems: list[str] = []
notes: list[str] = []


def fail(message: str) -> None:
    problems.append(message)


def read_ascii(path: Path) -> str:
    """Return the file as text, reporting any non-ASCII byte as a problem.

    The offending content is still returned (decoded leniently) so that one bad
    byte does not hide every other problem in the same file.
    """
    data = path.read_bytes()
    bad = next((offset for offset, byte in enumerate(data) if byte > 0x7F), None)
    if bad is not None:
        line = data.count(b"\n", 0, bad) + 1
        fail(
            f"{path.name}: non-ASCII byte 0x{data[bad]:02x} on line {line} "
            f"(use an HTML entity such as &mdash; instead)"
        )
    return data.decode("ascii", errors="replace")


def parse_project(text: str) -> tuple[dict[str, str], list[str]]:
    options: dict[str, str] = {}
    files: list[str] = []
    section = ""
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith(";"):
            continue
        section_match = SECTION_RE.match(line)
        if section_match:
            section = section_match.group(1).upper()
            continue
        if section == "FILES":
            files.append(line.replace("\\", "/"))
            continue
        if "=" in line:
            key, _, value = line.partition("=")
            options[key.strip().lower()] = value.strip()
    return options, files


def split_target(raw: str) -> tuple[str, str]:
    target = raw.strip()
    if "#" in target:
        path, _, fragment = target.partition("#")
        return path, fragment
    return target, ""


def check_reference(origin: str, raw: str, sources: dict[str, str]) -> str | None:
    """Validate one link target; return the referenced file name when local."""
    target = raw.strip()
    if not target or target.startswith("#") or target.lower().startswith(SKIP_PREFIXES):
        return None

    file_part, fragment = split_target(target)
    if not file_part:
        return None

    file_part = file_part.lstrip("./")
    referenced = ROOT / file_part
    if not referenced.is_file():
        fail(f"{origin}: link target does not exist: {raw}")
        return None

    if fragment:
        body = sources.get(file_part)
        if body is None:
            body = read_ascii(referenced)
            sources[file_part] = body
        if f'id="{fragment}"' not in body and f"name=\"{fragment}\"" not in body:
            fail(f"{origin}: link target has no #{fragment} anchor: {raw}")

    return file_part


def main() -> int:
    if not PROJECT.is_file():
        print(paint(f"missing project file: {PROJECT}", "31"))
        return 1

    project_text = read_ascii(PROJECT)
    if "Project file corrupted" in project_text:  # pragma: no cover - defensive
        print(paint("the project file could not be read as ASCII", "31"))
        return 1

    options, listed = parse_project(project_text)

    # --- [FILES] coverage, both directions ---------------------------------
    listed_set = set(listed)
    if len(listed_set) != len(listed):
        seen: set[str] = set()
        for entry in listed:
            if entry in seen:
                fail(f"[FILES] lists {entry} more than once")
            seen.add(entry)

    for entry in listed:
        if not (ROOT / entry).is_file():
            fail(f"[FILES] lists a file that does not exist: {entry}")

    on_disk: list[Path] = []
    for candidate in sorted(ROOT.iterdir()):
        if not candidate.is_file():
            continue
        if candidate.name in NOT_IN_CHM or candidate.suffix.lower() == ".chm":
            continue
        on_disk.append(candidate)

    for candidate in on_disk:
        if candidate.name not in listed_set:
            fail(
                f"{candidate.name} is in this folder but not in [FILES] - it would be "
                f"left out of the compiled help"
            )

    # --- project pointers ---------------------------------------------------
    contents_name = options.get("contents file")
    index_name = options.get("index file")
    default_topic = options.get("default topic")
    for label, name in (
        ("Contents file", contents_name),
        ("Index file", index_name),
        ("Default topic", default_topic),
    ):
        if not name:
            fail(f"[OPTIONS] does not set a {label}")
        elif not (ROOT / name.replace("\\", "/")).is_file():
            fail(f"[OPTIONS] {label} points at a missing file: {name}")

    # Read every source once, so encoding problems surface even for pages that
    # nothing links to.
    sources: dict[str, str] = {}
    for candidate in on_disk:
        if candidate.suffix.lower() in {".html", ".htm", ".css"}:
            sources[candidate.name] = read_ascii(candidate)

    for name, body in sources.items():
        if name.endswith((".html", ".htm")) and "charset" not in body.lower():
            fail(f"{name}: no <meta> charset declaration (the CHM viewer needs it)")

    # --- links in topics ----------------------------------------------------
    reachable: set[str] = set()
    if default_topic:
        reachable.add(default_topic.replace("\\", "/"))

    for name, body in sources.items():
        if not name.endswith((".html", ".htm")):
            continue
        for raw in LINK_RE.findall(body):
            referenced = check_reference(name, raw, sources)
            if referenced and referenced in sources:
                reachable.add(referenced)

    # --- navigation: Contents and Index ------------------------------------
    navigation_entries = 0
    for nav_name, label in ((contents_name, "Contents"), (index_name, "Index")):
        if not nav_name:
            continue
        nav_path = ROOT / nav_name.replace("\\", "/")
        if not nav_path.is_file():
            continue
        body = sources.get(nav_path.name)
        if body is None:
            body = read_ascii(nav_path)
            sources[nav_path.name] = body
        params = PARAM_RE.findall(body)
        if not params:
            fail(f"{nav_name}: no <param> entries - {label} would be empty")
        for param_name, value in params:
            if param_name.lower() != "local":
                continue
            navigation_entries += 1
            referenced = check_reference(nav_name, value, sources)
            if referenced:
                reachable.add(referenced)
        if not any(p[0].lower() == "local" for p in params) and nav_name == contents_name:
            fail(f"{nav_name}: no topic entries point anywhere")

    if navigation_entries == 0:
        fail("neither the Contents nor the Index points at a topic")

    # --- orphans ------------------------------------------------------------
    topics = [p.name for p in on_disk if p.suffix.lower() in {".html", ".htm"}]
    for topic in topics:
        if topic not in reachable:
            notes.append(
                f"{topic} is not linked from the Contents, the Index or any topic - "
                f"a user cannot reach it"
            )
    for note in notes:
        fail(note)

    if problems:
        print(paint(f"{len(problems)} problem(s) in the help sources:", "31"))
        for problem in dict.fromkeys(problems):
            print(f"  - {problem}")
        return 1

    print(
        paint(
            f"help sources OK: {len(topics)} topics, {len(listed)} files in [FILES], "
            f"{navigation_entries} Contents/Index entries, links and anchors resolve",
            "32",
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
