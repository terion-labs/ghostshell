using System.Security.Cryptography;
using System.Text;

namespace Asura.AccessibilityAcceptance;

internal static class AcceptanceCatalog
{
    public static IReadOnlyList<AcceptanceCheck> All { get; } =
    [
        Check(
            "named-interactive-host",
            "Named local interactive desktop",
            "Confirm the declared host is unlocked and you are using its direct keyboard, display, and audio with a dedicated synthetic Asura profile. Do not use remote control, automation, a container, or a virtual display.",
            "Use the local macOS console session.",
            "Use the local Windows console session rather than Remote Desktop.",
            "Use a local X11 desktop with a real window manager; Wayland, XWayland, Xvfb, VNC, and forwarded DISPLAY do not qualify.",
            ("local-direct-interaction", "The interaction is local and direct."),
            ("session-unlocked", "The desktop remains unlocked."),
            ("synthetic-test-profile", "Only synthetic test definitions and terminal text are present.")),
        Check(
            "assistive-technology-active",
            "Expected screen reader active",
            "Use only the platform screen reader identified by the runner. Confirm speech output is available and exercise its normal navigation and interaction commands rather than inferring accessibility from visual labels.",
            "Use VoiceOver with its normal VO modifier and group interaction.",
            "Use Narrator and exercise both scan mode and focused control interaction.",
            "Use Orca with AT-SPI active and exercise focus mode plus flat-review or structural navigation where appropriate.",
            ("expected-reader-running", "The runner verified the expected screen reader identity."),
            ("speech-output-observed", "The expected screen reader produced usable output."),
            ("reader-controls-used", "The operator used that screen reader's navigation controls.")),
        Check(
            "fingerprinted-package",
            "Exact packaged Asura build",
            "The runner starts and later re-fingerprints the exact package. Do not replace, update, or modify package files during this run.",
            "The package must be Asura.app with bundle identifier sh.asura.",
            "The package must contain Asura.exe.",
            "The package must contain the Asura executable.",
            ("exact-package-identity", "The package identity and initial digest were verified."),
            ("package-launched", "The fingerprinted package remained running for observation."),
            ("package-remained-unchanged", "The post-run package fingerprint matched.")),
        Check(
            "launch-orientation",
            "Launch orientation and initial focus",
            "On first presentation, verify the screen reader identifies Asura, communicates the main window or application context, and starts at a useful deterministic focus location without reading unrelated terminal or user data.",
            "Confirm VoiceOver can enter and leave the main application group.",
            "Confirm Narrator identifies the application and main window in scan and focus modes.",
            "Confirm Orca identifies the application and active top-level frame.",
            ("application-identity-announced", "The application identity is understandable."),
            ("window-context-announced", "The main window context is understandable."),
            ("initial-focus-deterministic", "Initial focus is useful and deterministic.")),
        Check(
            "semantic-controls",
            "Control names, roles, states, and values",
            "Inspect representative launcher navigation, tabs, panels, forms, lists, toggles, buttons, status elements, and the terminal. Verify each has a useful name and recognizable role, and that enabled, selected, expanded, checked, invalid, and current states are conveyed where applicable.",
            "Use VoiceOver item navigation and rotor categories where useful.",
            "Use Narrator item navigation and scan-mode landmarks where useful.",
            "Use Orca structural navigation and focus review where useful.",
            ("representative-names", "Representative controls have useful names."),
            ("representative-roles", "Representative controls expose useful roles."),
            ("state-and-value-changes", "Relevant states and values are conveyed.")),
        Check(
            "focus-order-visible-focus",
            "Logical order and visible keyboard focus",
            "Traverse forward and backward with both screen-reader navigation and Tab or Shift+Tab. Verify order follows the visual workflow, keyboard and screen-reader focus remain understandable, focus is visibly apparent, disabled or hidden controls are skipped, and there are no dead ends.",
            "Check VoiceOver cursor versus keyboard focus with Quick Nav both as needed and disabled for raw Tab traversal.",
            "Check Narrator scan-mode cursor versus keyboard focus, including toggling scan mode when forms require it.",
            "Check Orca browse/flat-review position versus keyboard focus and focus mode.",
            ("screen-reader-order-logical", "Screen-reader traversal order is logical."),
            ("tab-order-logical", "Forward and reverse Tab order is logical."),
            ("visible-focus-present", "Keyboard focus remains visibly apparent."),
            ("no-focus-dead-end", "No hidden target or dead end traps traversal.")),
        Check(
            "primary-keyboard-workflows",
            "Primary workflows without pointer input",
            "Using no pointer or drag-and-drop, navigate the launcher, open a workspace, switch tabs and panels, open settings, invoke the command palette and new-panel chooser, and complete representative save and cancel paths.",
            "Use macOS-standard keyboard navigation with VoiceOver enabled.",
            "Use Windows-standard keyboard navigation with Narrator enabled.",
            "Use X11 desktop keyboard navigation with Orca enabled.",
            ("launcher-navigation", "Launcher navigation completes by keyboard."),
            ("workspace-tab-panel-navigation", "Workspace, tab, and panel navigation completes by keyboard."),
            ("settings-palette-chooser", "Settings, palette, and chooser paths complete by keyboard."),
            ("no-pointer-or-drag", "The workflow used neither pointer nor drag-and-drop.")),
        Check(
            "modal-layout-focus-return",
            "Modal containment, layout editing, and focus return",
            "Exercise the command palette, chooser, dirty editor, active-work close confirmation, and layout designer. Verify modal traversal cycles inside, Escape and cancel are announced and safe, focus returns to the exact invoking route or panel, and layout order/move/resize is operable by keyboard.",
            "Confirm VoiceOver interaction does not escape an active modal group.",
            "Confirm Narrator scan mode does not expose inactive background controls as actionable.",
            "Confirm Orca does not navigate actionable background controls while a modal is active.",
            ("modal-focus-contained", "Modal focus remains contained."),
            ("escape-cancel-safe", "Escape and cancel preserve state safely."),
            ("focus-return-exact", "Focus returns to the exact invoking context."),
            ("layout-edit-keyboard", "Layout order, move, and resize work by keyboard.")),
        Check(
            "scale-reflow-contrast-status",
            "Text scale, reflow, contrast, and non-color status",
            "Exercise the highest supported production text scale needed for acceptance. Verify text and controls reflow without clipping or lost actions, focused and disabled states remain visually distinguishable, contrast is sufficient, and every status has text, icon, or screen-reader meaning beyond color.",
            "In Settings > Appearance, save Application text size at 200% or 250% and verify the live application reflows; display magnification is not a reflow pass.",
            "Use the Windows Text size accessibility setting at a high value and the relevant contrast theme.",
            "Use the supported GNOME text-scaling setting in the named X11 session and an appropriate high-contrast theme.",
            ("high-text-scale-exercised", "A legitimate high application text scale was exercised."),
            ("no-clipping-or-lost-actions", "Scaled content has no clipping or lost actions."),
            ("contrast-and-visible-states", "Contrast and visible state distinctions remain sufficient."),
            ("status-not-color-only", "Status meaning never depends on color alone.")),
        Check(
            "live-host-preferences",
            "Live host accessibility preferences",
            "While Asura remains running, change each supported production host text-scale, reduced-motion, reduced-transparency, or high-contrast preference and verify the running UI updates without restart. Exercise Quick Terminal reduced effects. Restore every changed host preference before the final check.",
            "Exercise Reduce motion and Reduce transparency, then change Asura's saved Application text size while the app remains open.",
            "Exercise Text size, Animation effects, Transparency effects, and contrast settings supported by the host.",
            "Exercise the portal/GNOME text-scale and animation preferences supported by this X11 desktop.",
            ("live-text-scale-update", "Supported text-scale changes update the running UI."),
            ("live-motion-transparency-update", "Supported motion/transparency changes update the running UI."),
            ("quick-terminal-reduced-effects", "Quick Terminal respects reduced effects.")),
        Check(
            "live-announcements-errors",
            "Session, connection, and error announcements",
            "Using synthetic local state only, observe session starting, ready, recoverable failure, retry, and ended transitions plus a deliberately invalid connection or validation error. Verify concise announcements occur, remain distinguishable from focus changes, do not repeat indefinitely, and expose destructive runtime effects before confirmation.",
            "Listen for VoiceOver announcements without capturing a speech transcript.",
            "Listen for Narrator live-region announcements without capturing a speech transcript.",
            "Listen for Orca live-region announcements without capturing a speech transcript.",
            ("session-state-announced", "Session lifecycle transitions are announced."),
            ("connection-error-announced", "A synthetic connection or validation error is announced."),
            ("recoverable-action-announced", "Retry/recovery and destructive effects are understandable."),
            ("announcement-does-not-steal-focus", "Announcements do not unexpectedly steal focus.")),
        Check(
            "terminal-quick-terminal-cleanup",
            "Terminal, Quick Terminal, restoration, and cleanup",
            "Verify the terminal has a stable name and status, terminal focus can be entered and left without trapping screen-reader controls, and Quick Terminal announces its context, receives focus, dismisses with Escape, and restores the previous target. Restore host preferences, close the package normally, and leave the screen reader running.",
            "Use the configured macOS Quick Terminal shortcut and VoiceOver.",
            "Use the RegisterHotKey-backed shortcut and Narrator.",
            "Use the XGrabKey-backed shortcut in this real X11 session and Orca.",
            ("terminal-name-and-status", "The terminal name and status are understandable."),
            ("terminal-focus-enter-exit", "Terminal focus can be entered and left safely."),
            ("quick-terminal-focus-restore", "Quick Terminal focus and restoration are correct."),
            ("preferences-restored", "All changed host accessibility preferences are restored."),
            ("package-exited", "The runner-owned package process exited normally."),
            ("screen-reader-remained-active", "The expected screen reader remains active.")),
    ];

    public static string Digest { get; } = ComputeDigest();

    private static AcceptanceCheck Check(
        string id,
        string title,
        string commonInstructions,
        string macOSInstructions,
        string windowsInstructions,
        string linuxInstructions,
        params (string Id, string Instructions)[] assertions) =>
        new(
            id,
            title,
            commonInstructions,
            macOSInstructions,
            windowsInstructions,
            linuxInstructions,
            [.. assertions.Select(assertion => new AcceptanceAssertion(
                assertion.Id,
                assertion.Instructions))]);

    private static string ComputeDigest()
    {
        var source = new StringBuilder();
        foreach (var check in All)
        {
            source
                .Append(check.Id).Append('\0')
                .Append(check.Title).Append('\0')
                .Append(check.CommonInstructions).Append('\0')
                .Append(check.MacOSInstructions).Append('\0')
                .Append(check.WindowsInstructions).Append('\0')
                .Append(check.LinuxInstructions).Append('\0');
            foreach (var assertion in check.Assertions)
            {
                source
                    .Append(assertion.Id).Append('\0')
                    .Append(assertion.Instructions).Append('\0');
            }
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(source.ToString()))).ToLowerInvariant();
    }
}
