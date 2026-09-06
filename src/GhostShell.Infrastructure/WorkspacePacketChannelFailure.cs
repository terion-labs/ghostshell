namespace GhostShell.Infrastructure;

public enum WorkspacePacketChannelFailure
{
    UnexpectedEndOfStream,
    InvalidFrame,
    UnsupportedVersion,
    UnexpectedMessage,
    InvalidSequence,
    FrameTooLarge,
    AuthenticationFailed,
    MalformedPayload,
    ChannelFaulted,
}
