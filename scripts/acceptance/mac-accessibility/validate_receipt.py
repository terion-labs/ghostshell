#!/usr/bin/env python3
"""Validate the bounded, privacy-safe Asura macOS AX receipt."""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime
from pathlib import Path
from typing import Any


CHECK_RULES = {
    "platform": {("PASS", "DARWIN")},
    "screen-unlocked": {
        ("PASS", "UNLOCKED"),
        ("BLOCKED", "SCREEN_LOCKED"),
        ("BLOCKED", "SESSION_STATE_UNAVAILABLE"),
    },
    "accessibility-trusted": {
        ("PASS", "TRUSTED"),
        ("BLOCKED", "NOT_TRUSTED"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "messaging-timeout": {
        ("PASS", "CONFIGURED"),
        ("BLOCKED", "CONFIGURATION_FAILED"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "discovery-bounds": {
        ("PASS", "WITHIN_LIMITS"),
        ("BLOCKED", "APPLICATION_LIMIT_EXCEEDED"),
        ("BLOCKED", "DEADLINE_EXCEEDED"),
        ("BLOCKED", "WINDOW_LIMIT_EXCEEDED"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "packaged-build-identity": {
        ("PASS", "VERIFIED"),
        ("BLOCKED", "NO_VERIFIED_PACKAGE"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "unique-application": {
        ("PASS", "EXACTLY_ONE"),
        ("BLOCKED", "MULTIPLE"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "unique-main-window": {
        ("PASS", "EXACTLY_ONE"),
        ("BLOCKED", "NONE"),
        ("BLOCKED", "MULTIPLE"),
        ("BLOCKED", "EXTRA_WINDOWS"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "tree-bounded": {
        ("PASS", "COMPLETE"),
        ("FAIL", "LIMIT_EXCEEDED"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "metadata-readable": {
        ("PASS", "READABLE"),
        ("FAIL", "READ_ERRORS"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "actionable-elements-named": {
        ("PASS", "COMPLETE"),
        ("FAIL", "MISSING_NAMES"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "terminal-accessibility": {
        ("PASS", "NAMED_TERMINAL_PRESENT"),
        ("FAIL", "INVALID_TERMINAL_ROLE"),
        ("NOT_APPLICABLE", "NO_TERMINAL_IN_CURRENT_SURFACE"),
        ("NOT_RUN", "NOT_RUN"),
    },
    "focus-state": {
        ("PASS", "FOCUSED_ELEMENT_PRESENT"),
        ("FAIL", "UNAVAILABLE"),
        ("FAIL", "NO_FOCUSED_ELEMENT"),
        ("NOT_RUN", "NOT_RUN"),
    },
}

REASON_CODES = {
    "PASS": {"ACCEPTANCE_PASSED"},
    "FAIL": {
        "FOCUSED_ELEMENT_MISSING",
        "FOCUS_METADATA_UNAVAILABLE",
        "METADATA_READ_FAILED",
        "TERMINAL_ROLE_INVALID",
        "TREE_LIMIT_EXCEEDED",
        "UNNAMED_ACTIONABLE_ELEMENTS",
    },
    "BLOCKED": {
        "AMBIGUOUS_APP_INSTANCES",
        "AX_NOT_TRUSTED",
        "AX_TIMEOUT_CONFIGURATION_FAILED",
        "BUILD_IDENTITY_UNAVAILABLE",
        "DISCOVERY_LIMIT_EXCEEDED",
        "DISCOVERY_TIMEOUT",
        "SCREEN_LOCKED",
        "SCREEN_STATE_UNAVAILABLE",
        "WINDOW_COUNT_MISMATCH",
    },
}

TOP_LEVEL_KEYS = {
    "architecture",
    "checks",
    "limits",
    "platform",
    "privacy",
    "probe",
    "probeVersion",
    "reasonCode",
    "recordedAtUtc",
    "schemaVersion",
    "scope",
    "status",
    "summary",
    "targetIdentity",
}

SUMMARY_KEYS = {
    "actionableElementCount",
    "applicationWindowCount",
    "childLimitHitCount",
    "cycleCount",
    "depthLimitHitCount",
    "durationLimitHitCount",
    "focusedElementCount",
    "focusMetadataElementCount",
    "helpMetadataElementCount",
    "matchingApplicationCount",
    "matchingMainWindowCount",
    "maximumVerifiedApplicationWindowCount",
    "metadataReadErrorCount",
    "nameMetadataElementCount",
    "observedRunningApplicationCount",
    "stateMetadataElementCount",
    "terminalElementCount",
    "terminalNameRoleMismatchCount",
    "treeTruncated",
    "unnamedActionableElementCount",
    "visitedNodeCount",
}


class ReceiptValidationError(ValueError):
    pass


def _object_without_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ReceiptValidationError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _require_exact_keys(value: dict[str, Any], expected: set[str], label: str) -> None:
    actual = set(value)
    if actual != expected:
        missing = sorted(expected - actual)
        extra = sorted(actual - expected)
        raise ReceiptValidationError(
            f"{label} keys differ; missing={missing}, extra={extra}"
        )


def _require_non_negative_integer(value: Any, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ReceiptValidationError(f"{label} must be a non-negative integer")
    return value


def validate_receipt(receipt: Any) -> None:
    if not isinstance(receipt, dict):
        raise ReceiptValidationError("receipt root must be an object")
    _require_exact_keys(receipt, TOP_LEVEL_KEYS, "receipt")

    if receipt["schemaVersion"] != 3:
        raise ReceiptValidationError("unsupported schemaVersion")
    if receipt["probe"] != "asura.macos.accessibility":
        raise ReceiptValidationError("unexpected probe identifier")
    if receipt["probeVersion"] != "1.2.0":
        raise ReceiptValidationError("unexpected probe version")
    if receipt["platform"] != "macOS":
        raise ReceiptValidationError("unexpected platform")
    if receipt["architecture"] not in {"arm64", "x86_64"}:
        raise ReceiptValidationError("unexpected architecture")

    try:
        timestamp = receipt["recordedAtUtc"]
        if not isinstance(timestamp, str) or not timestamp.endswith("Z"):
            raise TypeError
        datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
    except (TypeError, ValueError) as error:
        raise ReceiptValidationError("recordedAtUtc is not ISO-8601") from error

    status = receipt["status"]
    reason_code = receipt["reasonCode"]
    if status not in REASON_CODES or reason_code not in REASON_CODES[status]:
        raise ReceiptValidationError("status and reasonCode are inconsistent")

    scope = receipt["scope"]
    if not isinstance(scope, dict):
        raise ReceiptValidationError("scope must be an object")
    _require_exact_keys(scope, {"actionsExecuted", "target", "traversal"}, "scope")
    if scope != {
        "actionsExecuted": [],
        "target": "ASURA_MAIN_WINDOW",
        "traversal": "PASSIVE_METADATA_ONLY",
    }:
        raise ReceiptValidationError("scope permits unexpected target or actions")

    privacy = receipt["privacy"]
    if not isinstance(privacy, dict):
        raise ReceiptValidationError("privacy must be an object")
    expected_privacy = {
        "rawHelpEmitted": False,
        "rawNamesEmitted": False,
        "screenshotsCaptured": False,
        "userTextEmitted": False,
        "valuesQueried": False,
    }
    if privacy != expected_privacy:
        raise ReceiptValidationError("privacy guarantees are absent or weakened")

    target_identity = receipt["targetIdentity"]
    if not isinstance(target_identity, dict):
        raise ReceiptValidationError("targetIdentity must be an object")
    _require_exact_keys(
        target_identity,
        {"executableSha256", "expectedBundleIdentifier", "kind", "processId"},
        "targetIdentity",
    )
    if target_identity["kind"] != "PACKAGED_EXECUTABLE_SHA256":
        raise ReceiptValidationError("unexpected target identity kind")
    if target_identity["expectedBundleIdentifier"] != "sh.asura":
        raise ReceiptValidationError("unexpected target bundle identifier")
    process_id = target_identity["processId"]
    executable_digest = target_identity["executableSha256"]
    if (process_id is None) != (executable_digest is None):
        raise ReceiptValidationError("target identity fields must be present together")
    identity_available = process_id is not None
    if identity_available:
        if (
            isinstance(process_id, bool)
            or not isinstance(process_id, int)
            or process_id <= 0
        ):
            raise ReceiptValidationError("target processId must be a positive integer")
        if (
            not isinstance(executable_digest, str)
            or len(executable_digest) != 64
            or any(character not in "0123456789abcdef" for character in executable_digest)
        ):
            raise ReceiptValidationError("target executableSha256 must be lowercase SHA-256")

    limits = receipt["limits"]
    if not isinstance(limits, dict):
        raise ReceiptValidationError("limits must be an object")
    _require_exact_keys(
        limits,
        {
            "maxChildrenPerNode",
            "maxDepth",
            "maxDurationMilliseconds",
            "maxNodes",
            "maxRunningApplications",
            "maxWindowsPerApplication",
        },
        "limits",
    )
    if limits != {
        "maxChildrenPerNode": 256,
        "maxDepth": 64,
        "maxDurationMilliseconds": 10000,
        "maxNodes": 5000,
        "maxRunningApplications": 256,
        "maxWindowsPerApplication": 16,
    }:
        raise ReceiptValidationError("unexpected traversal limits")

    summary = receipt["summary"]
    if not isinstance(summary, dict):
        raise ReceiptValidationError("summary must be an object")
    _require_exact_keys(summary, SUMMARY_KEYS, "summary")
    for key, value in summary.items():
        if key == "treeTruncated":
            if not isinstance(value, bool):
                raise ReceiptValidationError("summary.treeTruncated must be boolean")
        else:
            _require_non_negative_integer(value, f"summary.{key}")

    checks = receipt["checks"]
    if not isinstance(checks, list) or len(checks) != len(CHECK_RULES):
        raise ReceiptValidationError("checks must contain the complete ordered check set")
    if [check.get("id") for check in checks if isinstance(check, dict)] != list(
        CHECK_RULES
    ):
        raise ReceiptValidationError("checks are missing, duplicated, or out of order")
    for check in checks:
        if not isinstance(check, dict):
            raise ReceiptValidationError("each check must be an object")
        _require_exact_keys(check, {"detailCode", "id", "outcome"}, "check")
        result = (check["outcome"], check["detailCode"])
        if result not in CHECK_RULES[check["id"]]:
            raise ReceiptValidationError(f"invalid result for check {check['id']}")

    if summary["visitedNodeCount"] > limits["maxNodes"]:
        raise ReceiptValidationError("visitedNodeCount exceeds maxNodes")
    if summary["unnamedActionableElementCount"] > summary["actionableElementCount"]:
        raise ReceiptValidationError("unnamed actionable count exceeds actionable count")
    if summary["focusedElementCount"] > summary["focusMetadataElementCount"]:
        raise ReceiptValidationError("focused count exceeds focus-metadata count")
    if summary["matchingMainWindowCount"] > summary["applicationWindowCount"]:
        raise ReceiptValidationError("matching main-window count exceeds application windows")

    indexed_checks = {check["id"]: check for check in checks}
    visited = summary["visitedNodeCount"] > 0

    discovery_bounds_check = indexed_checks["discovery-bounds"]
    packaged_identity_check = indexed_checks["packaged-build-identity"]
    unique_application_check = indexed_checks["unique-application"]
    unique_window_check = indexed_checks["unique-main-window"]
    discovery_result = (
        discovery_bounds_check["outcome"],
        discovery_bounds_check["detailCode"],
    )
    if discovery_result == ("PASS", "WITHIN_LIMITS"):
        if (
            summary["observedRunningApplicationCount"] > limits["maxRunningApplications"]
            or summary["maximumVerifiedApplicationWindowCount"]
            > limits["maxWindowsPerApplication"]
        ):
            raise ReceiptValidationError("discovery PASS exceeds configured bounds")
    elif discovery_result == ("BLOCKED", "APPLICATION_LIMIT_EXCEEDED"):
        if summary["observedRunningApplicationCount"] <= limits["maxRunningApplications"]:
            raise ReceiptValidationError("application-limit blocker lacks excess applications")
    elif discovery_result == ("BLOCKED", "WINDOW_LIMIT_EXCEEDED"):
        if (
            summary["maximumVerifiedApplicationWindowCount"]
            <= limits["maxWindowsPerApplication"]
        ):
            raise ReceiptValidationError("window-limit blocker lacks excess windows")
    elif discovery_result == ("NOT_RUN", "NOT_RUN"):
        if (
            summary["observedRunningApplicationCount"] != 0
            or summary["maximumVerifiedApplicationWindowCount"] != 0
        ):
            raise ReceiptValidationError("unrun discovery contains observed counts")

    if discovery_bounds_check["outcome"] == "BLOCKED":
        if packaged_identity_check["outcome"] != "NOT_RUN":
            raise ReceiptValidationError("bounded discovery blocker continued to identity checks")
    elif packaged_identity_check["outcome"] != "NOT_RUN":
        if discovery_bounds_check["outcome"] != "PASS":
            raise ReceiptValidationError("package identity ran without bounded discovery")

    if packaged_identity_check["outcome"] == "PASS":
        if summary["matchingApplicationCount"] == 0:
            raise ReceiptValidationError("verified-build check lacks a packaged application")
    elif packaged_identity_check["detailCode"] == "NO_VERIFIED_PACKAGE":
        if summary["matchingApplicationCount"] != 0:
            raise ReceiptValidationError("no-package blocker contains a verified application")

    if unique_application_check["outcome"] == "PASS":
        if summary["matchingApplicationCount"] != 1:
            raise ReceiptValidationError("unique-application PASS does not have one candidate")
    elif unique_application_check["detailCode"] == "MULTIPLE":
        if summary["matchingApplicationCount"] < 2:
            raise ReceiptValidationError("multiple-application blocker lacks candidates")
        if packaged_identity_check["outcome"] != "PASS":
            raise ReceiptValidationError("multiple candidates lack verified package identity")

    window_result = (unique_window_check["outcome"], unique_window_check["detailCode"])
    if window_result == ("PASS", "EXACTLY_ONE"):
        if (
            summary["applicationWindowCount"] != 1
            or summary["matchingMainWindowCount"] != 1
        ):
            raise ReceiptValidationError("unique-window PASS does not have one main window")
    elif window_result == ("BLOCKED", "NONE"):
        if summary["matchingMainWindowCount"] != 0:
            raise ReceiptValidationError("missing-window blocker contains a main window")
    elif window_result == ("BLOCKED", "MULTIPLE"):
        if summary["matchingMainWindowCount"] < 2:
            raise ReceiptValidationError("multiple-window blocker lacks matching windows")
    elif window_result == ("BLOCKED", "EXTRA_WINDOWS"):
        if (
            summary["matchingMainWindowCount"] != 1
            or summary["applicationWindowCount"] <= 1
        ):
            raise ReceiptValidationError("extra-window blocker counts are inconsistent")

    if identity_available:
        if (
            packaged_identity_check["outcome"] != "PASS"
            or unique_application_check["outcome"] != "PASS"
        ):
            raise ReceiptValidationError("target identity lacks verified unique application")
    if visited and unique_window_check["outcome"] != "PASS":
        raise ReceiptValidationError("tree walk lacks a verified unique main window")

    expected_tree_result = (
        ("FAIL", "LIMIT_EXCEEDED")
        if summary["treeTruncated"]
        else (("PASS", "COMPLETE") if visited else ("NOT_RUN", "NOT_RUN"))
    )
    tree_check = indexed_checks["tree-bounded"]
    if (tree_check["outcome"], tree_check["detailCode"]) != expected_tree_result:
        raise ReceiptValidationError("tree-bounded check contradicts traversal counts")

    if expected_tree_result[0] == "PASS":
        expected_metadata_result = (
            ("FAIL", "READ_ERRORS")
            if summary["metadataReadErrorCount"] > 0
            else ("PASS", "READABLE")
        )
    else:
        expected_metadata_result = ("NOT_RUN", "NOT_RUN")
    metadata_check = indexed_checks["metadata-readable"]
    if (metadata_check["outcome"], metadata_check["detailCode"]) != expected_metadata_result:
        raise ReceiptValidationError("metadata-readable check contradicts read counts")

    if expected_metadata_result[0] == "PASS":
        expected_name_result = (
            ("FAIL", "MISSING_NAMES")
            if summary["unnamedActionableElementCount"] > 0
            else ("PASS", "COMPLETE")
        )
    else:
        expected_name_result = ("NOT_RUN", "NOT_RUN")
    name_check = indexed_checks["actionable-elements-named"]
    if (name_check["outcome"], name_check["detailCode"]) != expected_name_result:
        raise ReceiptValidationError("named-control check contradicts control counts")

    if not visited:
        expected_terminal_result = ("NOT_RUN", "NOT_RUN")
        expected_focus_result = ("NOT_RUN", "NOT_RUN")
    else:
        if summary["terminalNameRoleMismatchCount"] > 0:
            expected_terminal_result = ("FAIL", "INVALID_TERMINAL_ROLE")
        elif summary["terminalElementCount"] > 0:
            expected_terminal_result = ("PASS", "NAMED_TERMINAL_PRESENT")
        else:
            expected_terminal_result = (
                "NOT_APPLICABLE",
                "NO_TERMINAL_IN_CURRENT_SURFACE",
            )

        if summary["focusMetadataElementCount"] == 0:
            expected_focus_result = ("FAIL", "UNAVAILABLE")
        elif summary["focusedElementCount"] == 0:
            expected_focus_result = ("FAIL", "NO_FOCUSED_ELEMENT")
        else:
            expected_focus_result = ("PASS", "FOCUSED_ELEMENT_PRESENT")

    terminal_check = indexed_checks["terminal-accessibility"]
    if (terminal_check["outcome"], terminal_check["detailCode"]) != expected_terminal_result:
        raise ReceiptValidationError("terminal check contradicts terminal-role counts")
    focus_check = indexed_checks["focus-state"]
    if (focus_check["outcome"], focus_check["detailCode"]) != expected_focus_result:
        raise ReceiptValidationError("focus check contradicts focus counts")

    expected_failure_reason: str | None = None
    if summary["treeTruncated"]:
        expected_failure_reason = "TREE_LIMIT_EXCEEDED"
    elif summary["metadataReadErrorCount"] > 0:
        expected_failure_reason = "METADATA_READ_FAILED"
    elif summary["unnamedActionableElementCount"] > 0:
        expected_failure_reason = "UNNAMED_ACTIONABLE_ELEMENTS"
    elif summary["terminalNameRoleMismatchCount"] > 0:
        expected_failure_reason = "TERMINAL_ROLE_INVALID"
    elif visited and summary["focusMetadataElementCount"] == 0:
        expected_failure_reason = "FOCUS_METADATA_UNAVAILABLE"
    elif visited and summary["focusedElementCount"] == 0:
        expected_failure_reason = "FOCUSED_ELEMENT_MISSING"

    if status == "PASS":
        if not identity_available:
            raise ReceiptValidationError("PASS receipt lacks packaged executable identity")
        if (
            summary["matchingApplicationCount"] != 1
            or summary["applicationWindowCount"] != 1
            or summary["matchingMainWindowCount"] != 1
            or summary["visitedNodeCount"] == 0
        ):
            raise ReceiptValidationError("PASS receipt lacks one inspected application window")
        if summary["treeTruncated"]:
            raise ReceiptValidationError("PASS receipt cannot have a truncated tree")
        if summary["metadataReadErrorCount"] != 0:
            raise ReceiptValidationError("PASS receipt cannot have metadata read errors")
        if summary["unnamedActionableElementCount"] != 0:
            raise ReceiptValidationError("PASS receipt cannot have unnamed controls")
        if expected_failure_reason is not None:
            raise ReceiptValidationError("PASS receipt contains a failing observation")
        invalid_outcomes = {"FAIL", "BLOCKED", "NOT_RUN"}
        if any(check["outcome"] in invalid_outcomes for check in checks):
            raise ReceiptValidationError("PASS receipt has incomplete or failed checks")
    elif status == "FAIL":
        if not identity_available:
            raise ReceiptValidationError("FAIL receipt lacks packaged executable identity")
        for check_id in (
            "platform",
            "screen-unlocked",
            "accessibility-trusted",
            "messaging-timeout",
            "discovery-bounds",
            "packaged-build-identity",
            "unique-application",
            "unique-main-window",
        ):
            if indexed_checks[check_id]["outcome"] != "PASS":
                raise ReceiptValidationError("FAIL receipt has an unmet probe precondition")
        if (
            summary["matchingApplicationCount"] != 1
            or summary["applicationWindowCount"] != 1
            or summary["matchingMainWindowCount"] != 1
        ):
            raise ReceiptValidationError("FAIL receipt lacks one inspected application window")
        if expected_failure_reason != reason_code:
            raise ReceiptValidationError("FAIL reason/check does not match failure priority")
    else:
        blocked_checks = {
            "AMBIGUOUS_APP_INSTANCES": ("unique-application", "MULTIPLE"),
            "AX_NOT_TRUSTED": ("accessibility-trusted", "NOT_TRUSTED"),
            "AX_TIMEOUT_CONFIGURATION_FAILED": (
                "messaging-timeout",
                "CONFIGURATION_FAILED",
            ),
            "SCREEN_LOCKED": ("screen-unlocked", "SCREEN_LOCKED"),
            "SCREEN_STATE_UNAVAILABLE": (
                "screen-unlocked",
                "SESSION_STATE_UNAVAILABLE",
            ),
        }
        if visited:
            raise ReceiptValidationError("BLOCKED receipt cannot contain a tree walk")
        if reason_code == "WINDOW_COUNT_MISMATCH":
            window_check = indexed_checks["unique-main-window"]
            if window_check["outcome"] != "BLOCKED" or not identity_available:
                raise ReceiptValidationError("window-count blocker is not recorded")
        elif reason_code == "BUILD_IDENTITY_UNAVAILABLE":
            identity_check = indexed_checks["packaged-build-identity"]
            if identity_check["outcome"] != "BLOCKED" or identity_available:
                raise ReceiptValidationError("build-identity blocker is not recorded")
        elif reason_code == "DISCOVERY_TIMEOUT":
            if discovery_result != ("BLOCKED", "DEADLINE_EXCEEDED"):
                raise ReceiptValidationError("discovery-timeout blocker is not recorded")
            if identity_available:
                raise ReceiptValidationError("discovery timeout cannot carry target identity")
        elif reason_code == "DISCOVERY_LIMIT_EXCEEDED":
            if discovery_result not in {
                ("BLOCKED", "APPLICATION_LIMIT_EXCEEDED"),
                ("BLOCKED", "WINDOW_LIMIT_EXCEEDED"),
            }:
                raise ReceiptValidationError("discovery-limit blocker is not recorded")
            if identity_available:
                raise ReceiptValidationError("discovery limit cannot carry target identity")
        else:
            check_id, detail_code = blocked_checks[reason_code]
            check = indexed_checks[check_id]
            if check["outcome"] != "BLOCKED" or check["detailCode"] != detail_code:
                raise ReceiptValidationError("BLOCKED reason is unsupported by check results")
            if identity_available:
                raise ReceiptValidationError("pre-target blocker cannot carry a target identity")


def load_receipt(path: str) -> Any:
    if path == "-":
        source = sys.stdin.read()
    else:
        source = Path(path).read_text(encoding="utf-8")
    return json.loads(source, object_pairs_hook=_object_without_duplicate_keys)


def _valid_self_test_receipt() -> dict[str, Any]:
    return {
        "architecture": "arm64",
        "checks": [
            {"detailCode": "DARWIN", "id": "platform", "outcome": "PASS"},
            {"detailCode": "UNLOCKED", "id": "screen-unlocked", "outcome": "PASS"},
            {"detailCode": "TRUSTED", "id": "accessibility-trusted", "outcome": "PASS"},
            {"detailCode": "CONFIGURED", "id": "messaging-timeout", "outcome": "PASS"},
            {
                "detailCode": "WITHIN_LIMITS",
                "id": "discovery-bounds",
                "outcome": "PASS",
            },
            {
                "detailCode": "VERIFIED",
                "id": "packaged-build-identity",
                "outcome": "PASS",
            },
            {"detailCode": "EXACTLY_ONE", "id": "unique-application", "outcome": "PASS"},
            {"detailCode": "EXACTLY_ONE", "id": "unique-main-window", "outcome": "PASS"},
            {"detailCode": "COMPLETE", "id": "tree-bounded", "outcome": "PASS"},
            {"detailCode": "READABLE", "id": "metadata-readable", "outcome": "PASS"},
            {"detailCode": "COMPLETE", "id": "actionable-elements-named", "outcome": "PASS"},
            {
                "detailCode": "NO_TERMINAL_IN_CURRENT_SURFACE",
                "id": "terminal-accessibility",
                "outcome": "NOT_APPLICABLE",
            },
            {
                "detailCode": "FOCUSED_ELEMENT_PRESENT",
                "id": "focus-state",
                "outcome": "PASS",
            },
        ],
        "limits": {
            "maxChildrenPerNode": 256,
            "maxDepth": 64,
            "maxDurationMilliseconds": 10000,
            "maxNodes": 5000,
            "maxRunningApplications": 256,
            "maxWindowsPerApplication": 16,
        },
        "platform": "macOS",
        "privacy": {
            "rawHelpEmitted": False,
            "rawNamesEmitted": False,
            "screenshotsCaptured": False,
            "userTextEmitted": False,
            "valuesQueried": False,
        },
        "probe": "asura.macos.accessibility",
        "probeVersion": "1.2.0",
        "reasonCode": "ACCEPTANCE_PASSED",
        "recordedAtUtc": "2026-07-23T00:00:00.000Z",
        "schemaVersion": 3,
        "scope": {
            "actionsExecuted": [],
            "target": "ASURA_MAIN_WINDOW",
            "traversal": "PASSIVE_METADATA_ONLY",
        },
        "status": "PASS",
        "targetIdentity": {
            "executableSha256": "a" * 64,
            "expectedBundleIdentifier": "sh.asura",
            "kind": "PACKAGED_EXECUTABLE_SHA256",
            "processId": 4242,
        },
        "summary": {
            "actionableElementCount": 1,
            "applicationWindowCount": 1,
            "childLimitHitCount": 0,
            "cycleCount": 0,
            "depthLimitHitCount": 0,
            "durationLimitHitCount": 0,
            "focusedElementCount": 1,
            "focusMetadataElementCount": 1,
            "helpMetadataElementCount": 0,
            "matchingApplicationCount": 1,
            "matchingMainWindowCount": 1,
            "maximumVerifiedApplicationWindowCount": 1,
            "metadataReadErrorCount": 0,
            "nameMetadataElementCount": 1,
            "observedRunningApplicationCount": 42,
            "stateMetadataElementCount": 1,
            "terminalElementCount": 0,
            "terminalNameRoleMismatchCount": 0,
            "treeTruncated": False,
            "unnamedActionableElementCount": 0,
            "visitedNodeCount": 2,
        },
    }


def run_self_test() -> None:
    valid = _valid_self_test_receipt()
    validate_receipt(valid)

    focused_failure = json.loads(json.dumps(valid))
    focused_failure["status"] = "FAIL"
    focused_failure["reasonCode"] = "FOCUSED_ELEMENT_MISSING"
    focused_failure["summary"]["focusedElementCount"] = 0
    focused_failure["checks"][-1] = {
        "detailCode": "NO_FOCUSED_ELEMENT",
        "id": "focus-state",
        "outcome": "FAIL",
    }
    validate_receipt(focused_failure)

    terminal_present = json.loads(json.dumps(valid))
    terminal_present["summary"]["terminalElementCount"] = 1
    terminal_present["checks"][-2] = {
        "detailCode": "NAMED_TERMINAL_PRESENT",
        "id": "terminal-accessibility",
        "outcome": "PASS",
    }
    validate_receipt(terminal_present)

    terminal_role_failure = json.loads(json.dumps(valid))
    terminal_role_failure["status"] = "FAIL"
    terminal_role_failure["reasonCode"] = "TERMINAL_ROLE_INVALID"
    terminal_role_failure["summary"]["terminalNameRoleMismatchCount"] = 1
    terminal_role_failure["checks"][-2] = {
        "detailCode": "INVALID_TERMINAL_ROLE",
        "id": "terminal-accessibility",
        "outcome": "FAIL",
    }
    validate_receipt(terminal_role_failure)

    build_identity_blocked = json.loads(json.dumps(valid))
    build_identity_blocked["status"] = "BLOCKED"
    build_identity_blocked["reasonCode"] = "BUILD_IDENTITY_UNAVAILABLE"
    build_identity_blocked["targetIdentity"]["processId"] = None
    build_identity_blocked["targetIdentity"]["executableSha256"] = None
    for key in build_identity_blocked["summary"]:
        build_identity_blocked["summary"][key] = (
            False if key == "treeTruncated" else 0
        )
    build_identity_blocked["checks"][5] = {
        "detailCode": "NO_VERIFIED_PACKAGE",
        "id": "packaged-build-identity",
        "outcome": "BLOCKED",
    }
    for index in range(6, len(build_identity_blocked["checks"])):
        check_id = build_identity_blocked["checks"][index]["id"]
        build_identity_blocked["checks"][index] = {
            "detailCode": "NOT_RUN",
            "id": check_id,
            "outcome": "NOT_RUN",
        }
    validate_receipt(build_identity_blocked)

    application_limit_blocked = json.loads(json.dumps(build_identity_blocked))
    application_limit_blocked["reasonCode"] = "DISCOVERY_LIMIT_EXCEEDED"
    application_limit_blocked["summary"]["observedRunningApplicationCount"] = 257
    application_limit_blocked["checks"][4] = {
        "detailCode": "APPLICATION_LIMIT_EXCEEDED",
        "id": "discovery-bounds",
        "outcome": "BLOCKED",
    }
    application_limit_blocked["checks"][5] = {
        "detailCode": "NOT_RUN",
        "id": "packaged-build-identity",
        "outcome": "NOT_RUN",
    }
    validate_receipt(application_limit_blocked)

    window_limit_blocked = json.loads(json.dumps(build_identity_blocked))
    window_limit_blocked["reasonCode"] = "DISCOVERY_LIMIT_EXCEEDED"
    window_limit_blocked["summary"]["observedRunningApplicationCount"] = 42
    window_limit_blocked["summary"]["maximumVerifiedApplicationWindowCount"] = 17
    window_limit_blocked["checks"][4] = {
        "detailCode": "WINDOW_LIMIT_EXCEEDED",
        "id": "discovery-bounds",
        "outcome": "BLOCKED",
    }
    window_limit_blocked["checks"][5] = {
        "detailCode": "NOT_RUN",
        "id": "packaged-build-identity",
        "outcome": "NOT_RUN",
    }
    validate_receipt(window_limit_blocked)

    deadline_blocked = json.loads(json.dumps(build_identity_blocked))
    deadline_blocked["reasonCode"] = "DISCOVERY_TIMEOUT"
    deadline_blocked["summary"]["observedRunningApplicationCount"] = 42
    deadline_blocked["checks"][4] = {
        "detailCode": "DEADLINE_EXCEEDED",
        "id": "discovery-bounds",
        "outcome": "BLOCKED",
    }
    deadline_blocked["checks"][5] = {
        "detailCode": "NOT_RUN",
        "id": "packaged-build-identity",
        "outcome": "NOT_RUN",
    }
    validate_receipt(deadline_blocked)

    invalid_cases: list[dict[str, Any]] = []

    leaked_value = json.loads(json.dumps(valid))
    leaked_value["terminalValue"] = "sensitive"
    invalid_cases.append(leaked_value)

    weakened_privacy = json.loads(json.dumps(valid))
    weakened_privacy["privacy"]["valuesQueried"] = True
    invalid_cases.append(weakened_privacy)

    unbounded_pass = json.loads(json.dumps(valid))
    unbounded_pass["summary"]["treeTruncated"] = True
    invalid_cases.append(unbounded_pass)

    missing_name_pass = json.loads(json.dumps(valid))
    missing_name_pass["summary"]["unnamedActionableElementCount"] = 1
    invalid_cases.append(missing_name_pass)

    unsafe_action = json.loads(json.dumps(valid))
    unsafe_action["scope"]["actionsExecuted"] = ["AXPress"]
    invalid_cases.append(unsafe_action)

    missing_target_identity = json.loads(json.dumps(valid))
    missing_target_identity["targetIdentity"]["processId"] = None
    missing_target_identity["targetIdentity"]["executableSha256"] = None
    invalid_cases.append(missing_target_identity)

    malformed_digest = json.loads(json.dumps(valid))
    malformed_digest["targetIdentity"]["executableSha256"] = "not-a-digest"
    invalid_cases.append(malformed_digest)

    terminal_outcome_mismatch = json.loads(json.dumps(valid))
    terminal_outcome_mismatch["summary"]["terminalElementCount"] = 1
    invalid_cases.append(terminal_outcome_mismatch)

    terminal_role_mismatch_hidden = json.loads(json.dumps(valid))
    terminal_role_mismatch_hidden["summary"]["terminalNameRoleMismatchCount"] = 1
    invalid_cases.append(terminal_role_mismatch_hidden)

    focused_count_hidden = json.loads(json.dumps(valid))
    focused_count_hidden["summary"]["focusedElementCount"] = 0
    invalid_cases.append(focused_count_hidden)

    fail_reason_mismatch = json.loads(json.dumps(focused_failure))
    fail_reason_mismatch["reasonCode"] = "METADATA_READ_FAILED"
    invalid_cases.append(fail_reason_mismatch)

    for index, invalid in enumerate(invalid_cases):
        try:
            validate_receipt(invalid)
        except ReceiptValidationError:
            continue
        raise AssertionError(f"invalid self-test receipt {index} was accepted")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("receipt", nargs="?", help="receipt path, or - for stdin")
    parser.add_argument("--self-test", action="store_true")
    arguments = parser.parse_args()

    try:
        if arguments.self_test:
            if arguments.receipt is not None:
                parser.error("receipt cannot be combined with --self-test")
            run_self_test()
            print("mac-accessibility receipt validator self-test passed")
            return 0
        if arguments.receipt is None:
            parser.error("receipt is required unless --self-test is used")
        validate_receipt(load_receipt(arguments.receipt))
        return 0
    except (OSError, json.JSONDecodeError, ReceiptValidationError) as error:
        print(f"invalid mac-accessibility receipt: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
