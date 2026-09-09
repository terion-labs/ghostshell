namespace Asura.Browser.Tests;

internal sealed class InlineBrowserUiDispatcher : IBrowserUiDispatcher
{
    public static InlineBrowserUiDispatcher Instance { get; } = new();

    private InlineBrowserUiDispatcher()
    {
    }

    public bool CheckAccess() => true;

    public ValueTask<T> InvokeAsync<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ValueTask.FromResult(operation());
    }

    public void Post(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation();
    }
}
