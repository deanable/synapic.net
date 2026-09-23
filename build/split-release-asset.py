#!/usr/bin/env python3
"""Split a release asset that is too big for GitHub's 2 GiB per-file cap.

Usage:
    split-release-asset.py <asset> [--out-dir DIR] [--max-part-mib N] [--keep-original]

Why this exists
---------------
GitHub Releases accept at most 1000 assets, but "each file included in a release
must be under 2 GiB" (there is no cap on the *total* size of a release). The CPU
sidecar is ~216 MB and uploads as one file; the CUDA sidecar is ~2.5 GB, so
``softprops/action-gh-release`` rejects it and the whole release fails.

Splitting is therefore not a packaging preference, it is the only way to publish
the CUDA build at all. This script splits the bundle into fixed-size parts and
drops a reassembly helper (``reassemble-<name>.bat`` on Windows, ``.sh``
elsewhere) next to them that concatenates the parts back byte-for-byte and
prints the expected SHA-256 for the user to compare.

Behaviour
---------
* ``asset`` larger than the part size -> write ``<name>.part1 .. <name>.partN``
  (each strictly under the cap) plus the reassembly helper. The oversized
  original is removed from ``--out-dir`` unless ``--keep-original`` is given, so
  a directory uploaded wholesale can never contain an invalid asset.
* ``asset`` that already fits -> nothing is split; the file is copied into
  ``--out-dir`` so callers can stage and upload one directory either way.
* Parts are raw byte ranges, so reassembly needs no archiver: Windows' ``copy
  /b`` and POSIX ``cat`` are enough.

Exit codes: 0 = staged (split or copied), 2 = usage or I/O problem.
"""

from __future__ import annotations

import argparse
import hashlib
import shutil
import sys
from pathlib import Path

# GitHub's documented ceiling is "under 2 GiB" per asset. Parts are cut below
# that with margin so the *uploaded* size (and any proxy overhead) stays legal.
GITHUB_ASSET_LIMIT_BYTES = 2 * 1024**3
DEFAULT_MAX_PART_MIB = 1800
CHUNK = 8 * 1024 * 1024


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(CHUNK), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def split_file(src: Path, out_dir: Path, max_part_bytes: int) -> list[Path]:
    """Copy ``src`` into ``<out_dir>/<name>.partN`` byte ranges."""
    parts: list[Path] = []
    with src.open("rb") as source:
        index = 1
        while True:
            written = 0
            part = out_dir / f"{src.name}.part{index}"
            with part.open("wb") as sink:
                while written < max_part_bytes:
                    block = source.read(min(CHUNK, max_part_bytes - written))
                    if not block:
                        break
                    sink.write(block)
                    written += len(block)
            if written == 0:
                part.unlink()  # exact multiple of the part size - drop the empty tail
                break
            parts.append(part)
            index += 1
            if written < max_part_bytes:
                break
    return parts


def reassembly_helper(target: Path, parts: list[Path], expected: str) -> str:
    """Script text that rejoins ``parts`` into ``target`` and verifies the hash."""
    names = " ".join(f'"{p.name}"' for p in parts)

    if target.suffix.lower() == ".exe":
        joined = "+".join(f'"%NAME%.part{i}"' for i in range(1, len(parts) + 1))
        return (
            "@echo off\r\n"
            f"REM Reassemble {target.name} from its {len(parts)} part(s).\r\n"
            "REM Run this in the folder holding every part.\r\n"
            "setlocal\r\n"
            'cd /d "%~dp0"\r\n'
            f'set "NAME={target.name}"\r\n'
            'if exist "%NAME%" del "%NAME%"\r\n'
            f'copy /b {joined} "%NAME%" >nul || goto fail\r\n'
            "echo.\r\n"
            'echo Joined "%NAME%"\r\n'
            f"echo Expected SHA-256: {expected}\r\n"
            "echo Actual SHA-256:\r\n"
            'certutil -hashfile "%NAME%" SHA256 | findstr /v ":"\r\n'
            "exit /b 0\r\n"
            ":fail\r\n"
            f"echo Reassembly failed - make sure all {len(parts)} parts ({names}) are in this folder.\r\n"
            "exit /b 1\r\n"
        )

    return (
        "#!/usr/bin/env bash\n"
        f"# Reassemble {target.name} from its {len(parts)} part(s).\n"
        "# Run this in the folder holding every part.\n"
        "set -euo pipefail\n"
        'cd "$(dirname "$0")"\n'
        f'NAME="{target.name}"\n'
        + "".join(f'cat "$NAME.part{i}" > "$NAME"\n' if i == 1
                  else f'cat "$NAME.part{i}" >> "$NAME"\n'
                  for i in range(1, len(parts) + 1))
        + 'chmod +x "$NAME"\n'
        'echo "Joined $NAME"\n'
        f'echo "Expected SHA-256: {expected}"\n'
        'if command -v sha256sum >/dev/null 2>&1; then sha256sum "$NAME"; else shasum -a 256 "$NAME"; fi\n'
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Split a release asset into <2 GiB parts with a reassembly helper.")
    parser.add_argument("asset", help="the release asset to stage (artifact/<rid>/synapic-inference*)")
    parser.add_argument("--out-dir", default="standalone",
                        help="directory the release uploads wholesale (default: standalone)")
    parser.add_argument("--max-part-mib", type=int, default=DEFAULT_MAX_PART_MIB,
                        help=f"part size in MiB, must stay under 2 GiB (default: {DEFAULT_MAX_PART_MIB})")
    parser.add_argument("--keep-original", action="store_true",
                        help="keep the oversized original in --out-dir (it cannot be uploaded as an asset)")
    args = parser.parse_args(argv[1:])

    max_part_bytes = args.max_part_mib * 1024 * 1024
    if max_part_bytes >= GITHUB_ASSET_LIMIT_BYTES:
        print(f"error: --max-part-mib {args.max_part_mib} would allow parts at or above the "
              f"2 GiB release asset cap", file=sys.stderr)
        return 2

    src = Path(args.asset)
    if not src.is_file():
        print(f"error: no such asset: {src}", file=sys.stderr)
        return 2

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    size = src.stat().st_size

    if size <= max_part_bytes:
        staged = out_dir / src.name
        if src.resolve() != staged.resolve():
            shutil.copy2(src, staged)
        print(f"{src.name} is {size / 1024 / 1024:.1f} MiB - under the "
              f"{args.max_part_mib} MiB part size, uploads as a single asset")
        return 0

    # Drop any stale parts from an earlier run so the upload cannot mix sizes.
    for stale in sorted(out_dir.glob(f"{src.name}.part*")):
        stale.unlink()

    parts = split_file(src, out_dir, max_part_bytes)
    expected = sha256_file(src)
    helper = out_dir / f"reassemble-{src.name}{'.bat' if src.suffix.lower() == '.exe' else '.sh'}"
    helper.write_text(reassembly_helper(src, parts, expected), encoding="utf-8", newline="")
    helper.chmod(0o755)

    removed = False
    if not args.keep_original:
        # Only ever touch a copy that sits inside the upload directory: an
        # oversized file there would be rejected as a release asset (and fail
        # the whole release), while the caller's own build output is left alone.
        staged = out_dir / src.name
        if staged.exists() and staged.parent.resolve() == out_dir.resolve():
            staged.unlink()
            removed = True

    print(f"{src.name} is {size / 1024**3:.2f} GiB - larger than the "
          f"{args.max_part_mib} MiB part size, split into {len(parts)} parts"
          f" (GitHub rejects any release asset of 2 GiB or more)")
    print(f"  SHA-256 (reassembled): {expected}")
    for part in parts:
        print(f"  {part.name}: {part.stat().st_size / 1024**2:.1f} MiB")
    print(f"  reassembly helper: {helper.name}")
    if removed:
        print(f"  removed the oversized {src.name} from {out_dir} - only the parts are uploadable")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
