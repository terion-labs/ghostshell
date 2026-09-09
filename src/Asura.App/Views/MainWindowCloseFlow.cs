using Asura.Application;

namespace Asura.App.Views;

internal static class MainWindowCloseFlow
{
    public static async Task<bool> RunAsync(
        Func<CloseDecision, CancellationToken, ValueTask<HostResult<CloseScopeResult>>> close,
        Func<CloseScopeResult.ConfirmationRequired, Task<bool>> confirm,
        Func<string, Task> showError,
        Action restoreFocus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(showError);
        ArgumentNullException.ThrowIfNull(restoreFocus);

        try
        {
            var initial = await close(CloseDecision.Request, cancellationToken);
            if (initial is HostResult<CloseScopeResult>.Failure initialFailure)
            {
                await showError(initialFailure.Error.Message);
                return false;
            }

            var result = ((HostResult<CloseScopeResult>.Success)initial).Value;
            if (result is CloseScopeResult.ConfirmationRequired confirmation)
            {
                if (!await confirm(confirmation))
                {
                    // Return keyboard ownership before the cancellation round trip.
                    // The host may still need to snapshot live sessions.
                    restoreFocus();
                    _ = await close(CloseDecision.Cancel, cancellationToken);
                    return false;
                }

                var confirmed = await close(CloseDecision.Confirm, cancellationToken);
                if (confirmed is HostResult<CloseScopeResult>.Failure confirmationFailure)
                {
                    await showError(confirmationFailure.Error.Message);
                    return false;
                }

                result = ((HostResult<CloseScopeResult>.Success)confirmed).Value;
            }

            if (result is not CloseScopeResult.Completed completed)
            {
                await showError("The session still requires confirmation.");
                return false;
            }

            var failure = completed.Sessions.FirstOrDefault(item =>
                item.Outcome is SessionCloseOutcome.EngineFailed
                    or SessionCloseOutcome.ConfirmationRequired);
            if (failure is not null)
            {
                await showError(failure.Detail);
                return false;
            }

            return completed.Sessions.All(item =>
                item.Outcome != SessionCloseOutcome.Cancelled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
