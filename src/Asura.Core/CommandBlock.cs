namespace Asura.Core;

public enum CommandActor
{
    User,
    Agent,
}

public enum CommandStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record CommandBlock(
    CommandBlockId Id,
    CommandActor Actor,
    string WorkingDirectory,
    string Command,
    string Output,
    CommandStatus Status,
    TimeSpan Elapsed);

