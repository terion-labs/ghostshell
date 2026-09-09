#!/usr/bin/env python3
"""Automate bounded packaged Asura acceptance on Linux arm64 under Xvfb.

This is intentionally not a substitute for physical-host acceptance. It drives the
packaged desktop through X11, sends input through the real managed renderer, and
records which observations remain outside an Xvfb container's reach.
"""

from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import secrets
import shlex
import signal
import subprocess
import time


SYSTEM_NAME = "docker-linux-arm64-xvfb-openbox"
WINDOW_WAIT_SECONDS = 20.0
FILE_WAIT_SECONDS = 10.0


@dataclass(frozen=True, slots=True)
class CheckResult:
    id: str
    title: str
    result: str
    notes: str
    evidence: tuple[str, ...] = ()


def atomic_write_text(path: Path, content: str) -> None:
    """Publish one receipt file without exposing a partially written document."""
    temporary = path.with_name(
        f".{path.name}.{os.getpid()}.{secrets.token_hex(6)}.tmp"
    )
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as destination:
            destination.write(content)
            destination.flush()
            os.fsync(destination.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def render_markdown(
    evidence: dict[str, object], checks: list[CheckResult]
) -> str:
    lines = [
        "# Asura Linux arm64 Xvfb packaged acceptance",
        "",
        f"- Declared system: `{evidence['declaredSystemName']}`",
        f"- Actual execution host: `{evidence['actualHostName']}`",
        f"- Environment: {evidence['executionEnvironment']}",
        f"- OS: `{evidence['osDescription']}`",
        f"- Package SHA-256: `{evidence['packageSha256'] or 'NOT_AVAILABLE'}`",
        f"- Source snapshot SHA-256: "
        f"`{evidence['sourceSnapshotSha256'] or 'NOT_AVAILABLE'}`",
        f"- Overall: **{evidence['overallResult']}**",
        "",
        "This evidence is deliberately bounded. A passing automated observation "
        "under Xvfb does not imply physical-host, compositor, IME, sleep/wake, or "
        "Windows coverage.",
        "",
        "| Check | Result | Evidence notes |",
        "| --- | --- | --- |",
    ]
    for check in checks:
        notes = check.notes.replace("|", "\\|").replace("\n", "<br>")
        artifacts = ", ".join(f"`{item}`" for item in check.evidence)
        if artifacts:
            notes = f"{notes} Artifacts: {artifacts}."
        lines.append(f"| {check.title} | {check.result} | {notes} |")
    lines.append("")
    return "\n".join(lines)


def publish_receipt(
    output: Path, evidence: dict[str, object], checks: list[CheckResult]
) -> None:
    """Publish Markdown first and evidence.json last as the receipt commit marker."""
    output.mkdir(parents=True, exist_ok=True)
    atomic_write_text(output / "evidence.md", render_markdown(evidence, checks))
    atomic_write_text(
        output / "evidence.json",
        json.dumps(evidence, indent=2, ensure_ascii=False) + "\n",
    )


def validate_evidence_references(
    output: Path, checks: list[CheckResult]
) -> list[CheckResult]:
    """Remove missing references and prevent a PASS without its declared artifacts."""
    validated: list[CheckResult] = []
    for check in checks:
        present: list[str] = []
        missing: list[str] = []
        for relative_name in check.evidence:
            relative_path = Path(relative_name)
            path_is_safe = (
                not relative_path.is_absolute()
                and ".." not in relative_path.parts
                and relative_path.parts
            )
            if path_is_safe and (output / relative_path).is_file():
                present.append(relative_name)
            else:
                missing.append(relative_name)

        result = "FAIL" if missing and check.result == "PASS" else check.result
        notes = check.notes
        if missing:
            notes += (
                " Receipt finalization could not find declared artifact(s): "
                + ", ".join(missing)
                + "."
            )
        validated.append(
            CheckResult(check.id, check.title, result, notes, tuple(present))
        )
    return validated


def unproven_boundary_checks() -> list[CheckResult]:
    return [
        CheckResult(
            "physical-global-hotkey",
            "Physical desktop or compositor global-hotkey behavior",
            "NOT_PROVEN",
            "A successful cross-client passive grab on Xvfb would not prove behavior "
            "under a physical desktop, window manager, compositor, or desktop shortcut "
            "policy.",
        ),
        CheckResult(
            "ime-composition",
            "IME preedit, candidate placement, and committed composition",
            "NOT_PROVEN",
            "Xvfb has no desktop input-method compositor. Unicode clipboard/input "
            "coverage does not prove IME composition.",
        ),
        CheckResult(
            "physical-x11-compositor",
            "Physical X11 desktop and compositor behavior",
            "NOT_PROVEN",
            "This named system is an Xvfb server inside an arm64 Docker VM, not a "
            "physical/self-hosted X11 desktop. Window-manager focus, compositor "
            "effects, and human interaction remain unproven.",
        ),
        CheckResult(
            "sleep-wake",
            "Host sleep and wake recovery",
            "NOT_PROVEN",
            "A Docker/Xvfb container cannot suspend and resume the named physical host.",
        ),
    ]


def parse_linux_process_stat(stat: str) -> tuple[int, str] | None:
    """Return (parent PID, start time) without splitting a spaced process name."""
    command_end = stat.rfind(")")
    if command_end < 0:
        return None
    fields_after_command = stat[command_end + 1 :].split()
    if len(fields_after_command) <= 19:
        return None
    try:
        return int(fields_after_command[1]), fields_after_command[19]
    except ValueError:
        return None


class AcceptanceRun:
    """Own the packaged desktop process, X11 input, and collected observations."""

    def __init__(
        self,
        package: Path,
        source_root: Path,
        output: Path,
        runtime_evidence: Path,
        source_digest: str,
        image_reference: str,
    ) -> None:
        self.package = package
        self.source_root = source_root
        self.output = output
        self.runtime_evidence = runtime_evidence
        self.source_digest = source_digest
        self.image_reference = image_reference
        self.started_at = datetime.now(UTC)
        self.checks: list[CheckResult] = []
        self.app_process: subprocess.Popen[bytes] | None = None
        self.main_window: str | None = None
        self._clipboard_processes: list[subprocess.Popen[bytes]] = []
        self._app_log = None
        self._main_shell_identity: tuple[int, str] | None = None

    @property
    def executable(self) -> Path:
        return self.package / "Asura"

    def runtime_path_for_shell(self, name: str) -> str:
        """Return one harness-owned evidence path quoted for the child shell."""
        return shlex.quote(str(self.runtime_evidence / name))

    def record(
        self,
        check_id: str,
        title: str,
        result: str,
        notes: str,
        *evidence: str,
    ) -> None:
        self.checks.append(CheckResult(check_id, title, result, notes, evidence))

    def start_desktop(self) -> bool:
        self.output.mkdir(parents=True, exist_ok=True)
        self.runtime_evidence.mkdir(parents=True, exist_ok=True)
        self._app_log = (self.output / "asura.log").open("wb")
        environment = os.environ.copy()
        environment.update(
            {
                "DISPLAY": ":99",
                "XDG_SESSION_TYPE": "x11",
                "SHELL": "/bin/bash",
                "HOME": "/work/home",
                "XDG_DATA_HOME": "/work/home/.local/share",
                "XDG_CONFIG_HOME": "/work/home/.config",
                "XDG_CACHE_HOME": "/work/home/.cache",
                "XDG_RUNTIME_DIR": "/work/runtime",
                "LIBGL_ALWAYS_SOFTWARE": "1",
            }
        )
        self.app_process = subprocess.Popen(
            [str(self.executable)],
            cwd=self.source_root,
            env=environment,
            stdout=self._app_log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        try:
            self.main_window = self.wait_for_window(r"^Asura$")
        except TimeoutError as error:
            self.record(
                "desktop-startup",
                "Packaged Avalonia desktop startup",
                "FAIL",
                str(error),
                "asura.log",
            )
            return False

        time.sleep(1.0)
        self.capture(self.main_window, "launcher.png")
        geometry = self.command(
            "xdotool", "getwindowgeometry", "--shell", self.main_window
        ).stdout
        (self.output / "launcher-window-geometry.txt").write_bytes(geometry)
        self.record(
            "desktop-startup",
            "Packaged Avalonia desktop startup",
            "PASS",
            "The self-contained package opened its real X11 launcher and remained alive.",
            "launcher.png",
            "launcher-window-geometry.txt",
            "asura.log",
        )
        return True

    def open_real_terminal(self) -> bool:
        assert self.main_window is not None
        # Use the documented application shortcut instead of a launcher coordinate.
        # The launcher reflows with font metrics and accessibility scale, so a fixed
        # click can silently target empty space while the keyboard contract stays
        # stable and is itself part of M1 acceptance.
        self.command("xdotool", "windowfocus", "--sync", self.main_window)
        self.key("ctrl+t")
        time.sleep(1.5)
        self.click(self.main_window, 700, 420)
        pty_path = self.runtime_path_for_shell("pty.txt")
        command = (
            f"printf 'tty=%s\\n' \"$(tty)\" > {pty_path}; "
            f"if test -t 0; then printf 'is_tty=yes\\n' >> {pty_path}; "
            f"else printf 'is_tty=no\\n' >> {pty_path}; fi; "
            "printf 'ASURA_PTY_READY\\n'"
        )
        for _ in range(3):
            time.sleep(1.0)
            self.send_command(command)
            try:
                self.wait_for_file(self.runtime_evidence / "pty.txt", timeout=4.0)
                break
            except TimeoutError:
                self.click(self.main_window, 700, 420)

        evidence_path = self.runtime_evidence / "pty.txt"
        if not evidence_path.exists():
            self.capture(self.main_window, "terminal-open-failed.png")
            self.record(
                "real-pty",
                "Managed renderer to real PTY",
                "FAIL",
                "X11 input did not produce the PTY evidence marker.",
                "terminal-open-failed.png",
            )
            return False

        content = evidence_path.read_text(encoding="utf-8")
        passed = "is_tty=yes" in content and re.search(r"tty=/dev/pts/\d+", content)
        self.capture(self.main_window, "terminal-pty.png")
        self.record(
            "real-pty",
            "Managed renderer to real PTY",
            "PASS" if passed else "FAIL",
            "Ctrl+T opened the terminal; " + content.strip().replace("\n", "; "),
            "terminal-pty.png",
            "runtime/pty.txt",
        )
        return bool(passed)

    def observe_unicode(self) -> None:
        assert self.main_window is not None
        expected = "日本語 Україна 😀 e\u0301"
        unicode_path = self.runtime_path_for_shell("unicode.txt")
        self.send_command(
            f"printf '%s\\n' '{expected}' > {unicode_path}; "
            f"printf '%s\\n' '{expected}'"
        )
        try:
            path = self.wait_for_file(self.runtime_evidence / "unicode.txt")
            passed = path.read_text(encoding="utf-8") == expected + "\n"
        except (TimeoutError, UnicodeDecodeError):
            passed = False
        self.capture(self.main_window, "terminal-unicode.png")
        self.record(
            "unicode-roundtrip",
            "Unicode input and output through the managed renderer",
            "PASS" if passed else "FAIL",
            "UTF-8 Japanese, Ukrainian, emoji, and a combining accent round-tripped "
            "through X11 clipboard paste, the terminal input contract, the PTY, and "
            "shell output.",
            "terminal-unicode.png",
            "runtime/unicode.txt",
        )
        self.record(
            "unicode-glyph-fidelity",
            "Unicode glyph fallback and terminal-cell fidelity",
            "NOT_PROVEN",
            "The UTF-8 byte roundtrip does not prove glyph availability, fallback, "
            "combining-mark placement, emoji width, or double-width cell behavior. "
            "The captured terminal image requires explicit visual review.",
            "terminal-unicode.png",
            "fontconfig-matches.txt",
        )

    def observe_resize(self) -> None:
        assert self.main_window is not None
        before = self.runtime_evidence / "size-before.txt"
        after = self.runtime_evidence / "size-after.txt"
        self.send_command(
            f"stty size > {self.runtime_path_for_shell('size-before.txt')}"
        )
        try:
            self.wait_for_file(before)
            self.command(
                "xdotool", "windowsize", "--sync", self.main_window, "1180", "720"
            )
            time.sleep(1.0)
            self.click(self.main_window, 590, 360)
            self.send_command(
                f"stty size > {self.runtime_path_for_shell('size-after.txt')}"
            )
            self.wait_for_file(after)
            before_size = self.parse_terminal_size(before)
            after_size = self.parse_terminal_size(after)
            passed = before_size != after_size and all(value > 0 for value in after_size)
        except (TimeoutError, ValueError, subprocess.CalledProcessError):
            before_size = (0, 0)
            after_size = (0, 0)
            passed = False
        self.capture(self.main_window, "terminal-resized.png")
        self.command("xdotool", "windowsize", "--sync", self.main_window, "1440", "900")
        time.sleep(0.8)
        self.click(self.main_window, 700, 420)
        self.record(
            "pty-resize",
            "X11 viewport to PTY grid resize",
            "PASS" if passed else "FAIL",
            f"stty size changed from {before_size[0]}x{before_size[1]} to "
            f"{after_size[0]}x{after_size[1]}.",
            "terminal-resized.png",
            "runtime/size-before.txt",
            "runtime/size-after.txt",
        )

    def observe_interactive_tui(self) -> None:
        assert self.main_window is not None
        fixture = self.runtime_evidence / "less-fixture.txt"
        fixture.write_text(
            "".join(f"Asura interactive TUI line {index:03d}\n" for index in range(1, 241)),
            encoding="utf-8",
        )
        marker = self.runtime_evidence / "tui.txt"
        fixture_path = self.runtime_path_for_shell("less-fixture.txt")
        marker_path = self.runtime_path_for_shell("tui.txt")
        self.send_command(
            f"less {fixture_path}; printf 'less-exited=yes\\n' > {marker_path}"
        )
        time.sleep(1.0)
        self.capture(self.main_window, "terminal-less-tui.png")
        self.key("Next")
        time.sleep(0.3)
        self.key("q")
        try:
            self.wait_for_file(marker)
            passed = marker.read_text(encoding="utf-8") == "less-exited=yes\n"
        except TimeoutError:
            passed = False
        self.record(
            "interactive-tui",
            "Interactive less TUI through the packaged renderer",
            "PASS" if passed else "FAIL",
            "less entered an interactive screen, accepted PageDown and q, then "
            "returned control to the shell.",
            "terminal-less-tui.png",
            "runtime/tui.txt",
        )

    def observe_alternate_screen(self) -> None:
        assert self.main_window is not None
        active = self.runtime_evidence / "alternate-active.txt"
        completed = self.runtime_evidence / "alternate-completed.txt"
        active_path = self.runtime_path_for_shell("alternate-active.txt")
        completed_path = self.runtime_path_for_shell("alternate-completed.txt")
        self.send_command(
            "printf 'PRIMARY_BEFORE_ALT\\n'; tput smcup; "
            "printf '\\033[2J\\033[HALTERNATE_SCREEN_ACTIVE\\n'; "
            f"printf 'active=yes\\n' > {active_path}; "
            "sleep 4; tput rmcup; printf 'PRIMARY_AFTER_ALT\\n'; "
            f"printf 'completed=yes\\n' > {completed_path}"
        )
        try:
            self.wait_for_file(active)
            time.sleep(0.5)
            self.capture(self.main_window, "terminal-alternate-active.png")
            self.wait_for_file(completed, timeout=8.0)
            time.sleep(0.4)
            self.capture(self.main_window, "terminal-alternate-restored.png")
            completed_ok = completed.read_text(encoding="utf-8") == "completed=yes\n"
        except TimeoutError:
            completed_ok = False
        self.record(
            "alternate-screen",
            "Alternate-screen entry and restoration",
            "NOT_PROVEN",
            "The real PTY fixture completed without crashing and before/after "
            "screenshots were captured, but this runner does not use OCR or a "
            "screen-snapshot API to assert restoration. Manual review is still required."
            if completed_ok
            else (
                "The alternate-screen fixture did not complete; manual review and "
                "diagnosis are required."
            ),
            "terminal-alternate-active.png",
            "terminal-alternate-restored.png",
            "runtime/alternate-completed.txt",
        )

    def observe_mouse_reporting(self) -> None:
        assert self.main_window is not None
        result_path = self.runtime_evidence / "mouse-reporting.json"
        fixture = self.source_root / "scripts/acceptance/mouse_reporting_fixture.py"
        self.send_command(
            f"python3 {shlex.quote(str(fixture))} "
            f"{self.runtime_path_for_shell('mouse-reporting.json')} --timeout 5"
        )
        time.sleep(0.8)
        self.command("xdotool", "mousemove", "--window", self.main_window, "650", "350")
        self.command("xdotool", "mousedown", "1")
        self.command("xdotool", "mousemove", "--window", self.main_window, "720", "390")
        self.command("xdotool", "mouseup", "1")
        self.command("xdotool", "click", "4")
        self.command("xdotool", "click", "5")
        try:
            self.wait_for_file(result_path, timeout=8.0)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            passed = all(
                result.get(name) is True
                for name in (
                    "observedPress",
                    "observedRelease",
                    "observedDrag",
                    "observedWheel",
                )
            )
            report_count = len(result.get("reports", []))
        except (TimeoutError, json.JSONDecodeError, OSError):
            passed = False
            report_count = 0
        self.record(
            "mouse-reporting",
            "SGR mouse reporting through X11 and the PTY",
            "PASS" if passed else "FAIL",
            f"Captured {report_count} SGR reports; required press, release, drag, and "
            f"wheel reports were {'present' if passed else 'not all present'}.",
            "runtime/mouse-reporting.json",
        )

    def observe_guarded_paste(self) -> None:
        assert self.main_window is not None
        cancelled = self.runtime_evidence / "paste-cancelled-should-not-exist.txt"
        confirmed = self.runtime_evidence / "paste-confirmed.txt"
        barrier = self.runtime_evidence / "paste-barrier.txt"
        confirmation_token = f"confirmed-{secrets.token_hex(12)}"
        barrier_token = f"settled-{secrets.token_hex(12)}"
        self.send_command("bind 'set enable-bracketed-paste off' 2>/dev/null || true")
        time.sleep(0.4)

        self.set_clipboard(
            "printf 'cancelled=no\\n' > "
            f"{self.runtime_path_for_shell('paste-cancelled-should-not-exist.txt')}\n"
        )
        self.key("ctrl+shift+v")
        time.sleep(0.5)
        self.capture(self.main_window, "terminal-paste-confirmation.png")
        pending_was_not_executed = not cancelled.exists()
        self.key("Escape")
        time.sleep(0.4)
        cancel_worked = not cancelled.exists()

        self.set_clipboard(
            f"printf '%s\\n' '{confirmation_token}' >> "
            f"{self.runtime_path_for_shell('paste-confirmed.txt')}\n"
        )
        self.key("ctrl+shift+v")
        time.sleep(0.4)
        self.key("Return")
        try:
            self.wait_for_file(confirmed)
            # A later command through the same PTY is a causal boundary: when its
            # marker exists, every byte submitted by the confirmed paste has been
            # processed. A fixed delay cannot establish that ordering.
            self.send_command(
                f"printf '%s\\n' '{barrier_token}' > "
                f"{self.runtime_path_for_shell('paste-barrier.txt')}"
            )
            self.wait_for_file(barrier)
            confirmation_records = confirmed.read_text(encoding="utf-8").splitlines()
            confirmation_count = confirmation_records.count(confirmation_token)
            barrier_completed = (
                barrier.read_text(encoding="utf-8") == barrier_token + "\n"
            )
            cancelled_remained_absent = not cancelled.exists()
            confirm_worked = (
                confirmation_count == 1 and len(confirmation_records) == 1
            )
        except (TimeoutError, OSError, UnicodeDecodeError):
            confirmation_count = 0
            barrier_completed = False
            cancelled_remained_absent = not cancelled.exists()
            confirm_worked = False
        passed = (
            pending_was_not_executed
            and cancel_worked
            and cancelled_remained_absent
            and barrier_completed
            and confirm_worked
        )
        self.record(
            "guarded-paste",
            "Unsafe multiline paste confirmation",
            "PASS" if passed else "FAIL",
            "A multiline paste stayed pending, Escape cancelled it without execution, "
            f"and the confirmed command produced {confirmation_count} matching record(s) "
            "with no extra records after a causal PTY barrier; "
            f"cancelled command absent after barrier: {cancelled_remained_absent}.",
            "terminal-paste-confirmation.png",
            "runtime/paste-confirmed.txt",
            "runtime/paste-barrier.txt",
        )

    def observe_osc52_write_policy(self) -> None:
        assert self.main_window is not None
        self.set_clipboard("BASELINE")
        self.command(
            "xdotool",
            "type",
            "--clearmodifiers",
            "--delay",
            "2",
            "printf '\\033]52;c;R2hvc3RTaGVsbC1PU0M1Mg==\\007'",
        )
        self.key("Return")
        time.sleep(1.0)
        clipboard = self.command(
            "xclip", "-selection", "clipboard", "-o", check=False
        ).stdout.decode("utf-8", errors="replace")
        passed = clipboard == "BASELINE"
        (self.output / "osc52-clipboard-observation.txt").write_text(
            clipboard, encoding="utf-8"
        )
        self.record(
            "clipboard-write-policy",
            "Brokerless OSC 52 clipboard write policy",
            "PASS" if passed else "FAIL",
            "The managed adapter has no safe process-originated clipboard broker; its "
            "documented contract discarded OSC 52 and preserved the existing clipboard."
            if passed
            else (
                "The managed adapter's documented fail-closed OSC 52 contract did not "
                "preserve the existing X11 clipboard."
            ),
            "osc52-clipboard-observation.txt",
        )
        self.record(
            "clipboard-read-policy",
            "Brokerless OSC 52 clipboard read response",
            "NOT_PROVEN",
            "Unit conformance covers the empty denial response, but this packaged run "
            "did not capture process-side bytes for an OSC 52 query.",
        )

    def observe_quick_terminal_hotkey(self) -> None:
        other_environment = os.environ.copy()
        other_environment["DISPLAY"] = ":99"
        other_log = (self.output / "x11-other-client.log").open("wb")
        other = subprocess.Popen(
            [
                "xmessage",
                "-title",
                "Asura Acceptance Other Client",
                "Other X11 client used to place keyboard focus outside Asura.",
            ],
            env=other_environment,
            stdout=other_log,
            stderr=subprocess.STDOUT,
        )
        try:
            other_window = self.wait_for_window(r"^Asura Acceptance Other Client$")
            self.command("xdotool", "windowfocus", "--sync", other_window)
            time.sleep(0.4)
            focused_window = self.command(
                "xdotool", "getwindowfocus", "getwindowname", check=False
            ).stdout.decode("utf-8", errors="replace")
            (self.output / "quick-trigger-focus.txt").write_text(
                focused_window, encoding="utf-8"
            )
            if "Asura Acceptance Other Client" not in focused_window:
                self.record(
                    "quick-terminal-xvfb-cross-client",
                    "Xvfb cross-client Quick Terminal grab and Escape dismissal",
                    "FAIL",
                    "The helper X11 client did not own focus before the global hotkey was sent.",
                    "quick-trigger-focus.txt",
                    "x11-other-client.log",
                )
                return

            self.key("Super_L+grave")
            quick_window = self.wait_for_window(r"^Asura Quick Terminal$")
            geometry = self.command(
                "xdotool", "getwindowgeometry", "--shell", quick_window
            ).stdout.decode("ascii", errors="replace")
            (self.output / "quick-terminal-window.txt").write_text(
                f"WINDOW={quick_window}\n{geometry}", encoding="utf-8"
            )
            self.key("Escape")
            hidden = self.wait_until(
                lambda: self.find_window(r"^Asura Quick Terminal$") is None,
                timeout=4.0,
            )
            self.record(
                "quick-terminal-xvfb-cross-client",
                "Xvfb cross-client Quick Terminal grab and Escape dismissal",
                "PASS" if hidden else "FAIL",
                "Super+grave opened Quick Terminal while a different X11 client had "
                "focus on the same Xvfb server, and the transient Escape grab dismissed "
                "it. This result is scoped to the named Xvfb system."
                if hidden
                else (
                    "Quick Terminal opened, but Escape did not hide it within the "
                    "bounded wait."
                ),
                "quick-terminal-window.txt",
                "quick-trigger-focus.txt",
                "x11-other-client.log",
            )
        except (TimeoutError, subprocess.CalledProcessError) as error:
            self.record(
                "quick-terminal-xvfb-cross-client",
                "Xvfb cross-client Quick Terminal grab and Escape dismissal",
                "FAIL",
                str(error),
                "quick-trigger-focus.txt",
                "x11-other-client.log",
            )
        finally:
            other.terminate()
            try:
                other.wait(timeout=3.0)
            except subprocess.TimeoutExpired:
                other.kill()
                other.wait(timeout=3.0)
            other_log.close()

    def observe_active_work_close_confirmation(self) -> None:
        assert self.main_window is not None
        assert self.app_process is not None
        self.command(
            "xdotool", "windowactivate", "--sync", self.main_window, check=False
        )
        self.command("xdotool", "windowfocus", "--sync", self.main_window, check=False)
        self.click(self.main_window, 700, 420)
        time.sleep(0.3)
        shell_identity_path = self.runtime_evidence / "active-shell.txt"
        self.send_command(
            "{ printf '%s ' \"$$\"; awk '{print $22}' /proc/$$/stat; } "
            f"> {self.runtime_path_for_shell('active-shell.txt')}"
        )
        try:
            identity_parts = self.wait_for_file(shell_identity_path).read_text(
                encoding="ascii"
            ).split()
            if len(identity_parts) != 2:
                raise ValueError("The shell identity marker is malformed.")
            shell_identity = (int(identity_parts[0]), identity_parts[1])
            if self.process_start_time(shell_identity[0]) != shell_identity[1]:
                raise ValueError("The recorded shell identity is no longer live.")
            self._main_shell_identity = shell_identity
        except (TimeoutError, UnicodeDecodeError, ValueError) as error:
            self.record(
                "active-work-close-confirmation",
                "Close with a live child PTY requires confirmation",
                "FAIL",
                str(error),
                "runtime/active-shell.txt",
            )
            return

        # Exercise the same window-manager close gesture used by a keyboard
        # operator. Direct X11 window destruction can bypass Avalonia's closing
        # contract and therefore cannot prove the active-work guard.
        self.key("alt+F4")
        try:
            confirmation_window = self.wait_for_window(r"^Confirm close$")
            geometry = self.command(
                "xdotool", "getwindowgeometry", "--shell", confirmation_window
            ).stdout.decode("ascii", errors="replace")
            (self.output / "active-work-confirmation-window.txt").write_text(
                f"WINDOW={confirmation_window}\n{geometry}", encoding="utf-8"
            )
            self.command(
                "xdotool", "windowactivate", "--sync", confirmation_window
            )
            time.sleep(0.2)
            self.command(
                "xdotool",
                "key",
                "--window",
                confirmation_window,
                "--clearmodifiers",
                "Escape",
                check=False,
            )
            escape_cancelled = self.wait_until(
                lambda: self.find_window(r"^Confirm close$") is None,
                timeout=1.0,
            )
            if not escape_cancelled:
                dimensions = dict(
                    line.split("=", 1)
                    for line in geometry.splitlines()
                    if "=" in line
                )
                # The synthetic X server can focus the Openbox frame instead of
                # the Avalonia client. Exercise the explicit Cancel control as a
                # bounded fallback without claiming keyboard acceptance.
                self.click(
                    confirmation_window,
                    int(dimensions["WIDTH"]) - 202,
                    int(dimensions["HEIGHT"]) - 40,
                )
            cancelled = self.wait_until(
                lambda: self.find_window(r"^Confirm close$") is None,
                timeout=4.0,
            )
            application_survived = self.app_process.poll() is None
            same_shell_survived = (
                self.process_start_time(shell_identity[0]) == shell_identity[1]
            )
            passed = cancelled and application_survived and same_shell_survived
            notes = (
                "A live child PTY required confirmation; "
                f"{'Escape' if escape_cancelled else 'the explicit Cancel button'} "
                "cancelled the close, "
                "the packaged application remained running, and the same shell process "
                "identity survived."
                if passed
                else (
                    "The active-work confirmation did not preserve every required state: "
                    f"dialog dismissed={cancelled}; application survived="
                    f"{application_survived}; same shell identity survived="
                    f"{same_shell_survived}."
                )
            )
            self.record(
                "active-work-close-confirmation",
                "Close with a live child PTY requires confirmation",
                "PASS" if passed else "FAIL",
                notes,
                "active-work-confirmation-window.txt",
                "runtime/active-shell.txt",
            )
        except (TimeoutError, subprocess.CalledProcessError) as error:
            self.record(
                "active-work-close-confirmation",
                "Close with a live child PTY requires confirmation",
                "FAIL",
                str(error),
            )

    def observe_lifecycle(self) -> None:
        assert self.main_window is not None
        assert self.app_process is not None
        marker = self.runtime_evidence / "lifecycle.txt"
        captured_identities = self.descendant_process_identities(self.app_process.pid)
        if self._main_shell_identity is not None:
            captured_identities[self._main_shell_identity[0]] = (
                self._main_shell_identity[1]
            )
        application_live_before_shell_exit = self.app_process.poll() is None
        self.command(
            "xdotool", "windowactivate", "--sync", self.main_window, check=False
        )
        self.command("xdotool", "windowfocus", "--sync", self.main_window)
        self.click(self.main_window, 700, 420)
        time.sleep(0.3)
        lifecycle_command = (
            "printf 'shell-exit-requested=yes\\n' > "
            f"{self.runtime_path_for_shell('lifecycle.txt')}; exit"
        )
        # Earlier checks already cover clipboard paste. Type the lifecycle command
        # as keyboard input so a stale application-prefix state or clipboard owner
        # cannot turn process cleanup into a false focus failure.
        self.command(
            "xdotool",
            "type",
            "--clearmodifiers",
            "--delay",
            "1",
            lifecycle_command,
        )
        self.key("Return")
        try:
            self.wait_for_file(marker, timeout=15.0)
        except TimeoutError:
            self.capture(self.main_window, "lifecycle-input-failed.png")
        shell_exited = self._main_shell_identity is not None and self.wait_until(
            lambda: self.process_start_time(self._main_shell_identity[0])
            != self._main_shell_identity[1],
            timeout=8.0,
        )
        time.sleep(1.0)
        application_live_before_close = self.app_process.poll() is None
        if application_live_before_close:
            captured_identities.update(
                self.descendant_process_identities(self.app_process.pid)
            )
            self.command(
                "xdotool", "windowactivate", "--sync", self.main_window, check=False
            )
            self.key("alt+F4")
            confirmation_observed = self.wait_until(
                lambda: self.find_window(r"^Confirm close$") is not None,
                timeout=2.0,
            )
        else:
            confirmation_observed = False
        if confirmation_observed:
            # The close contract may conservatively retain active-work state for a
            # short interval after the shell exits. Exercise the real confirmation
            # flow and click relative to the dialog bounds so text scaling cannot
            # invalidate the target.
            confirmation_window = self.find_window(r"^Confirm close$")
            assert confirmation_window is not None
            self.capture(confirmation_window, "close-confirmation.png")
            geometry = self.command(
                "xdotool", "getwindowgeometry", "--shell", confirmation_window
            ).stdout.decode("ascii")
            dimensions = dict(
                line.split("=", 1)
                for line in geometry.splitlines()
                if "=" in line
            )
            self.click(
                confirmation_window,
                int(dimensions["WIDTH"]) - 70,
                int(dimensions["HEIGHT"]) - 30,
            )
        try:
            return_code = self.app_process.wait(timeout=10.0)
            stopped = True
        except subprocess.TimeoutExpired:
            return_code = None
            stopped = False
        leaked = sorted(
            pid
            for pid, start_time in captured_identities.items()
            if self.process_start_time(pid) == start_time
        )
        passed = (
            application_live_before_shell_exit
            and application_live_before_close
            and stopped
            and return_code == 0
            and not leaked
            and marker.exists()
            and shell_exited
        )
        lifecycle_evidence = ["runtime/lifecycle.txt"]
        if not marker.exists():
            lifecycle_evidence.append("lifecycle-input-failed.png")
        if confirmation_observed:
            lifecycle_evidence.append("close-confirmation.png")
        self.record(
            "desktop-lifecycle",
            "Normal desktop and child PTY lifecycle",
            "PASS" if passed else "FAIL",
            f"Desktop exit code: {return_code}; surviving captured descendants: {leaked}; "
            f"application live before shell exit: {application_live_before_shell_exit}; "
            f"application live before close: {application_live_before_close}; "
            f"original shell exited: {shell_exited}; "
            f"active-work confirmation observed: {confirmation_observed}.",
            *lifecycle_evidence,
        )

    def record_unproven_boundaries(self) -> None:
        self.checks.extend(unproven_boundary_checks())

    def finish(self) -> int:
        self.record_unproven_boundaries()
        try:
            self.close_resources()
        except (OSError, subprocess.SubprocessError) as error:
            self.record(
                "harness-cleanup",
                "Bounded acceptance harness cleanup",
                "FAIL",
                f"Cleanup raised {type(error).__name__}; no cleanup success was inferred.",
            )
        try:
            self.copy_runtime_evidence()
        except OSError as error:
            self.record(
                "harness-evidence-copy",
                "Bounded acceptance evidence collection",
                "FAIL",
                f"Evidence collection raised {type(error).__name__}.",
            )
        self.checks = validate_evidence_references(self.output, self.checks)
        completed_at = datetime.now(UTC)
        try:
            package_hash = sha256(self.executable)
        except OSError as error:
            package_hash = None
            self.record(
                "package-fingerprint",
                "Packaged executable fingerprint",
                "FAIL",
                f"Package fingerprinting raised {type(error).__name__}.",
            )
        overall = (
            "PASS"
            if self.checks and all(check.result == "PASS" for check in self.checks)
            else "NOT_PASSING"
        )
        evidence = {
            "schemaVersion": 1,
            "declaredSystemName": SYSTEM_NAME,
            "actualHostName": platform.node(),
            "executionEnvironment": (
                "Docker Linux arm64 under Xvfb with Openbox (synthetic display)"
            ),
            "containerImage": self.image_reference,
            "osDescription": platform.platform(),
            "osArchitecture": platform.machine(),
            "kernel": platform.release(),
            "xdgSessionType": "x11",
            "display": ":99",
            "packageExecutable": str(self.executable),
            "packageSha256": package_hash,
            "sourceSnapshotSha256": self.source_digest,
            "startedAtUtc": self.started_at.isoformat(),
            "completedAtUtc": completed_at.isoformat(),
            "overallResult": overall,
            "checks": [asdict(check) for check in self.checks],
        }
        publish_receipt(self.output, evidence, self.checks)
        return 0 if overall == "PASS" else 1

    def copy_runtime_evidence(self) -> None:
        destination = self.output / "runtime"
        destination.mkdir(exist_ok=True)
        for source in self.runtime_evidence.iterdir():
            if source.is_file():
                (destination / source.name).write_bytes(source.read_bytes())

    def send_command(self, text: str) -> None:
        self.set_clipboard(text)
        self.key("ctrl+shift+v")
        time.sleep(0.15)
        self.key("Return")

    def set_clipboard(self, text: str) -> None:
        self.reap_clipboard_processes()
        process = subprocess.Popen(
            ["xclip", "-selection", "clipboard", "-loops", "1"],
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        assert process.stdin is not None
        process.stdin.write(text.encode("utf-8"))
        process.stdin.close()
        self._clipboard_processes.append(process)
        time.sleep(0.1)

    def reap_clipboard_processes(self) -> None:
        active: list[subprocess.Popen[bytes]] = []
        for process in self._clipboard_processes:
            if process.poll() is None:
                active.append(process)
            else:
                process.wait()
        self._clipboard_processes = active

    def click(self, window: str, x: int, y: int) -> None:
        self.command(
            "xdotool", "mousemove", "--window", window, str(x), str(y), "click", "1"
        )

    def key(self, gesture: str) -> None:
        self.command("xdotool", "key", "--clearmodifiers", gesture)

    def capture(self, window: str, name: str) -> None:
        self.command("import", "-window", window, str(self.output / name))

    def wait_for_window(self, title_pattern: str) -> str:
        window: str | None = None

        def found() -> bool:
            nonlocal window
            window = self.find_window(title_pattern)
            return window is not None

        if not self.wait_until(found, WINDOW_WAIT_SECONDS):
            raise TimeoutError(f"No visible X11 window matched {title_pattern!r}.")
        assert window is not None
        return window

    def find_window(self, title_pattern: str) -> str | None:
        result = self.command(
            "xdotool",
            "search",
            "--onlyvisible",
            "--name",
            title_pattern,
            check=False,
        )
        if result.returncode != 0:
            return None
        windows = result.stdout.decode("ascii", errors="ignore").splitlines()
        return windows[-1] if windows else None

    def wait_for_file(self, path: Path, timeout: float = FILE_WAIT_SECONDS) -> Path:
        if not self.wait_until(lambda: path.exists() and path.stat().st_size > 0, timeout):
            raise TimeoutError(f"Timed out waiting for {path}.")
        return path

    @staticmethod
    def wait_until(predicate, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():
                return True
            time.sleep(0.1)
        return predicate()

    @staticmethod
    def parse_terminal_size(path: Path) -> tuple[int, int]:
        parts = path.read_text(encoding="utf-8").split()
        if len(parts) != 2:
            raise ValueError(f"Invalid stty size payload: {parts!r}")
        return int(parts[0]), int(parts[1])

    @staticmethod
    def descendant_process_identities(root_pid: int) -> dict[int, str]:
        """Snapshot descendants by PID and start time before they can be reparented."""
        processes: dict[int, tuple[int, str]] = {}
        try:
            process_directories = list(Path("/proc").iterdir())
        except OSError:
            return {}
        for directory in process_directories:
            if not directory.name.isdigit():
                continue
            try:
                parsed = parse_linux_process_stat(
                    (directory / "stat").read_text(encoding="ascii")
                )
            except (FileNotFoundError, ProcessLookupError, UnicodeDecodeError, OSError):
                continue
            if parsed is not None:
                processes[int(directory.name)] = parsed

        descendants: dict[int, str] = {}
        pending = [root_pid]
        while pending:
            parent = pending.pop()
            for pid, (parent_pid, start_time) in processes.items():
                if parent_pid == parent and pid not in descendants:
                    descendants[pid] = start_time
                    pending.append(pid)
        return descendants

    @staticmethod
    def process_start_time(pid: int) -> str | None:
        try:
            stat = Path(f"/proc/{pid}/stat").read_text(encoding="ascii")
        except (FileNotFoundError, ProcessLookupError, UnicodeDecodeError):
            return None
        parsed = parse_linux_process_stat(stat)
        return parsed[1] if parsed is not None else None

    @staticmethod
    def command(
        *args: str,
        check: bool = True,
        input_bytes: bytes | None = None,
    ) -> subprocess.CompletedProcess[bytes]:
        return subprocess.run(
            args,
            input=input_bytes,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=check,
            timeout=20.0,
        )

    def close_resources(self) -> None:
        for process in self._clipboard_processes:
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=1.0)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=1.0)
        self._clipboard_processes.clear()
        if self.app_process is not None and self.app_process.poll() is None:
            try:
                os.killpg(self.app_process.pid, signal.SIGTERM)
                self.app_process.wait(timeout=3.0)
            except (ProcessLookupError, subprocess.TimeoutExpired):
                try:
                    os.killpg(self.app_process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                self.app_process.wait(timeout=3.0)
        if self._app_log is not None:
            self._app_log.close()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_infrastructure_failure_receipt(
    output: Path,
    source_digest: str,
    image_reference: str,
    stage: str,
    exit_code: int,
) -> int:
    """Record setup failures that happen before the interactive runner can start."""
    output.mkdir(parents=True, exist_ok=True)
    candidates = (
        "docker-server.json",
        "docker-daemon-platform.txt",
        "publish.log",
        "xvfb.log",
        "openbox.log",
        "window-manager.txt",
    )
    available = tuple(name for name in candidates if (output / name).is_file())
    checks = [
        CheckResult(
            "harness-infrastructure",
            "Bounded acceptance harness infrastructure",
            "FAIL",
            f"Stage {stage!r} exited with status {exit_code} before the interactive "
            "acceptance run could publish its receipt. No product behavior was inferred.",
            available,
        ),
        *unproven_boundary_checks(),
    ]
    checks = validate_evidence_references(output, checks)
    now = datetime.now(UTC)
    normalized_digest = (
        source_digest.lower()
        if re.fullmatch(r"[0-9a-fA-F]{64}", source_digest)
        else None
    )
    evidence = {
        "schemaVersion": 1,
        "declaredSystemName": SYSTEM_NAME,
        "actualHostName": platform.node(),
        "executionEnvironment": (
            "Acceptance coordinator/setup boundary; the packaged Docker/Xvfb "
            "interaction phase was not reached"
        ),
        "containerImage": image_reference,
        "osDescription": platform.platform(),
        "osArchitecture": platform.machine(),
        "kernel": platform.release(),
        "xdgSessionType": None,
        "display": None,
        "packageExecutable": None,
        "packageSha256": None,
        "sourceSnapshotSha256": normalized_digest,
        "startedAtUtc": now.isoformat(),
        "completedAtUtc": now.isoformat(),
        "overallResult": "NOT_PASSING",
        "checks": [asdict(check) for check in checks],
    }
    publish_receipt(output, evidence, checks)
    return 1


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path)
    parser.add_argument("--source-root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime-evidence", type=Path)
    parser.add_argument("--source-digest", required=True)
    parser.add_argument("--image-reference", required=True)
    parser.add_argument("--infrastructure-failure-stage")
    parser.add_argument("--infrastructure-failure-exit-code", type=int)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.infrastructure_failure_stage is not None:
        if args.infrastructure_failure_exit_code is None:
            raise SystemExit("--infrastructure-failure-exit-code is required")
        return write_infrastructure_failure_receipt(
            args.output.resolve(),
            args.source_digest,
            args.image_reference,
            args.infrastructure_failure_stage,
            args.infrastructure_failure_exit_code,
        )
    missing = [
        name
        for name in ("package", "source_root", "runtime_evidence")
        if getattr(args, name) is None
    ]
    if missing:
        raise SystemExit("missing required run argument(s): " + ", ".join(missing))
    assert args.package is not None
    assert args.source_root is not None
    assert args.runtime_evidence is not None
    run = AcceptanceRun(
        args.package.resolve(),
        args.source_root.resolve(),
        args.output.resolve(),
        args.runtime_evidence.resolve(),
        args.source_digest,
        args.image_reference,
    )
    try:
        try:
            if run.start_desktop() and run.open_real_terminal():
                run.observe_unicode()
                run.observe_resize()
                run.observe_interactive_tui()
                run.observe_alternate_screen()
                run.observe_mouse_reporting()
                run.observe_guarded_paste()
                run.observe_osc52_write_policy()
                run.observe_quick_terminal_hotkey()
                # Quick Terminal deliberately exercises a different foreground
                # client. Run the modal close/cancel flow afterwards so its focus
                # restoration is the immediate precondition for lifecycle input.
                run.observe_active_work_close_confirmation()
                run.observe_lifecycle()
        except (
            TimeoutError,
            subprocess.SubprocessError,
            OSError,
            UnicodeError,
            ValueError,
            KeyError,
        ) as error:
            run.record(
                "harness-infrastructure",
                "Bounded acceptance harness infrastructure",
                "FAIL",
                "A required infrastructure operation raised "
                f"{type(error).__name__}; the run was finalized without inferring "
                "any remaining observations.",
                "asura.log",
            )
        return run.finish()
    finally:
        run.close_resources()


if __name__ == "__main__":
    raise SystemExit(main())
