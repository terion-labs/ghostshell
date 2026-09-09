using System.Text;
using Asura.App.Views;
using Asura.Application;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Asura.App;

/// <summary>
/// Shows network password and post-authentication persistence dialogs. Only the
/// runtime handles secret material and vault writes.
/// </summary>
public sealed class AvaloniaNetworkPasswordPrompt : INetworkPasswordPrompt
{
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    public ValueTask<bool> ConfirmPasswordUpdateAsync(
        NetworkPasswordPromptRequest request, CancellationToken cancellationToken) =>
        ShowConfirmationAsync(new ConfirmationDialogOptions
        {
            Title = "Update stored password",
            Heading = "The replacement password worked",
            Detail = $"Update the stored password for {request.ConnectionName}?",
            Notice = "If you decline, this connection keeps using the replacement until it disconnects. The stored password stays unchanged.",
            ConfirmLabel = "Update stored password",
            CancelLabel = "Keep for this connection",
        }, cancellationToken);

    public async ValueTask NotifyPasswordUpdateFailedAsync(
        NetworkPasswordPromptRequest request, CancellationToken cancellationToken)
    {
        _ = await ShowConfirmationAsync(new ConfirmationDialogOptions
        {
            Title = "Password not saved",
            Heading = "The stored password was not replaced",
            Detail = $"Asura could not update the password for {request.ConnectionName}. It may have been edited while the prompt was open, or the credential store may be unavailable.",
            Notice = "This does not disconnect your connection. You can replace the stored password in connection settings.",
            ConfirmLabel = "OK",
            ShowsCancel = false,
            Intent = ConfirmationIntent.Acknowledge,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ShowConfirmationAsync(
        ConfirmationDialogOptions options, CancellationToken cancellationToken)
    {
        await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InvokeOnUiThreadAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Avalonia.Application.Current?.ApplicationLifetime
                        is not IClassicDesktopStyleApplicationLifetime desktop
                    || ActiveWindow(desktop) is not { } owner)
                {
                    return false;
                }

                var dialog = new ConfirmationDialog(options);
                using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
                {
                    if (dialog.IsVisible)
                    {
                        dialog.Close(false);
                    }
                }));
                var confirmed = await dialog.ShowDialog<bool>(owner);
                cancellationToken.ThrowIfCancellationRequested();
                return confirmed;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _promptGate.Release();
        }
    }

    public async ValueTask<NetworkConnectionResult<SecretMaterial>> RequestPasswordAsync(
        NetworkPasswordPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            return await InvokeOnUiThreadAsync(
                    () => ShowPromptAsync(request, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NetworkConnectionResult<SecretMaterial>.Fail(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.AuthenticationRequired,
                    "network_password_prompt_failed",
                    "Asura could not open the password prompt.",
                    retryable: true));
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private static async Task<NetworkConnectionResult<SecretMaterial>> ShowPromptAsync(
        NetworkPasswordPromptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop
            || ActiveWindow(desktop) is not { } owner)
        {
            return NetworkConnectionResult<SecretMaterial>.Fail(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.AuthenticationRequired,
                    "network_password_window_unavailable",
                    $"{request.ConnectionName} needs a password, but no Asura window can show the prompt.",
                    retryable: true));
        }

        var dialog = new DatabasePasswordPromptDialog(request.ConnectionName,
            description: request.StoredPasswordRejected
                ? "The stored password was rejected. Enter a replacement to retry. After it works, you can choose whether to update the stored password."
                : null);
        using var cancellationRegistration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (dialog.IsVisible)
                {
                    dialog.Close(null);
                }
            }));
        var result = await dialog.ShowDialog<DatabasePasswordPromptResult?>(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null)
        {
            return Cancelled();
        }

        if (string.IsNullOrEmpty(result.Password))
        {
            return NetworkConnectionResult<SecretMaterial>.Fail(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.AuthenticationRequired,
                    "network_password_empty",
                    $"{request.ConnectionName} needs a non-empty password.",
                    retryable: true));
        }

        return NetworkConnectionResult<SecretMaterial>.Succeed(
            SecretMaterial.TakeOwnership(Encoding.UTF8.GetBytes(result.Password)));
    }

    private static Window? ActiveWindow(IClassicDesktopStyleApplicationLifetime desktop) =>
        desktop.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsActive)
        ?? desktop.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsVisible)
        ?? desktop.MainWindow;

    private static async Task<T> InvokeOnUiThreadAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await action();
        }

        var scheduled = await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
        return await scheduled.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NetworkConnectionResult<SecretMaterial> Cancelled() =>
        NetworkConnectionResult<SecretMaterial>.Fail(
            new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "network_password_prompt_cancelled",
                "The password prompt was cancelled.",
                retryable: true));
}
