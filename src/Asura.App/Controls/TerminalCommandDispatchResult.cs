using Asura.Core;

namespace Asura.App.Controls;

/// <summary>
/// The typed outcome of invoking a terminal-layer keybinding. Unsupported bindings are
/// consumed and reported instead of leaking their trigger into the remote terminal.
/// </summary>
public sealed record TerminalCommandDispatchResult(
    TerminalCommandDispatchResult.Outcome Status,
    CommandId? CommandId,
    string Message)
{
    public enum Outcome
    {
        NotMatched,
        Pending,
        PassedThrough,
        Executed,
        Unavailable,
        Unsupported,
        Rejected,
    }

    public bool ShouldHandle => Status != Outcome.NotMatched;

    internal static TerminalCommandDispatchResult NotMatched() => new(
        Outcome.NotMatched,
        CommandId: null,
        string.Empty);

    internal static TerminalCommandDispatchResult Pending() => new(
        Outcome.Pending,
        CommandId: null,
        "Waiting for the rest of the terminal shortcut.");

    internal static TerminalCommandDispatchResult PassedThrough() => new(
        Outcome.PassedThrough,
        CommandId: null,
        string.Empty);

    internal static TerminalCommandDispatchResult Executed(CommandId commandId) => new(
        Outcome.Executed,
        commandId,
        string.Empty);

    internal static TerminalCommandDispatchResult Unavailable(
        CommandId commandId,
        string message) => new(Outcome.Unavailable, commandId, message);

    internal static TerminalCommandDispatchResult Unsupported(
        CommandId commandId,
        string message) => new(Outcome.Unsupported, commandId, message);

    internal static TerminalCommandDispatchResult UnsupportedSequence(string message) => new(
        Outcome.Unsupported,
        CommandId: null,
        message);

    internal static TerminalCommandDispatchResult Rejected(bool shouldHandle) => shouldHandle
        ? new(
            Outcome.Rejected,
            CommandId: null,
            "The terminal shortcut sequence is not bound.")
        : NotMatched();
}
