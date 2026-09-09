using Avalonia.Threading;

namespace Asura.Browser;

internal interface IBrowserUiDispatcher
{
    bool CheckAccess();

    ValueTask<T> InvokeAsync<T>(Func<T> operation);

    void Post(Action operation);
}

internal sealed class AvaloniaBrowserUiDispatcher : IBrowserUiDispatcher
{
    public static AvaloniaBrowserUiDispatcher Instance { get; } = new();

    private AvaloniaBrowserUiDispatcher()
    {
    }

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public async ValueTask<T> InvokeAsync<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await Dispatcher.UIThread.InvokeAsync(operation);
    }

    public void Post(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Dispatcher.UIThread.Post(operation);
    }
}
