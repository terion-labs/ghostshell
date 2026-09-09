import AppKit
import ApplicationServices
import CoreGraphics
import CryptoKit
import Foundation

// This acceptance binary treats the AX tree as sensitive input. It inspects a
// fixed metadata allowlist, keeps raw strings in memory only long enough to
// classify each element, and emits controlled codes and aggregate counts.
private let receiptSchemaVersion = 3
private let currentProbeVersion = "1.2.0"
private let mainWindowTitle = "Asura"
private let asuraBundleIdentifier = "sh.asura"
private let expectedBundleName = "Asura.app"
private let expectedExecutableName = "Asura"
private let screenLockedSessionKey = "CGSSessionScreenIsLocked"
private let messagingTimeoutSeconds: Float = 0.25

private enum ReceiptStatus: String, Encodable {
    case pass = "PASS"
    case fail = "FAIL"
    case blocked = "BLOCKED"
}

private enum CheckOutcome: String, Encodable {
    case pass = "PASS"
    case fail = "FAIL"
    case blocked = "BLOCKED"
    case notApplicable = "NOT_APPLICABLE"
    case notRun = "NOT_RUN"
}

private struct ReceiptCheck: Encodable {
    let id: String
    var outcome: CheckOutcome
    var detailCode: String
}

private struct WalkLimits: Encodable {
    let maxRunningApplications: Int
    let maxWindowsPerApplication: Int
    let maxNodes: Int
    let maxDepth: Int
    let maxChildrenPerNode: Int
    let maxDurationMilliseconds: Int
}

private struct ReceiptSummary: Encodable {
    var matchingApplicationCount = 0
    var observedRunningApplicationCount = 0
    var maximumVerifiedApplicationWindowCount = 0
    var applicationWindowCount = 0
    var matchingMainWindowCount = 0
    var visitedNodeCount = 0
    var cycleCount = 0
    var depthLimitHitCount = 0
    var childLimitHitCount = 0
    var durationLimitHitCount = 0
    var actionableElementCount = 0
    var unnamedActionableElementCount = 0
    var nameMetadataElementCount = 0
    var helpMetadataElementCount = 0
    var focusMetadataElementCount = 0
    var focusedElementCount = 0
    var stateMetadataElementCount = 0
    var terminalElementCount = 0
    var terminalNameRoleMismatchCount = 0
    var metadataReadErrorCount = 0
    var treeTruncated = false
}

private struct ReceiptTargetIdentity: Encodable {
    let kind = "PACKAGED_EXECUTABLE_SHA256"
    let expectedBundleIdentifier = asuraBundleIdentifier
    let processId: Int32?
    let executableSha256: String?

    private enum CodingKeys: String, CodingKey {
        case kind
        case expectedBundleIdentifier
        case processId
        case executableSha256
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(kind, forKey: .kind)
        try container.encode(expectedBundleIdentifier, forKey: .expectedBundleIdentifier)
        if let processId {
            try container.encode(processId, forKey: .processId)
        } else {
            try container.encodeNil(forKey: .processId)
        }
        if let executableSha256 {
            try container.encode(executableSha256, forKey: .executableSha256)
        } else {
            try container.encodeNil(forKey: .executableSha256)
        }
    }
}

private struct ReceiptScope: Encodable {
    let target = "ASURA_MAIN_WINDOW"
    let traversal = "PASSIVE_METADATA_ONLY"
    let actionsExecuted: [String] = []
}

private struct ReceiptPrivacy: Encodable {
    let valuesQueried = false
    let rawNamesEmitted = false
    let rawHelpEmitted = false
    let userTextEmitted = false
    let screenshotsCaptured = false
}

private struct AccessibilityReceipt: Encodable {
    let schemaVersion = receiptSchemaVersion
    let probe = "asura.macos.accessibility"
    let probeVersion = currentProbeVersion
    let recordedAtUtc: String
    let status: ReceiptStatus
    let reasonCode: String
    let platform = "macOS"
    let architecture: String
    let scope = ReceiptScope()
    let privacy = ReceiptPrivacy()
    let targetIdentity: ReceiptTargetIdentity
    let limits: WalkLimits
    let summary: ReceiptSummary
    let checks: [ReceiptCheck]
}

private struct ProbeResult {
    let receipt: AccessibilityReceipt
    let exitCode: Int32
}

private struct AsuraCandidate {
    let identity: ReceiptTargetIdentity
    let windows: [AXUIElement]
    let matchingMainWindows: [AXUIElement]
}

private struct CandidateSearch {
    let candidates: [AsuraCandidate]
    let observedRunningApplicationCount: Int
    let maximumVerifiedApplicationWindowCount: Int
    let blocker: CandidateSearchBlocker?
}

private enum CandidateSearchBlocker: String {
    case applicationLimitExceeded = "APPLICATION_LIMIT_EXCEEDED"
    case deadlineExceeded = "DEADLINE_EXCEEDED"
    case windowLimitExceeded = "WINDOW_LIMIT_EXCEEDED"

    var reasonCode: String {
        switch self {
        case .deadlineExceeded:
            "DISCOVERY_TIMEOUT"
        case .applicationLimitExceeded, .windowLimitExceeded:
            "DISCOVERY_LIMIT_EXCEEDED"
        }
    }
}

private struct MetadataRead<T> {
    let value: T?
    let failed: Bool
}

private struct VisitedElements {
    private var elementsByHash: [CFHashCode: [AXUIElement]] = [:]

    mutating func insert(_ element: AXUIElement) -> Bool {
        let hash = CFHash(element)
        if elementsByHash[hash]?.contains(where: { CFEqual($0, element) }) == true {
            return false
        }

        elementsByHash[hash, default: []].append(element)
        return true
    }
}

private struct AccessibilityTreeWalk {
    private static let actionableRoles: Set<String> = [
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
    ]

    private static let terminalNames: Set<String> = [
        "Interactive terminal",
        "Native interactive terminal",
    ]

    private static let terminalRoles: Set<String> = [
        "AXGroup",
        "AXScrollArea",
        "AXTextArea",
    ]

    let limits: WalkLimits

    func inspect(root: AXUIElement) -> ReceiptSummary {
        var summary = ReceiptSummary()
        var visited = VisitedElements()
        var pending: [(element: AXUIElement, depth: Int)] = [(root, 0)]
        let deadline = ProcessInfo.processInfo.systemUptime
            + Double(limits.maxDurationMilliseconds) / 1_000

        while let current = pending.popLast() {
            guard ProcessInfo.processInfo.systemUptime <= deadline else {
                summary.durationLimitHitCount += 1
                summary.treeTruncated = true
                break
            }

            if summary.visitedNodeCount >= limits.maxNodes {
                summary.treeTruncated = true
                break
            }

            guard visited.insert(current.element) else {
                summary.cycleCount += 1
                continue
            }

            summary.visitedNodeCount += 1
            inspectMetadata(of: current.element, summary: &summary)

            let children = readArray(current.element, attribute: kAXChildrenAttribute as CFString)
            if children.failed {
                summary.metadataReadErrorCount += 1
            }

            guard let childElements = children.value, !childElements.isEmpty else {
                continue
            }

            guard current.depth < limits.maxDepth else {
                summary.depthLimitHitCount += 1
                summary.treeTruncated = true
                continue
            }

            let acceptedChildren = childElements.prefix(limits.maxChildrenPerNode)
            if childElements.count > limits.maxChildrenPerNode {
                summary.childLimitHitCount += 1
                summary.treeTruncated = true
            }

            for child in acceptedChildren.reversed() {
                pending.append((child, current.depth + 1))
            }
        }

        return summary
    }

    private func inspectMetadata(
        of element: AXUIElement,
        summary: inout ReceiptSummary
    ) {
        let role = readString(element, attribute: kAXRoleAttribute as CFString)
        let title = readString(element, attribute: kAXTitleAttribute as CFString)
        let description = readString(element, attribute: kAXDescriptionAttribute as CFString)
        let help = readString(element, attribute: kAXHelpAttribute as CFString)
        let focused = readBoolean(element, attribute: kAXFocusedAttribute as CFString)
        let enabled = readBoolean(element, attribute: kAXEnabledAttribute as CFString)
        let selected = readBoolean(element, attribute: kAXSelectedAttribute as CFString)
        let expanded = readBoolean(element, attribute: kAXExpandedAttribute as CFString)

        let readsFailed = [
            role.failed,
            title.failed,
            description.failed,
            help.failed,
            focused.failed,
            enabled.failed,
            selected.failed,
            expanded.failed,
        ].filter { $0 }.count
        summary.metadataReadErrorCount += readsFailed

        let names = [title.value, description.value]
            .compactMap(normalizedMetadata)
        let hasName = !names.isEmpty
        if hasName {
            summary.nameMetadataElementCount += 1
        }
        if normalizedMetadata(help.value) != nil {
            summary.helpMetadataElementCount += 1
        }

        if let roleValue = role.value,
           Self.actionableRoles.contains(roleValue)
        {
            summary.actionableElementCount += 1
            if !hasName {
                summary.unnamedActionableElementCount += 1
            }
        }

        if names.contains(where: Self.terminalNames.contains) {
            if let roleValue = role.value,
               Self.terminalRoles.contains(roleValue)
            {
                summary.terminalElementCount += 1
            } else {
                summary.terminalNameRoleMismatchCount += 1
            }
        }

        if let isFocused = focused.value {
            summary.focusMetadataElementCount += 1
            if isFocused {
                summary.focusedElementCount += 1
            }
        }

        if enabled.value != nil || selected.value != nil || expanded.value != nil {
            summary.stateMetadataElementCount += 1
        }
    }

    private func normalizedMetadata(_ value: String?) -> String? {
        guard let value else {
            return nil
        }

        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return normalized.isEmpty ? nil : normalized
    }
}

private let checkOrder = [
    "platform",
    "screen-unlocked",
    "accessibility-trusted",
    "messaging-timeout",
    "discovery-bounds",
    "packaged-build-identity",
    "unique-application",
    "unique-main-window",
    "tree-bounded",
    "metadata-readable",
    "actionable-elements-named",
    "terminal-accessibility",
    "focus-state",
]

private struct CheckLedger {
    private var checks = checkOrder.map {
        ReceiptCheck(id: $0, outcome: .notRun, detailCode: "NOT_RUN")
    }

    mutating func record(
        _ id: String,
        _ outcome: CheckOutcome,
        _ detailCode: String
    ) {
        guard let index = checks.firstIndex(where: { $0.id == id }) else {
            preconditionFailure("Unknown receipt check identifier")
        }

        checks[index].outcome = outcome
        checks[index].detailCode = detailCode
    }

    var orderedChecks: [ReceiptCheck] {
        checks
    }
}

private final class AsuraAccessibilityProbe {
    private let limits = WalkLimits(
        maxRunningApplications: 256,
        maxWindowsPerApplication: 16,
        maxNodes: 5_000,
        maxDepth: 64,
        maxChildrenPerNode: 256,
        maxDurationMilliseconds: 10_000)

    @MainActor
    func run() -> ProbeResult {
        var checks = CheckLedger()
        checks.record("platform", .pass, "DARWIN")

        guard let screenLocked = currentScreenLockState() else {
            checks.record("screen-unlocked", .blocked, "SESSION_STATE_UNAVAILABLE")
            return result(
                status: .blocked,
                reasonCode: "SCREEN_STATE_UNAVAILABLE",
                summary: ReceiptSummary(),
                checks: checks,
                exitCode: 2)
        }

        guard !screenLocked else {
            checks.record("screen-unlocked", .blocked, "SCREEN_LOCKED")
            return result(
                status: .blocked,
                reasonCode: "SCREEN_LOCKED",
                summary: ReceiptSummary(),
                checks: checks,
                exitCode: 2)
        }
        checks.record("screen-unlocked", .pass, "UNLOCKED")

        // Passing no options checks trust without presenting an OS permission prompt.
        guard AXIsProcessTrusted() else {
            checks.record("accessibility-trusted", .blocked, "NOT_TRUSTED")
            return result(
                status: .blocked,
                reasonCode: "AX_NOT_TRUSTED",
                summary: ReceiptSummary(),
                checks: checks,
                exitCode: 2)
        }
        checks.record("accessibility-trusted", .pass, "TRUSTED")

        let systemWideElement = AXUIElementCreateSystemWide()
        guard AXUIElementSetMessagingTimeout(
            systemWideElement,
            messagingTimeoutSeconds) == .success
        else {
            checks.record("messaging-timeout", .blocked, "CONFIGURATION_FAILED")
            return result(
                status: .blocked,
                reasonCode: "AX_TIMEOUT_CONFIGURATION_FAILED",
                summary: ReceiptSummary(),
                checks: checks,
                exitCode: 2)
        }
        checks.record("messaging-timeout", .pass, "CONFIGURED")
        defer {
            _ = AXUIElementSetMessagingTimeout(systemWideElement, 0)
        }

        let candidateSearch = findCandidates()
        let candidates = candidateSearch.candidates
        var summary = ReceiptSummary()
        summary.matchingApplicationCount = candidates.count
        summary.observedRunningApplicationCount =
            candidateSearch.observedRunningApplicationCount
        summary.maximumVerifiedApplicationWindowCount =
            candidateSearch.maximumVerifiedApplicationWindowCount
        summary.applicationWindowCount = candidates.reduce(0) {
            $0 + $1.windows.count
        }
        summary.matchingMainWindowCount = candidates.reduce(0) {
            $0 + $1.matchingMainWindows.count
        }

        guard let discoveryBlocker = candidateSearch.blocker else {
            checks.record("discovery-bounds", .pass, "WITHIN_LIMITS")
            return continueAfterBoundedDiscovery(
                candidates: candidates,
                summary: summary,
                checks: checks)
        }

        checks.record("discovery-bounds", .blocked, discoveryBlocker.rawValue)
        return result(
            status: .blocked,
            reasonCode: discoveryBlocker.reasonCode,
            summary: summary,
            checks: checks,
            exitCode: 2)
    }

    private func continueAfterBoundedDiscovery(
        candidates: [AsuraCandidate],
        summary initialSummary: ReceiptSummary,
        checks initialChecks: CheckLedger
    ) -> ProbeResult {
        var summary = initialSummary
        var checks = initialChecks

        guard !candidates.isEmpty else {
            checks.record("packaged-build-identity", .blocked, "NO_VERIFIED_PACKAGE")
            return result(
                status: .blocked,
                reasonCode: "BUILD_IDENTITY_UNAVAILABLE",
                summary: summary,
                checks: checks,
                exitCode: 2)
        }
        checks.record("packaged-build-identity", .pass, "VERIFIED")

        guard candidates.count == 1 else {
            checks.record("unique-application", .blocked, "MULTIPLE")
            return result(
                status: .blocked,
                reasonCode: "AMBIGUOUS_APP_INSTANCES",
                summary: summary,
                checks: checks,
                exitCode: 2)
        }
        checks.record("unique-application", .pass, "EXACTLY_ONE")

        let candidate = candidates[0]
        guard candidate.windows.count == 1,
              candidate.matchingMainWindows.count == 1,
              let mainWindow = candidate.matchingMainWindows.first
        else {
            let detailCode: String
            if candidate.matchingMainWindows.isEmpty {
                detailCode = "NONE"
            } else if candidate.matchingMainWindows.count > 1 {
                detailCode = "MULTIPLE"
            } else {
                detailCode = "EXTRA_WINDOWS"
            }
            checks.record("unique-main-window", .blocked, detailCode)
            return result(
                status: .blocked,
                reasonCode: "WINDOW_COUNT_MISMATCH",
                summary: summary,
                checks: checks,
                targetIdentity: candidate.identity,
                exitCode: 2)
        }
        checks.record("unique-main-window", .pass, "EXACTLY_ONE")

        let walked = AccessibilityTreeWalk(limits: limits).inspect(root: mainWindow)
        summary.visitedNodeCount = walked.visitedNodeCount
        summary.cycleCount = walked.cycleCount
        summary.depthLimitHitCount = walked.depthLimitHitCount
        summary.childLimitHitCount = walked.childLimitHitCount
        summary.durationLimitHitCount = walked.durationLimitHitCount
        summary.actionableElementCount = walked.actionableElementCount
        summary.unnamedActionableElementCount = walked.unnamedActionableElementCount
        summary.nameMetadataElementCount = walked.nameMetadataElementCount
        summary.helpMetadataElementCount = walked.helpMetadataElementCount
        summary.focusMetadataElementCount = walked.focusMetadataElementCount
        summary.focusedElementCount = walked.focusedElementCount
        summary.stateMetadataElementCount = walked.stateMetadataElementCount
        summary.terminalElementCount = walked.terminalElementCount
        summary.terminalNameRoleMismatchCount = walked.terminalNameRoleMismatchCount
        summary.metadataReadErrorCount = walked.metadataReadErrorCount
        summary.treeTruncated = walked.treeTruncated

        recordObservedAccessibilityChecks(summary: summary, checks: &checks)

        guard !summary.treeTruncated else {
            checks.record("tree-bounded", .fail, "LIMIT_EXCEEDED")
            return result(
                status: .fail,
                reasonCode: "TREE_LIMIT_EXCEEDED",
                summary: summary,
                checks: checks,
                targetIdentity: candidate.identity,
                exitCode: 1)
        }
        checks.record("tree-bounded", .pass, "COMPLETE")

        guard summary.metadataReadErrorCount == 0 else {
            checks.record("metadata-readable", .fail, "READ_ERRORS")
            return result(
                status: .fail,
                reasonCode: "METADATA_READ_FAILED",
                summary: summary,
                checks: checks,
                targetIdentity: candidate.identity,
                exitCode: 1)
        }
        checks.record("metadata-readable", .pass, "READABLE")

        guard summary.unnamedActionableElementCount == 0 else {
            checks.record("actionable-elements-named", .fail, "MISSING_NAMES")
            return result(
                status: .fail,
                reasonCode: "UNNAMED_ACTIONABLE_ELEMENTS",
                summary: summary,
                checks: checks,
                targetIdentity: candidate.identity,
                exitCode: 1)
        }
        checks.record("actionable-elements-named", .pass, "COMPLETE")

        guard summary.terminalNameRoleMismatchCount == 0 else {
            return result(
                status: .fail,
                reasonCode: "TERMINAL_ROLE_INVALID",
                summary: summary,
                checks: checks,
                targetIdentity: candidate.identity,
                exitCode: 1)
        }

        guard summary.focusMetadataElementCount > 0 else {
            return result(
                status: .fail,
                reasonCode: "FOCUS_METADATA_UNAVAILABLE",
                summary: summary,
                checks: checks,
                targetIdentity: candidate.identity,
                exitCode: 1)
        }

        guard summary.focusedElementCount > 0 else {
            return result(
                status: .fail,
                reasonCode: "FOCUSED_ELEMENT_MISSING",
                summary: summary,
                checks: checks,
                targetIdentity: candidate.identity,
                exitCode: 1)
        }

        return result(
            status: .pass,
            reasonCode: "ACCEPTANCE_PASSED",
            summary: summary,
            checks: checks,
            targetIdentity: candidate.identity,
            exitCode: 0)
    }

    private func recordObservedAccessibilityChecks(
        summary: ReceiptSummary,
        checks: inout CheckLedger
    ) {
        if summary.terminalNameRoleMismatchCount > 0 {
            checks.record("terminal-accessibility", .fail, "INVALID_TERMINAL_ROLE")
        } else if summary.terminalElementCount > 0 {
            checks.record("terminal-accessibility", .pass, "NAMED_TERMINAL_PRESENT")
        } else {
            checks.record(
                "terminal-accessibility",
                .notApplicable,
                "NO_TERMINAL_IN_CURRENT_SURFACE")
        }

        if summary.focusMetadataElementCount == 0 {
            checks.record("focus-state", .fail, "UNAVAILABLE")
        } else if summary.focusedElementCount == 0 {
            checks.record("focus-state", .fail, "NO_FOCUSED_ELEMENT")
        } else {
            checks.record("focus-state", .pass, "FOCUSED_ELEMENT_PRESENT")
        }
    }

    @MainActor
    private func findCandidates() -> CandidateSearch {
        var candidates: [AsuraCandidate] = []
        var maximumVerifiedApplicationWindowCount = 0
        let runningApplications = NSWorkspace.shared.runningApplications
        let observedRunningApplicationCount = runningApplications.count
        guard observedRunningApplicationCount <= limits.maxRunningApplications else {
            return CandidateSearch(
                candidates: candidates,
                observedRunningApplicationCount: observedRunningApplicationCount,
                maximumVerifiedApplicationWindowCount: 0,
                blocker: .applicationLimitExceeded)
        }

        let deadline = ProcessInfo.processInfo.systemUptime
            + Double(limits.maxDurationMilliseconds) / 1_000

        for application in runningApplications {
            guard ProcessInfo.processInfo.systemUptime <= deadline else {
                return CandidateSearch(
                    candidates: candidates,
                    observedRunningApplicationCount: observedRunningApplicationCount,
                    maximumVerifiedApplicationWindowCount:
                        maximumVerifiedApplicationWindowCount,
                    blocker: .deadlineExceeded)
            }

            guard !application.isTerminated,
                  application.processIdentifier != ProcessInfo.processInfo.processIdentifier
            else {
                continue
            }

            guard let identity = packagedBuildIdentity(
                application: application,
                deadline: deadline)
            else {
                if ProcessInfo.processInfo.systemUptime > deadline {
                    return CandidateSearch(
                        candidates: candidates,
                        observedRunningApplicationCount: observedRunningApplicationCount,
                        maximumVerifiedApplicationWindowCount:
                            maximumVerifiedApplicationWindowCount,
                        blocker: .deadlineExceeded)
                }
                continue
            }

            let accessibilityApplication = AXUIElementCreateApplication(
                application.processIdentifier)
            let windows = readArray(
                accessibilityApplication,
                attribute: kAXWindowsAttribute as CFString)
            let windowElements = windows.value ?? []
            guard ProcessInfo.processInfo.systemUptime <= deadline else {
                return CandidateSearch(
                    candidates: candidates,
                    observedRunningApplicationCount: observedRunningApplicationCount,
                    maximumVerifiedApplicationWindowCount:
                        maximumVerifiedApplicationWindowCount,
                    blocker: .deadlineExceeded)
            }
            maximumVerifiedApplicationWindowCount = max(
                maximumVerifiedApplicationWindowCount,
                windowElements.count)
            guard windowElements.count <= limits.maxWindowsPerApplication else {
                return CandidateSearch(
                    candidates: candidates,
                    observedRunningApplicationCount: observedRunningApplicationCount,
                    maximumVerifiedApplicationWindowCount:
                        maximumVerifiedApplicationWindowCount,
                    blocker: .windowLimitExceeded)
            }

            var matchingWindows: [AXUIElement] = []
            for window in windowElements {
                guard ProcessInfo.processInfo.systemUptime <= deadline else {
                    return CandidateSearch(
                        candidates: candidates,
                        observedRunningApplicationCount: observedRunningApplicationCount,
                        maximumVerifiedApplicationWindowCount:
                            maximumVerifiedApplicationWindowCount,
                        blocker: .deadlineExceeded)
                }
                if readString(window, attribute: kAXTitleAttribute as CFString).value
                    == mainWindowTitle
                {
                    matchingWindows.append(window)
                }
            }

            candidates.append(AsuraCandidate(
                identity: identity,
                windows: windowElements,
                matchingMainWindows: matchingWindows))
        }

        return CandidateSearch(
            candidates: candidates,
            observedRunningApplicationCount: observedRunningApplicationCount,
            maximumVerifiedApplicationWindowCount:
                maximumVerifiedApplicationWindowCount,
            blocker: nil)
    }

    @MainActor
    private func packagedBuildIdentity(
        application: NSRunningApplication,
        deadline: TimeInterval
    ) -> ReceiptTargetIdentity? {
        guard application.activationPolicy == .regular,
              application.bundleIdentifier == asuraBundleIdentifier,
              let bundleUrl = application.bundleURL,
              bundleUrl.isFileURL,
              bundleUrl.lastPathComponent == expectedBundleName,
              let bundle = Bundle(url: bundleUrl),
              bundle.bundleIdentifier == asuraBundleIdentifier,
              bundle.object(forInfoDictionaryKey: "CFBundlePackageType") as? String
                == "APPL",
              bundle.object(forInfoDictionaryKey: "CFBundleExecutable") as? String
                == expectedExecutableName,
              let applicationExecutableUrl = application.executableURL,
              let bundleExecutableUrl = bundle.executableURL,
              applicationExecutableUrl.lastPathComponent == expectedExecutableName,
              application.processIdentifier > 0
        else {
            return nil
        }

        let resolvedBundleUrl = bundleUrl.resolvingSymlinksInPath().standardizedFileURL
        let resolvedApplicationExecutableUrl = applicationExecutableUrl
            .resolvingSymlinksInPath()
            .standardizedFileURL
        let resolvedBundleExecutableUrl = bundleExecutableUrl
            .resolvingSymlinksInPath()
            .standardizedFileURL
        let expectedExecutableDirectory = resolvedBundleUrl
            .appendingPathComponent("Contents", isDirectory: true)
            .appendingPathComponent("MacOS", isDirectory: true)
            .standardizedFileURL

        guard resolvedApplicationExecutableUrl.path == resolvedBundleExecutableUrl.path,
              resolvedApplicationExecutableUrl.deletingLastPathComponent().path
                == expectedExecutableDirectory.path,
              FileManager.default.isExecutableFile(
                atPath: resolvedApplicationExecutableUrl.path),
              let resourceValues = try? resolvedApplicationExecutableUrl.resourceValues(
                forKeys: [.isRegularFileKey]),
              resourceValues.isRegularFile == true,
              let digest = sha256Executable(
                at: resolvedApplicationExecutableUrl,
                deadline: deadline),
              !application.isTerminated
        else {
            return nil
        }

        return ReceiptTargetIdentity(
            processId: application.processIdentifier,
            executableSha256: digest)
    }

    private func sha256Executable(
        at executableUrl: URL,
        deadline: TimeInterval
    ) -> String? {
        guard let executable = try? FileHandle(forReadingFrom: executableUrl) else {
            return nil
        }
        defer {
            try? executable.close()
        }

        var hasher = SHA256()
        do {
            while ProcessInfo.processInfo.systemUptime <= deadline {
                guard let chunk = try executable.read(upToCount: 1_048_576),
                      !chunk.isEmpty
                else {
                    return hasher.finalize().map { String(format: "%02x", $0) }
                        .joined()
                }
                hasher.update(data: chunk)
            }
        } catch {
            return nil
        }

        return nil
    }

    private func result(
        status: ReceiptStatus,
        reasonCode: String,
        summary: ReceiptSummary,
        checks: CheckLedger,
        targetIdentity: ReceiptTargetIdentity? = nil,
        exitCode: Int32
    ) -> ProbeResult {
        ProbeResult(
            receipt: AccessibilityReceipt(
                recordedAtUtc: Self.currentTimestamp(),
                status: status,
                reasonCode: reasonCode,
                architecture: Self.architecture,
                targetIdentity: targetIdentity ?? ReceiptTargetIdentity(
                    processId: nil,
                    executableSha256: nil),
                limits: limits,
                summary: summary,
                checks: checks.orderedChecks),
            exitCode: exitCode)
    }

    private func currentScreenLockState() -> Bool? {
        guard let session = CGSessionCopyCurrentDictionary() as? [String: Any],
              let screenLocked = session[screenLockedSessionKey] as? NSNumber
        else {
            return nil
        }

        return screenLocked.boolValue
    }

    private static func currentTimestamp() -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: Date())
    }

    private static var architecture: String {
        #if arch(arm64)
        "arm64"
        #elseif arch(x86_64)
        "x86_64"
        #else
        "unsupported"
        #endif
    }
}

private func readString(
    _ element: AXUIElement,
    attribute: CFString
) -> MetadataRead<String> {
    readMetadata(element, attribute: attribute, as: String.self)
}

private func readBoolean(
    _ element: AXUIElement,
    attribute: CFString
) -> MetadataRead<Bool> {
    let raw = readMetadata(element, attribute: attribute, as: NSNumber.self)
    return MetadataRead(value: raw.value?.boolValue, failed: raw.failed)
}

private func readArray(
    _ element: AXUIElement,
    attribute: CFString
) -> MetadataRead<[AXUIElement]> {
    readMetadata(element, attribute: attribute, as: [AXUIElement].self)
}

private func readMetadata<T>(
    _ element: AXUIElement,
    attribute: CFString,
    as _: T.Type
) -> MetadataRead<T> {
    // Keep every AX read behind this boundary so the source-contract test can
    // prove that no content attributes or mutation APIs entered the probe.
    var rawValue: CFTypeRef?
    let error = AXUIElementCopyAttributeValue(element, attribute, &rawValue)
    switch error {
    case .success:
        return MetadataRead(value: rawValue as? T, failed: false)
    case .attributeUnsupported, .noValue:
        return MetadataRead(value: nil, failed: false)
    default:
        return MetadataRead(value: nil, failed: true)
    }
}

@main
private enum Main {
    @MainActor
    static func main() {
        let result = AsuraAccessibilityProbe().run()
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]

        guard let encoded = try? encoder.encode(result.receipt) else {
            // This fixed fallback carries no application metadata or user content.
            FileHandle.standardOutput.write(Data("{\"status\":\"BLOCKED\",\"reasonCode\":\"INTERNAL_ERROR\"}\n".utf8))
            exit(70)
        }

        FileHandle.standardOutput.write(encoded)
        FileHandle.standardOutput.write(Data("\n".utf8))
        exit(result.exitCode)
    }
}
