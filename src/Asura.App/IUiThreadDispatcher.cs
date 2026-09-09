using Avalonia.Threading;

namespace Asura.App;

public interface IUiThreadDispatcher
{
    bool RequiresFramePacing => false;

    /// <summary>
    /// Fails when presentation-owned state is being touched away from Avalonia's
    /// UI thread. Headless consumers have no Avalonia application, so there is
    /// no presentation thread to verify in that case.
    /// </summary>
    void VerifyAccess()
    {
        if (Avalonia.Application.Current is not null)
        {
            Dispatcher.UIThread.VerifyAccess();
        }
    }

    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

internal sealed class AvaloniaUiThreadDispatcher : IUiThreadDispatcher
{
    public static AvaloniaUiThreadDispatcher Instance { get; } = new();

    private AvaloniaUiThreadDispatcher()
    {
    }

    public bool RequiresFramePacing => true;

    public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (Avalonia.Application.Current is null
            || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
    }
}
