using Asura.Application;

namespace Asura.App;

public sealed class TerminalStartupCommandDispatchEventArgs(
    OperationContext context,
    TerminalStartupCommandDispatchResult result) : EventArgs
{
    public OperationContext Context { get; } =
        context ?? throw new ArgumentNullException(nameof(context));

    public TerminalStartupCommandDispatchResult Result { get; } =
        result ?? throw new ArgumentNullException(nameof(result));
}
