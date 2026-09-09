using Asura.Application;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Asura.Desktop;

/// <summary>
/// Runs the database-dependent part of startup after the unlock, when the
/// encryption keys arrived sealed under the PIN. The window opens locked;
/// the first successful unlock hands the keys to the encryption runtime and
/// this coordinator then does what startup would have done — once, off the
/// UI thread. A failure at that point is exactly as fatal as it would have
/// been before the window existed, and is reported the same way before the
/// application closes.
/// </summary>
internal static class DeferredStartupCoordinator
{
    public static void Arm(
        IStartupProtection protection,
        Func<Task<string?>> initializeProfile,
        Action initializeBrowserRuntime)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(initializeProfile);
        ArgumentNullException.ThrowIfNull(initializeBrowserRuntime);
        var armed = 0;
        protection.Changed += OnChanged;

        async void OnChanged(object? sender, EventArgs e)
        {
            if (protection.IsLocked
                || Interlocked.Exchange(ref armed, 1) != 0)
            {
                return;
            }

            protection.Changed -= OnChanged;
            string? error;
            try
            {
                error = await Task.Run(initializeProfile);
            }
            catch (Exception exception)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "desktop.deferred-startup.failed",
                    exception);
                error = "Deferred startup failed unexpectedly.";
            }

            if (error is null)
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(initializeBrowserRuntime);
                    return;
                }
                catch (Exception exception)
                {
                    SecretSafeDiagnosticProjection.WriteStandardError(
                        "desktop.deferred-browser-initialize.failed",
                        exception);
                    error = "The embedded browser could not start after unlock.";
                }
            }

            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.deferred-profile-open.failed",
                SecretSafeDiagnosticKind.Unexpected);
            Environment.ExitCode = 1;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown(1);
                }
            });
        }
    }
}
