namespace Asura.Agent;

public enum AgentSteerErrorCode
{
    NoActiveTurn,
    GenerationMismatch,
    NotInitialUserTurn,
    AlreadySteered,
    ProviderOperationLimit,
    LimitExceeded,
    ConversationConflict,
}

public sealed record AgentSteerResult
{
    private AgentSteerResult(
        bool succeeded,
        long? replacementGeneration,
        string? replacementUserMessage,
        AgentSteerErrorCode? errorCode)
    {
        Succeeded = succeeded;
        ReplacementGeneration = replacementGeneration;
        ReplacementUserMessage = replacementUserMessage;
        ErrorCode = errorCode;
    }

    public bool Succeeded { get; }

    public long? ReplacementGeneration { get; }

    public string? ReplacementUserMessage { get; }

    public AgentSteerErrorCode? ErrorCode { get; }

    public bool ContainsUntrustedContent => ReplacementUserMessage is not null;

    internal static AgentSteerResult Success(
        long replacementGeneration,
        string replacementUserMessage) =>
        new(true, replacementGeneration, replacementUserMessage, null);

    internal static AgentSteerResult Failure(AgentSteerErrorCode errorCode) =>
        new(false, null, null, errorCode);
}
