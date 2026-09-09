using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

internal abstract class HostAccessibilityPreferencesSource :
    IHostAccessibilityPreferencesSource
{
    private readonly object _gate = new();
    private HostAccessibilityPreferences _current = HostAccessibilityPreferences.Default;
    private bool _started;
    private bool _disposed;

    public HostAccessibilityPreferences Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        StartCore();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Changed = null;
        }

        DisposeCore();
    }

    protected bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    protected void Publish(HostAccessibilityPreferences next)
    {
        ArgumentNullException.ThrowIfNull(next);

        EventHandler? changed;
        lock (_gate)
        {
            if (_disposed || _current == next)
            {
                return;
            }

            _current = next;
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    protected abstract void StartCore();

    protected virtual void DisposeCore()
    {
    }
}
