namespace Asura.App;

internal enum QuickTerminalEscapeAction
{
    Ignore,
    CancelPendingInteraction,
    Hide,
}

/// <summary>
/// Keeps Quick Terminal lifecycle decisions independent of Avalonia window callbacks so focus,
/// Escape, reuse, and reset behavior can be verified without a compositor or native hotkey loop.
/// </summary>
internal static class QuickTerminalRuntimeRules
{
    public static bool ShouldDismissForFocusLoss(
        bool isVisible,
        bool hideOnFocusLoss) =>
        isVisible && hideOnFocusLoss;

    public static QuickTerminalEscapeAction ResolveEscape(
        bool isVisible,
        bool pendingInteractionCancelled) =>
        !isVisible
            ? QuickTerminalEscapeAction.Ignore
            : pendingInteractionCancelled
                ? QuickTerminalEscapeAction.CancelPendingInteraction
                : QuickTerminalEscapeAction.Hide;

    public static bool ShouldResetAfterHide(bool restoreLastSession) =>
        !restoreLastSession;

    public static bool ShouldResetForDefinitionOrPolicyChange(
        bool terminalDefinitionsChanged,
        bool previousRestoreLastSession,
        bool restoreLastSession,
        bool isVisible) =>
        terminalDefinitionsChanged
        || (previousRestoreLastSession && !restoreLastSession && !isVisible);
}
