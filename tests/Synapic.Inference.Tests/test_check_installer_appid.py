"""Tests for build/check-installer-appid.py.

The guard is the only thing standing between an AppId edit and every existing
install being stranded (Inno appends to an uninstall log only when the AppIds
match, and it names the Uninstall registry key after it), so the rules are
exercised directly rather than only through a real installer build - which needs
Inno Setup and a Windows runner.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "build" / "check-installer-appid.py"

SHIPPED = "{8A7C2C31-5E0D-4B21-9C4F-SYNAPICNET01}"

# Inno escapes a literal "{" by doubling it, so this is what the real script says
# while the effective AppId is SHIPPED. Written as a plain (non-f) string on
# purpose: the doubling has to survive verbatim.
ISS = """; a banner comment before any section
#define AppName "Synapic"

[Setup]
; AppId is mentioned in this comment and must not be mistaken for a directive.
; AppId={{DEADBEEF-DEAD-BEEF-DEAD-BEEFDEADBEEF}
AppId={{8A7C2C31-5E0D-4B21-9C4F-SYNAPICNET01}
AppName={#AppName}
DefaultDirName={autopf}\\Synapic

[Files]
Source: "payload\\*"; DestDir: "{app}"
"""


def _load_module():
    spec = importlib.util.spec_from_file_location("check_installer_appid", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def guard():
    return _load_module()


# ── The repository's own files ───────────────────────────────────────────────


def test_the_repository_script_matches_the_shipped_record(guard):
    """The real pair must agree, or packaging is broken right now."""
    assert guard.check(
        SCRIPT.parent.joinpath("installer-windows.iss").read_text(encoding="utf-8-sig"),
        guard.read_shipped(
            SCRIPT.parent.joinpath("installer-appid.txt").read_text(encoding="utf-8-sig")
        ),
    ) == []


def test_the_record_holds_the_value_that_shipped(guard):
    shipped = guard.read_shipped(
        SCRIPT.parent.joinpath("installer-appid.txt").read_text(encoding="utf-8-sig")
    )
    assert shipped == SHIPPED


def test_main_exits_zero_for_the_repository(guard):
    assert guard.main([]) == 0


# ── Parsing ──────────────────────────────────────────────────────────────────


def test_reads_the_appid_from_the_setup_section(guard):
    appid, problem = guard.read_appid(ISS)
    assert problem is None
    assert appid == SHIPPED


def test_a_commented_appid_is_not_a_directive(guard):
    """The script's own comments name AppId; only the directive may count."""
    appid, problem = guard.read_appid(ISS)
    assert problem is None
    assert "DEADBEEF" not in appid


def test_double_brace_is_resolved_to_a_literal_brace(guard):
    assert guard.unescape_inno_literal("{{8A7C2C31}") == "{8A7C2C31}"
    assert guard.unescape_inno_literal("{#SomeDefine}") == "{#SomeDefine}"


def test_an_appid_outside_setup_is_ignored(guard):
    text = '[Setup]\nAppId=Real\n\n[Files]\nAppId=Decoy\n'
    appid, problem = guard.read_appid(text)
    assert problem is None
    assert appid == "Real"


@pytest.mark.parametrize("header", ["[Setup]", "[setup]", "[ SETUP ]"])
def test_section_header_is_matched_case_insensitively(guard, header):
    appid, problem = guard.read_appid(f"{header}\nAppId=Real\n")
    assert problem is None
    assert appid == "Real"


# ── Values the guard cannot verify ───────────────────────────────────────────


def test_missing_appid_is_reported(guard):
    appid, problem = guard.read_appid("[Setup]\nAppName=Synapic\n")
    assert appid is None
    assert "no AppId directive" in problem


def test_duplicate_appid_is_reported(guard):
    appid, problem = guard.read_appid("[Setup]\nAppId=One\nAppId=Two\n")
    assert appid is None
    assert "2 times" in problem


def test_a_preprocessor_constant_is_refused_rather_than_guessed(guard):
    appid, problem = guard.read_appid("[Setup]\nAppId={#SomeDefine}\n")
    assert appid is None
    assert "preprocessor constant" in problem


# ── The verdicts ─────────────────────────────────────────────────────────────


def test_matching_values_pass(guard):
    assert guard.check(ISS, SHIPPED) == []


def test_a_drifted_appid_is_refused(guard):
    problems = guard.check(ISS.replace(SHIPPED, "{00000000-0000-0000-0000-000000000000}"), SHIPPED)
    assert any("AppId drifted" in p for p in problems)
    # Both values must appear, so the failure alone says what to revert.
    assert any(SHIPPED in p and "00000000" in p for p in problems)


def test_a_missing_record_is_refused(guard):
    problems = guard.check(ISS, None)
    assert any("missing or empty" in p for p in problems)


def test_an_unreadable_appid_never_passes(guard):
    """A script the guard cannot parse must not be waved through."""
    assert guard.check("[Setup]\nAppName=Synapic\n", SHIPPED) != []


def test_the_record_ignores_comments_and_blank_lines(guard):
    assert guard.read_shipped("# a note\n\n  \n" + SHIPPED + "\n# trailing\n") == SHIPPED


def test_an_empty_record_is_none(guard):
    assert guard.read_shipped("# only a comment\n\n") is None


# ── Command line ─────────────────────────────────────────────────────────────


def test_cli_refuses_a_drifted_script(guard, tmp_path, capsys):
    drifted = tmp_path / "drifted.iss"
    drifted.write_text(
        ISS.replace(SHIPPED, "{00000000-0000-0000-0000-000000000000}"), encoding="utf-8"
    )
    exit_code = guard.main(
        ["--iss", str(drifted), "--shipped", str(SCRIPT.parent / "installer-appid.txt")]
    )
    assert exit_code == 1
    message = capsys.readouterr().err.lower()
    assert "refusing to build" in message
    assert "second" in message and "add/remove programs" in message


def test_cli_reports_a_missing_script_as_a_guard_failure(guard, tmp_path):
    assert guard.main(["--iss", str(tmp_path / "absent.iss")]) == 2
