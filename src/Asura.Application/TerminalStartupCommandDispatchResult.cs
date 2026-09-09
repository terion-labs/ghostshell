namespace Asura.Application;

public sealed record TerminalStartupCommandDispatchResult(
    bool CommandsDelivered,
    TerminalStartupCommandDispatchError? Error)
{
    public bool Succeeded => CommandsDelivered && Error is null;

    public static TerminalStartupCommandDispatchResult Success() => new(true, null);

    public static TerminalStartupCommandDispatchResult Failure(
        TerminalStartupCommandDispatchError error,
        bool commandsDelivered = false) =>
        new(commandsDelivered, error ?? throw new ArgumentNullException(nameof(error)));
}
