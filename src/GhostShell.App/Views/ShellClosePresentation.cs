using Avalonia.Controls;
using GhostShell.Application;

namespace GhostShell.App.Views;

internal sealed record ShellClosePresentation(
    Func<Task<bool>> ConfirmLayoutDiscardAsync,
    Func<string, string, Task<bool>> ConfirmDiscardAsync,
    Func<CloseScopeResult.ConfirmationRequired, Task<bool>> ConfirmScopeAsync,
    Func<string, Task> ShowErrorAsync,
    Action RestoreFocus,
    Action FocusCurrentRoute,
    Action CloseWindow)
{
    public ShellClosePresentation WithPrivacyGuard(Func<bool> isLocked) => this with
    {
        ConfirmLayoutDiscardAsync = () => isLocked()
            ? ConfirmDiscardAsync("Discard unsaved changes?", "Unsaved changes will be lost.")
            : ConfirmLayoutDiscardAsync(),
        ConfirmDiscardAsync = (title, detail) => isLocked()
            ? ConfirmDiscardAsync("Discard unsaved changes?", "Unsaved changes will be lost.")
            : ConfirmDiscardAsync(title, detail),
        ConfirmScopeAsync = confirmation => ConfirmScopeAsync(isLocked()
            ? confirmation with { TargetId = string.Empty, Sessions = [] }
            : confirmation),
        ShowErrorAsync = message => ShowErrorAsync(isLocked()
            ? "The application could not finish closing. Unlock it to review the details."
            : message),
    };

    public static ShellClosePresentation ForWindow(
        Window owner,
        ShellFocusNavigator focus) => new(
        () => Confirmations.DiscardChanges().ShowDialog<bool>(owner),
        (title, detail) => Confirmations.DiscardChanges(title, detail)
            .ShowDialog<bool>(owner),
        confirmation => Confirmations.CloseScope(confirmation)
            .ShowDialog<bool>(owner),
        message => Confirmations.OperationError(message).ShowDialog(owner),
        focus.RestoreAfterCancelledClose,
        focus.FocusCurrentRoute,
        owner.Close);
}
