using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Agent;

public enum AgentMessageRole
{
    System,
    User,
    Assistant,
    Tool,
    Summary,
}

public sealed record AgentMessage
{
    public AgentMessage(AgentMessageRole role, string content)
        : this(
            role,
            content,
            [],
            toolResult: null,
            reasoningSummary: null,
            usage: null,
            images: [],
            providerReplayState: null,
            requestedReasoningEffort: null)
    {
    }

    public AgentMessage(
        AgentMessageRole role,
        string content,
        ImmutableArray<AgentImageAttachment> images)
        : this(
            role,
            content,
            [],
            toolResult: null,
            reasoningSummary: null,
            usage: null,
            images,
            providerReplayState: null,
            requestedReasoningEffort: null)
    {
    }

    private AgentMessage(
        AgentMessageRole role,
        string content,
        ImmutableArray<AgentToolProposal> toolCalls,
        AgentToolResult? toolResult,
        string? reasoningSummary,
        AgentTokenUsage? usage,
        ImmutableArray<AgentImageAttachment> images,
        AgentProviderReplayState? providerReplayState,
        AgentReasoningEffort? requestedReasoningEffort)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ArgumentNullException.ThrowIfNull(content);
        if (toolCalls.IsDefault)
        {
            throw new ArgumentException(
                "The tool-call collection is required.",
                nameof(toolCalls));
        }

        if (images.IsDefault || images.Any(image => image is null))
        {
            throw new ArgumentException(
                "The image collection is required.",
                nameof(images));
        }

        var hasToolCalls = toolCalls.Length > 0;
        var hasToolResult = toolResult is not null;
        var hasReasoningSummary = reasoningSummary is not null;
        var hasUsage = usage is not null;
        var hasRequestedReasoning = requestedReasoningEffort is not null;
        if ((role != AgentMessageRole.Assistant && hasToolCalls)
            || (hasToolResult && role != AgentMessageRole.Tool)
            || (hasToolCalls && hasToolResult)
            || (role != AgentMessageRole.Assistant
                && (hasReasoningSummary || hasUsage))
            || (role != AgentMessageRole.Assistant
                && providerReplayState is not null)
            || (role != AgentMessageRole.Assistant && hasRequestedReasoning)
            || (role != AgentMessageRole.User && images.Length > 0)
            || reasoningSummary is { Length: 0 })
        {
            throw new ArgumentException("The structured message shape is invalid.");
        }

        if (requestedReasoningEffort is { } effort && !Enum.IsDefined(effort))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedReasoningEffort));
        }

        Role = role;
        Content = content;
        ToolCalls = toolCalls;
        ToolResult = toolResult;
        ReasoningSummary = reasoningSummary;
        Usage = usage;
        Images = images;
        ProviderReplayState = providerReplayState;
        RequestedReasoningEffort = requestedReasoningEffort;
    }

    public AgentMessageRole Role { get; }

    public string Content { get; }

    public ImmutableArray<AgentToolProposal> ToolCalls { get; }

    public AgentToolResult? ToolResult { get; }

    /// <summary>
    /// Optional provider-authored reasoning summary. This is bounded model
    /// output, not hidden chain-of-thought and not trusted authority.
    /// </summary>
    public string? ReasoningSummary { get; }

    public AgentTokenUsage? Usage { get; }

    public ImmutableArray<AgentImageAttachment> Images { get; }

    /// <summary>
    /// The provider-neutral effort requested for this assistant generation.
    /// This records what Asura sent; token usage records what the provider
    /// reports it actually used.
    /// </summary>
    public AgentReasoningEffort? RequestedReasoningEffort { get; }

    internal AgentProviderReplayState? ProviderReplayState { get; }

    internal static AgentMessage Assistant(
        string content,
        ImmutableArray<AgentToolProposal> toolCalls,
        string? reasoningSummary = null,
        AgentTokenUsage? usage = null,
        AgentProviderReplayState? providerReplayState = null,
        AgentReasoningEffort? requestedReasoningEffort = null) =>
        new(
            AgentMessageRole.Assistant,
            content,
            toolCalls,
            toolResult: null,
            reasoningSummary,
            usage,
            images: [],
            providerReplayState,
            requestedReasoningEffort);

    internal static AgentMessage FromToolResult(AgentToolResult result) =>
        new(
            AgentMessageRole.Tool,
            result?.Value.Content ?? throw new ArgumentNullException(nameof(result)),
            [],
            result,
            reasoningSummary: null,
            usage: null,
            images: [],
            providerReplayState: null,
            requestedReasoningEffort: null);

    internal AgentMessage WithoutUsage() =>
        Usage is null
            ? this
            : new AgentMessage(
                Role,
                Content,
                ToolCalls,
                ToolResult,
                ReasoningSummary,
                usage: null,
                Images,
                ProviderReplayState,
                RequestedReasoningEffort);

    internal AgentMessage WithoutProviderReplayState() =>
        ProviderReplayState is null
            ? this
            : new AgentMessage(
                Role,
                Content,
                ToolCalls,
                ToolResult,
                ReasoningSummary,
                Usage,
                Images,
                providerReplayState: null,
                RequestedReasoningEffort);
}

public sealed record AgentToolDefinition
{
    public const int MaximumNameLength = 64;
    private const string ProviderAliasPrefix = "tool_";
    private const int MaximumDescriptionLength = 4 * 1024;
    private const int MaximumSchemaBytes = 1024 * 1024;
    private const int MaximumSchemaDepth = 128;
    private const int MaximumSchemaNodes = 64 * 1024;

    public AgentToolDefinition(
        string name,
        string description,
        ReadOnlyMemory<byte> utf8InputSchema)
    {
        ValidateIdentifier(name, nameof(name), MaximumNameLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > MaximumDescriptionLength
            || description.Any(char.IsControl)
            || !IsWellFormedUtf16(description))
        {
            throw new ArgumentException("The tool description is invalid.", nameof(description));
        }

        if (utf8InputSchema.IsEmpty || utf8InputSchema.Length > MaximumSchemaBytes)
        {
            throw new ArgumentException(
                "The tool input schema exceeds its byte limit.",
                nameof(utf8InputSchema));
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8InputSchema,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumSchemaDepth,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "The tool input schema must be a JSON object.",
                    nameof(utf8InputSchema));
            }

            var remainingNodes = MaximumSchemaNodes;
            ValidateSchema(document.RootElement, ref remainingNodes, nameof(utf8InputSchema));
            if (!document.RootElement.TryGetProperty("type", out var inputType)
                || inputType.ValueKind != JsonValueKind.String
                || !string.Equals(inputType.GetString(), "object", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The tool input schema must declare an object input.",
                    nameof(utf8InputSchema));
            }

            InputSchema = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The tool input schema is not valid bounded JSON.",
                nameof(utf8InputSchema),
                exception);
        }

        Name = name;
        ProviderName = GetProviderName(name);
        Description = description;
    }

    public string Name { get; }

    /// <summary>
    /// The provider-facing alias for <see cref="Name"/>. Internal operation
    /// names remain stable while this value satisfies provider tool-name
    /// contracts.
    /// </summary>
    public string ProviderName { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }

    private static void ValidateSchema(
        JsonElement element,
        ref int remainingNodes,
        string parameterName)
    {
        if (--remainingNodes < 0)
        {
            throw new ArgumentException(
                "The tool input schema exceeds its node limit.",
                parameterName);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException(
                        "The tool input schema contains a duplicate property.",
                        parameterName);
                }

                ValidateSchema(property.Value, ref remainingNodes, parameterName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateSchema(item, ref remainingNodes, parameterName);
            }
        }
    }

    internal static void ValidateIdentifier(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (!IsValidIdentifier(value, maximumLength))
        {
            throw new ArgumentException("The identifier is invalid.", parameterName);
        }
    }

    internal static bool IsValidIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(character =>
            char.IsControl(character) || char.IsWhiteSpace(character))
        && IsWellFormedUtf16(value);

    internal static string GetProviderName(string name)
    {
        ValidateIdentifier(name, nameof(name), MaximumNameLength);
        if (IsValidProviderName(name))
        {
            return name;
        }

        // Invalid provider syntax is represented opaquely instead of being
        // rewritten character-by-character, which could collapse distinct
        // internal names. The retained SHA-256 prefix supplies 236 hash bits.
        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))
            .ToLowerInvariant();
        var providerName = string.Concat(
            ProviderAliasPrefix,
            hash.AsSpan(0, MaximumNameLength - ProviderAliasPrefix.Length));
        ValidateProviderName(providerName, nameof(name));
        return providerName;
    }

    internal static void ValidateProviderName(string value, string parameterName)
    {
        if (!IsValidProviderName(value))
        {
            throw new ArgumentException(
                "The provider tool name is invalid.",
                parameterName);
        }
    }

    internal static bool IsValidProviderName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumNameLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isAsciiLetter = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (!isAsciiLetter && !isDigit && character is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 == value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record AgentProviderRequest
{
    internal AgentProviderRequest(
        AgentRunId runId,
        long generation,
        ImmutableArray<AgentMessage> messages,
        ImmutableArray<AgentToolDefinition> tools,
        AgentReasoningEffort reasoningEffort = AgentReasoningEffort.Automatic)
    {
        if (runId == default)
        {
            throw new ArgumentException("The agent run ID is required.", nameof(runId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (messages.IsDefault)
        {
            throw new ArgumentException("The message collection is required.", nameof(messages));
        }

        if (tools.IsDefault)
        {
            throw new ArgumentException("The tool collection is required.", nameof(tools));
        }

        if (!Enum.IsDefined(reasoningEffort))
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningEffort));
        }

        RunId = runId;
        Generation = generation;
        Messages = messages;
        Tools = tools;
        ReasoningEffort = reasoningEffort;
    }

    public AgentRunId RunId { get; }

    public long Generation { get; }

    public ImmutableArray<AgentMessage> Messages { get; }

    public ImmutableArray<AgentToolDefinition> Tools { get; }

    public AgentReasoningEffort ReasoningEffort { get; }
}

/// <summary>
/// One request-scoped provider adapter. A native steering replacement can
/// start before a superseded stream observes cancellation, so implementations
/// must keep per-stream state local and support at most two concurrent
/// <see cref="StreamAsync"/> enumerations on the same instance. Cancellation
/// of either enumeration must not corrupt or cancel the other.
/// </summary>
public interface IAgentProvider
{
    IAsyncEnumerable<AgentProviderEvent> StreamAsync(
        AgentProviderRequest request,
        CancellationToken cancellationToken);
}

public enum AgentProviderStopReason
{
    EndTurn,
    ToolUse,
    MaximumTokens,
    ContentFiltered,
}

public abstract record AgentProviderEvent
{
    private AgentProviderEvent()
    {
    }

    public sealed record ResponseStarted : AgentProviderEvent;

    public sealed record TextDelta(string Value) : AgentProviderEvent;

    /// <summary>
    /// A provider-authored summary of its reasoning. Adapters must not map
    /// hidden chain-of-thought or opaque reasoning payloads into this event.
    /// </summary>
    public sealed record ReasoningSummaryDelta(string Value) : AgentProviderEvent;

    /// <summary>
    /// Begins a provider tool call. <paramref name="Name"/> must equal the
    /// advertised <see cref="AgentToolDefinition.ProviderName"/>.
    /// </summary>
    public sealed record ToolCallStarted(
        int Index,
        string ProviderCallId,
        string Name) : AgentProviderEvent;

    public sealed record ToolCallArgumentsDelta(
        int Index,
        string Value) : AgentProviderEvent;

    public sealed record ToolCallCompleted(int Index) : AgentProviderEvent;

    public sealed record Usage(AgentTokenUsage Value) : AgentProviderEvent;

    /// <summary>
    /// Final provider-private continuity state. The kernel reduces this only as
    /// part of a successfully completed response and never projects it.
    /// </summary>
    internal sealed record ReplayStateFinalized(
        AgentProviderReplayState Value) : AgentProviderEvent;

    public sealed record ResponseCompleted(
        AgentProviderStopReason StopReason) : AgentProviderEvent;
}
