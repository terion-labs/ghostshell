using Asura.Core;

namespace Asura.Agent;

public enum AgentCheckpointCaptureErrorCode
{
    SessionNotIdle,
    LimitExceeded,
    UnsafeContent,
}

public sealed class AgentCheckpointCaptureResult
{
    private AgentCheckpointCaptureResult(
        AgentSessionCheckpoint? checkpoint,
        AgentCheckpointCaptureErrorCode? errorCode)
    {
        Checkpoint = checkpoint;
        ErrorCode = errorCode;
    }

    public bool Succeeded => ErrorCode is null;

    public AgentSessionCheckpoint? Checkpoint { get; }

    public AgentCheckpointCaptureErrorCode? ErrorCode { get; }

    internal static AgentCheckpointCaptureResult Success(
        AgentSessionCheckpoint checkpoint) =>
        new(checkpoint, null);

    internal static AgentCheckpointCaptureResult Failure(
        AgentCheckpointCaptureErrorCode errorCode) =>
        new(null, errorCode);
}

public enum AgentCheckpointRestoreErrorCode
{
    UnsupportedSchema,
    InvalidPayload,
    LimitExceeded,
    UnsafeContent,
}

public sealed class AgentCheckpointRestoreResult
{
    private AgentCheckpointRestoreResult(
        NativeAgentSession? session,
        AgentCheckpointRestoreErrorCode? errorCode)
    {
        Session = session;
        ErrorCode = errorCode;
    }

    public bool Succeeded => ErrorCode is null;

    public NativeAgentSession? Session { get; }

    public AgentCheckpointRestoreErrorCode? ErrorCode { get; }

    internal static AgentCheckpointRestoreResult Success(
        NativeAgentSession session) =>
        new(session, null);

    internal static AgentCheckpointRestoreResult Failure(
        AgentCheckpointRestoreErrorCode errorCode) =>
        new(null, errorCode);
}

/// <summary>
/// Presentation-safe identity recovered from a durable conversation. Provider
/// replay payloads remain private; only the binding needed to reopen the same
/// conversation is exposed.
/// </summary>
public sealed record AgentConversationDescriptor(
    AgentRunId RunId,
    string Title,
    AiProviderProfileId? ProviderId,
    string? Model,
    int MessageCount);
