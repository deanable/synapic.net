"""Tests for build/split-release-asset.py.

The CUDA sidecar is ~2.5 GB while GitHub refuses any release asset of 2 GiB or
more, so this splitter is the only way that bundle reaches the releases page.
If it is wrong the release either fails outright (asset rejected) or publishes
parts that do not rejoin into a working server - so byte-exactness, the emitted
reassembly helper, and the safety of what is left in the upload directory are
all tested here rather than discovered on a user's machine.
"""

from __future__ import annotations

import hashlib
import importlib.util
import shutil
import subprocess
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "build" / "split-release-asset.py"

MIB = 1024 * 1024


def _load_module():
    spec = importlib.util.spec_from_file_location("split_release_asset", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def splitter():
    return _load_module()


@pytest.fixture
def oversized(tmp_path):
    """A 2.5 MiB stand-in for the CUDA bundle (split at 1 MiB -> 3 parts)."""
    asset = tmp_path / "synapic-inference-win-x64-cuda.exe"
    asset.write_bytes(bytes(range(256)) * (int(2.5 * MIB) // 256))
    return asset


def run(splitter, *args):
    return splitter.main(["split-release-asset.py", *[str(a) for a in args]])


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def test_parts_rejoin_byte_for_byte(splitter, oversized, tmp_path, capsys):
    out = tmp_path / "standalone"
    assert run(splitter, oversized, "--out-dir", out, "--max-part-mib", "1") == 0
    capsys.readouterr()

    parts = sorted(out.glob("*.part*"), key=lambda p: int(p.name.rsplit("part", 1)[1]))
    assert [p.name for p in parts] == [
        "synapic-inference-win-x64-cuda.exe.part1",
        "synapic-inference-win-x64-cuda.exe.part2",
        "synapic-inference-win-x64-cuda.exe.part3",
    ]
    assert b"".join(p.read_bytes() for p in parts) == oversized.read_bytes()


def test_parts_are_exact_byte_ranges(splitter, oversized, tmp_path, capsys):
    out = tmp_path / "standalone"
    run(splitter, oversized, "--out-dir", out, "--max-part-mib", "1")
    capsys.readouterr()

    original = oversized.read_bytes()
    part1 = out / "synapic-inference-win-x64-cuda.exe.part1"
    part2 = out / "synapic-inference-win-x64-cuda.exe.part2"
    part3 = out / "synapic-inference-win-x64-cuda.exe.part3"

    assert part1.read_bytes() == original[:MIB]
    assert part2.read_bytes() == original[MIB:2 * MIB]
    assert part3.read_bytes() == original[2 * MIB:]


def test_no_part_reaches_the_github_asset_cap(splitter, oversized, tmp_path, capsys):
    out = tmp_path / "standalone"
    run(splitter, oversized, "--out-dir", out, "--max-part-mib", "1")
    capsys.readouterr()

    for part in out.glob("*.part*"):
        assert part.stat().st_size < 2 * 1024**3


def test_oversized_original_is_removed_from_the_upload_dir(splitter, oversized, tmp_path, capsys):
    """An oversized file in the upload dir fails the entire release, not one asset."""
    out = tmp_path / "standalone"
    out.mkdir()
    staged = out / oversized.name
    shutil.copy2(oversized, staged)

    assert run(splitter, staged, "--out-dir", out, "--max-part-mib", "1") == 0
    capsys.readouterr()

    assert not staged.exists()
    assert (out / f"{staged.name}.part1").exists()
    # The caller's own build output is never touched.
    assert oversized.exists()


def test_keep_original_leaves_the_staged_copy(splitter, oversized, tmp_path, capsys):
    out = tmp_path / "standalone"
    out.mkdir()
    staged = out / oversized.name
    shutil.copy2(oversized, staged)

    run(splitter, staged, "--out-dir", out, "--max-part-mib", "1", "--keep-original")
    capsys.readouterr()

    assert staged.exists()
    assert (out / f"{staged.name}.part1").exists()


def test_asset_under_the_cap_is_staged_whole(splitter, tmp_path, capsys):
    small = tmp_path / "synapic-inference-win-x64.exe"
    small.write_bytes(b"cpu sidecar" * 1024)
    out = tmp_path / "standalone"

    assert run(splitter, small, "--out-dir", out, "--max-part-mib", "1") == 0
    stdout = capsys.readouterr().out

    assert (out / small.name).read_bytes() == small.read_bytes()
    assert list(out.glob("*.part*")) == []
    assert "single asset" in stdout


def test_part_size_at_or_above_the_cap_is_rejected(splitter, oversized, tmp_path, capsys):
    assert run(splitter, oversized, "--out-dir", tmp_path / "standalone",
               "--max-part-mib", "2048") == 2
    assert "2 GiB" in capsys.readouterr().err
    assert list((tmp_path / "standalone").glob("*")) == []


def test_missing_asset_is_an_error(splitter, tmp_path, capsys):
    assert run(splitter, tmp_path / "nope.exe", "--out-dir", tmp_path / "standalone") == 2
    assert "no such asset" in capsys.readouterr().err


def test_stale_parts_from_an_earlier_run_are_cleared(splitter, tmp_path, capsys):
    out = tmp_path / "standalone"
    asset = tmp_path / "synapic-inference-win-x64-cuda.exe"
    asset.write_bytes(b"x" * int(2.5 * MIB))
    run(splitter, asset, "--out-dir", out, "--max-part-mib", "1")
    capsys.readouterr()
    assert (out / f"{asset.name}.part3").exists()

    asset.write_bytes(b"y" * int(1.5 * MIB))
    run(splitter, asset, "--out-dir", out, "--max-part-mib", "1")
    capsys.readouterr()

    assert not (out / f"{asset.name}.part3").exists()
    assert (out / f"{asset.name}.part2").exists()


def test_windows_asset_gets_a_batch_helper_that_lists_every_part(splitter, oversized, tmp_path, capsys):
    out = tmp_path / "standalone"
    run(splitter, oversized, "--out-dir", out, "--max-part-mib", "1")
    capsys.readouterr()

    helper = out / f"reassemble-{oversized.name}.bat"
    text = helper.read_text(encoding="utf-8", newline="")

    assert helper.exists()
    assert not (out / f"reassemble-{oversized.name}.sh").exists()
    assert 'copy /b "%NAME%.part1"+"%NAME%.part2"+"%NAME%.part3" "%NAME%"' in text
    assert f"Expected SHA-256: {sha256(oversized)}" in text
    # cmd.exe wants CRLF; a lone LF still parses but is not the native format.
    assert "\r\n" in text and "\n" not in text.replace("\r\n", "")


def test_posix_asset_gets_a_shell_helper_that_reproduces_the_file(splitter, tmp_path, capsys):
    if shutil.which("bash") is None:
        pytest.skip("bash is not available on this machine")
    asset = tmp_path / "synapic-inference-linux-x64"
    asset.write_bytes(bytes(range(256)) * (int(2.5 * MIB) // 256))
    out = tmp_path / "standalone"
    run(splitter, asset, "--out-dir", out, "--max-part-mib", "1")
    capsys.readouterr()

    helper = out / f"reassemble-{asset.name}.sh"
    assert helper.exists()
    assert not (out / f"reassemble-{asset.name}.bat").exists()

    result = subprocess.run(["bash", str(helper)], cwd=out, capture_output=True, text=True)

    assert result.returncode == 0, result.stderr
    assert sha256(out / asset.name) == sha256(asset)
    assert f"Expected SHA-256: {sha256(asset)}" in result.stdout
