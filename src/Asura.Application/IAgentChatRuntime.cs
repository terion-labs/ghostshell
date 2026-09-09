using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Presentation-only chat seam for the native agent kernel. This surface deliberately exposes no
/// terminal context, tool definitions, approval decisions, or execution capability.
/// </summary>
public interface IAgentChatRuntime : IDisposable
{
    event EventHandler? Changed;

    AgentChatSnapshot Snapshot { get; }

    ValueTask<AgentChatSendResult> SendAsync(
        AiProviderProfileId providerId,
        string prompt,
        CancellationToken cancellationToken);

    bool Cancel();

    bool Clear();
}

public enum AgentChatState
{
    Ready,
    Streaming,
    Cancelling,
    Failed,
    Cancelled,
}

public enum AgentChatMessageRole
{
    User,
    Assistant,
}

public sealed record AgentChatUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    long TotalTokens);

public sealed record AgentChatImage(
    string FileName,
    string MediaType,
    int ByteLength);

public sealed record AgentChatMessage(
    AgentChatMessageRole Role,
    string Content,
    string? ReasoningSummary = null,
    AgentChatUsage? Usage = null,
    IReadOnlyList<AgentChatImage>? Images = null,
    AgentReasoningEffort? RequestedReasoningEffort = null,
    AgentConversationForkPoint? ForkPoint = null);

public sealed record AgentChatSnapshot(
    AgentChatState State,
    AiProviderProfileId? ProviderId,
    IReadOnlyList<AgentChatMessage> Messages,
    string ProvisionalAssistantText,
    string Status)
{
    // A cancelling turn still owns the provider request and must remain busy until it drains.
    public bool IsStreaming =>
        State is AgentChatState.Streaming or AgentChatState.Cancelling;

    public bool CanSend => !IsStreaming;

    public bool CanCancel => State == AgentChatState.Streaming;

    public bool HasMessages => Messages.Count > 0 || ProvisionalAssistantText.Length > 0;
}

public sealed record AgentChatSendResult(
    bool IsSuccess,
    string Code,
    string Message);
