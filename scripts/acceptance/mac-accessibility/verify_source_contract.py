#!/usr/bin/env python3
"""Guard the passive AX metadata allowlist used by the macOS probe."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ALLOWED_ATTRIBUTES = {
    "kAXChildrenAttribute",
    "kAXDescriptionAttribute",
    "kAXEnabledAttribute",
    "kAXExpandedAttribute",
    "kAXFocusedAttribute",
    "kAXHelpAttribute",
    "kAXRoleAttribute",
    "kAXSelectedAttribute",
    "kAXTitleAttribute",
    "kAXWindowsAttribute",
}

FORBIDDEN_APIS = {
    "AXUIElementCopyActionNames",
    "AXUIElementCopyElementAtPosition",
    "AXUIElementCopyParameterizedAttributeValue",
    "AXUIElementPerformAction",
    "AXUIElementPostKeyboardEvent",
    "AXUIElementSetAttributeValue",
    "CGEventPost",
    "CGWindowListCreateImage",
    "NSPasteboard",
}

EXPECTED_NAMED_CONTROL_ROLES = {
    "AXBrowser",
    "AXButton",
    "AXCell",
    "AXCheckBox",
    "AXColumn",
    "AXComboBox",
    "AXDisclosureTriangle",
    "AXGrid",
    "AXIncrementor",
    "AXLink",
    "AXList",
    "AXMenu",
    "AXMenuBarItem",
    "AXMenuButton",
    "AXMenuItem",
    "AXOutline",
    "AXPopUpButton",
    "AXRadioButton",
    "AXRadioGroup",
    "AXRow",
    "AXSlider",
    "AXTabGroup",
    "AXTable",
    "AXTextArea",
    "AXTextField",
    "AXToolbar",
}

EXPECTED_TERMINAL_ROLES = {"AXGroup", "AXScrollArea", "AXTextArea"}

REQUIRED_IDENTITY_FRAGMENTS = {
    'import CryptoKit',
    'private let asuraBundleIdentifier = "sh.asura"',
    'private let expectedBundleName = "Asura.app"',
    'private let expectedExecutableName = "Asura"',
    'application.bundleIdentifier == asuraBundleIdentifier',
    'let bundleUrl = application.bundleURL',
    'let applicationExecutableUrl = application.executableURL',
    'var hasher = SHA256()',
    'processId: application.processIdentifier',
    'executableSha256: digest',
    'maxRunningApplications: 256',
    'maxWindowsPerApplication: 16',
    'guard observedRunningApplicationCount <= limits.maxRunningApplications else',
    'guard windowElements.count <= limits.maxWindowsPerApplication else',
}


def _swift_string_set(source: str, declaration: str) -> set[str]:
    match = re.search(
        rf"private static let {re.escape(declaration)}: Set<String> = \[(.*?)\n    \]",
        source,
        flags=re.DOTALL,
    )
    if match is None:
        return set()
    return set(re.findall(r'"([A-Za-z0-9 ]+)"', match.group(1)))


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: verify_source_contract.py <probe.swift>", file=sys.stderr)
        return 64

    source = Path(sys.argv[1]).read_text(encoding="utf-8")
    attributes = set(re.findall(r"\bkAX[A-Za-z0-9]+Attribute\b", source))
    unexpected_attributes = sorted(attributes - ALLOWED_ATTRIBUTES)
    missing_attributes = sorted(ALLOWED_ATTRIBUTES - attributes)
    forbidden_uses = sorted(api for api in FORBIDDEN_APIS if api in source)
    named_control_roles = _swift_string_set(source, "actionableRoles")
    terminal_roles = _swift_string_set(source, "terminalRoles")
    missing_identity_fragments = sorted(
        fragment for fragment in REQUIRED_IDENTITY_FRAGMENTS if fragment not in source
    )

    errors: list[str] = []
    if unexpected_attributes:
        errors.append(f"unexpected AX attributes: {unexpected_attributes}")
    if missing_attributes:
        errors.append(f"expected AX allowlist changed: {missing_attributes}")
    if forbidden_uses:
        errors.append(f"forbidden mutation/content APIs: {forbidden_uses}")
    if source.count("AXUIElementCopyAttributeValue") != 1:
        errors.append("AX attribute reads must remain centralized at one boundary")
    if named_control_roles != EXPECTED_NAMED_CONTROL_ROLES:
        errors.append("named-control role coverage changed without contract review")
    if terminal_roles != EXPECTED_TERMINAL_ROLES:
        errors.append("terminal-role allowlist changed without contract review")
    if missing_identity_fragments:
        errors.append(f"packaged-build identity checks missing: {missing_identity_fragments}")
    if "guard summary.focusedElementCount > 0 else" not in source:
        errors.append("PASS no longer requires an actually focused AX element")
    identity_gate = source.find("guard let identity = packagedBuildIdentity(")
    accessibility_application = source.find("AXUIElementCreateApplication(")
    if identity_gate < 0 or accessibility_application < 0 or identity_gate > accessibility_application:
        errors.append("AX discovery is no longer gated by packaged-build identity")
    window_loop = source.find("for window in windowElements {")
    window_title_read = source.find("readString(window, attribute: kAXTitleAttribute")
    deadline_check = source.find(
        "guard ProcessInfo.processInfo.systemUptime <= deadline else",
        window_loop,
    )
    if (
        window_loop < 0
        or window_title_read < window_loop
        or deadline_check < window_loop
        or deadline_check > window_title_read
    ):
        errors.append("per-window AX title reads are no longer deadline guarded")

    if errors:
        for error in errors:
            print(f"mac-accessibility source contract failed: {error}", file=sys.stderr)
        return 1

    print("mac-accessibility source contract passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
