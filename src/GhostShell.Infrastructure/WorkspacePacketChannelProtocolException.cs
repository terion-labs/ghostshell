namespace GhostShell.Infrastructure;

public sealed class WorkspacePacketChannelProtocolException : Exception
{
    public WorkspacePacketChannelProtocolException(
        WorkspacePacketChannelFailure failure,
        string message)
        : base(message)
    {
        Failure = failure;
    }

    public WorkspacePacketChannelProtocolException(
        WorkspacePacketChannelFailure failure,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public WorkspacePacketChannelFailure Failure { get; }
}
