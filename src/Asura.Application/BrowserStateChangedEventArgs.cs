namespace Asura.Application;

public sealed class BrowserStateChangedEventArgs(
    BrowserSessionState state) : EventArgs
{
    public BrowserSessionState State { get; } =
        state ?? throw new ArgumentNullException(nameof(state));
}
