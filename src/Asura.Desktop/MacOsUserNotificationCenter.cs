using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Asura.Application;

namespace Asura.Desktop;

/// <summary>
/// Narrow Objective-C bridge for UserNotifications. It owns the native delegate
/// for its full lifetime because <c>UNUserNotificationCenter.delegate</c> is a
/// weak reference.
/// </summary>
[SupportedOSPlatform("macos10.14")]
internal sealed partial class MacOsUserNotificationCenter : IDisposable
{
    private const nuint AuthorizationOptions = 2 | 4; // sound | alert

    private nint _center;
    private nint _delegate;
    private bool _disposed;

    public MacOsUserNotificationCenter()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(10, 14))
        {
            throw new PlatformNotSupportedException(
                "UNUserNotificationCenter requires macOS 10.14 or newer.");
        }

        // Loading UserNotifications in an unbundled command-line host can
        // destabilize the process even if currentNotificationCenter is never
        // requested. Reject unsupported hosts before loading the framework.
        RunWithAutoreleasePool(RequireApplicationBundle);
        EnsureFrameworkLoaded();
        RunWithAutoreleasePool(() =>
        {
            _center = SendObject(
                RequireClass("UNUserNotificationCenter"),
                Selector("currentNotificationCenter"));
            _delegate = SendObject(GetDelegateClass(), Selector("new"));
            if (_center == 0 || _delegate == 0)
            {
                Dispose();
                throw new InvalidOperationException(
                    "Could not initialize the macOS notification center.");
            }

            Centers[_delegate] = new WeakReference<MacOsUserNotificationCenter>(this);
            SendVoid(_center, Selector("setDelegate:"), _delegate);
        });
    }

    public event EventHandler<NativeNotificationActivatedEventArgs>? Activated;

    public async ValueTask<bool> RequestAuthorizationAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<AuthorizationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var block = MacOsObjectiveCBlock.CreateAuthorization((granted, error) =>
        {
            try
            {
                completion.TrySetResult(RunWithAutoreleasePool(
                    () => new AuthorizationResult(
                        granted,
                        DescribeError(error))));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        RunWithAutoreleasePool(() =>
            SendVoid(
                _center,
                Selector("requestAuthorizationWithOptions:completionHandler:"),
                AuthorizationOptions,
                block.Pointer));

        var result = await completion.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw new InvalidOperationException(
                $"macOS could not authorize notifications: {error}");
        }

        return result.Granted;
    }

    public async ValueTask AddAsync(
        NativeNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var request = RunWithAutoreleasePool(
            () => CreateRequest(notification));
        try
        {
            var completion = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var block = MacOsObjectiveCBlock.CreateError(error =>
            {
                try
                {
                    completion.TrySetResult(RunWithAutoreleasePool(
                        () => DescribeError(error)));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            RunWithAutoreleasePool(() =>
                SendVoid(
                    _center,
                    Selector("addNotificationRequest:withCompletionHandler:"),
                    request,
                    block.Pointer));

            var error = await completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    $"macOS could not schedule the notification: {error}");
            }
        }
        finally
        {
            RunWithAutoreleasePool(() => Release(request));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var nativeDelegate = Interlocked.Exchange(ref _delegate, 0);
        var center = Interlocked.Exchange(ref _center, 0);
        if (nativeDelegate == 0)
        {
            return;
        }

        Centers.TryRemove(nativeDelegate, out _);
        RunWithAutoreleasePool(() =>
        {
            if (center != 0
                && SendObject(center, Selector("delegate")) == nativeDelegate)
            {
                SendVoid(center, Selector("setDelegate:"), 0);
            }

            Release(nativeDelegate);
        });
    }

    private readonly record struct AuthorizationResult(bool Granted, string? Error);
}
