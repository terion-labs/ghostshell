using Asura.Application;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Asura.Desktop;

internal sealed class DesktopUpdateShutdown
{
    private readonly object _gate = new();
    private readonly Action<Func<Task>> _schedule;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private Func<CancellationToken, Task>? _quiesce;
    private bool _requestActive;

    public DesktopUpdateShutdown()
        : this(work => Dispatcher.UIThread.Post(() => _ = work()))
    {
    }

    internal DesktopUpdateShutdown(Action<Func<Task>> schedule)
    {
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    }

    public void Attach(
        IClassicDesktopStyleApplicationLifetime lifetime,
        Func<CancellationToken, Task> quiesce)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(quiesce);
        lock (_gate)
        {
            _lifetime = lifetime;
            _quiesce = quiesce;
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            _lifetime = null;
            _quiesce = null;
        }
    }

    public void Request()
    {
        IClassicDesktopStyleApplicationLifetime lifetime;
        Func<CancellationToken, Task> quiesce;
        lock (_gate)
        {
            lifetime = _lifetime
                ?? throw new InvalidOperationException(
                    "The desktop lifetime is not ready for an update restart.");
            quiesce = _quiesce
                ?? throw new InvalidOperationException(
                    "The desktop shutdown preflight is not ready for an update restart.");
            if (_requestActive)
            {
                return;
            }

            _requestActive = true;
        }

        try
        {
            _schedule(() => QuiesceAndShutdownAsync(lifetime, quiesce));
        }
        catch
        {
            lock (_gate)
            {
                _requestActive = false;
            }

            throw;
        }
    }

    private async Task QuiesceAndShutdownAsync(
        IClassicDesktopStyleApplicationLifetime lifetime,
        Func<CancellationToken, Task> quiesce)
    {
        try
        {
            await quiesce(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.update-restart-quiesce.failed",
                exception);
        }

        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_lifetime, lifetime))
                {
                    return;
                }
            }

            lifetime.Shutdown();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.update-restart-shutdown.failed",
                exception);
        }
        finally
        {
            lock (_gate)
            {
                _requestActive = false;
            }
        }
    }
}
