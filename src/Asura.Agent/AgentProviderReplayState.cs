using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Agent;

internal enum AgentProviderReplayFormat
{
    AnthropicContentBlocks,
    OpenAiResponseItems,
}

internal enum AgentProviderReplayItemKind
{
    AnthropicSummarizedThinking,
    AnthropicSuppressedThinking,
    AnthropicRedactedThinking,
    AnthropicText,
    AnthropicToolUse,
    OpenAiReasoning,
    OpenAiReasoningWithSuppressedRaw,
    OpenAiMessage,
    OpenAiFunctionCall,
}

internal sealed record AgentProviderReplayBinding
{
    internal const int MaximumRouteIdentityLength = 128;

    public AgentProviderReplayBinding(
        AiProviderProfileId profileId,
        AiProviderKind providerKind,
        AiProviderProtocol protocol,
        string model,
        Uri endpoint,
        string routeIdentity)
    {
        if (profileId == default
            || !Enum.IsDefined(providerKind)
            || !Enum.IsDefined(protocol))
        {
            throw new ArgumentException("The provider replay binding is invalid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > AiProviderProfile.MaximumModelIdLength
            || model.Any(char.IsControl))
        {
            throw new ArgumentException("The replay model binding is invalid.", nameof(model));
        }

        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("http" or "https")
            || endpoint.AbsoluteUri.Length > AiProviderProfile.MaximumEndpointLength
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("The replay endpoint binding is invalid.", nameof(endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        if (routeIdentity.Length > MaximumRouteIdentityLength
            || routeIdentity.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException(
                "The provider replay route identity is invalid.",
                nameof(routeIdentity));
        }

        ProfileId = profileId;
        ProviderKind = providerKind;
        Protocol = protocol;
        Model = model;
        Endpoint = new Uri(endpoint.AbsoluteUri, UriKind.Absolute);
        RouteIdentity = routeIdentity;
    }

    public AiProviderProfileId ProfileId { get; }

    public AiProviderKind ProviderKind { get; }

    public AiProviderProtocol Protocol { get; }

    public string Model { get; }

    public Uri Endpoint { get; }

    public string RouteIdentity { get; }
}

internal sealed record AgentProviderReplayItem
{
    public AgentProviderReplayItem(
        int index,
        AgentProviderReplayItemKind kind,
        string payloadJson,
        int? toolIndex = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(payloadJson);
        Index = index;
        Kind = kind;
        PayloadJson = AgentProviderReplayState.ValidateItemPayload(payloadJson);
        ToolIndex = toolIndex;
    }

    public int Index { get; }

    public AgentProviderReplayItemKind Kind { get; }

    public string PayloadJson { get; }

    public int? ToolIndex { get; }
}

/// <summary>
/// Provider-private continuity artifacts for one committed assistant message.
/// This state is never projected to run events, UI, logs, or audit records.
/// </summary>
internal sealed record AgentProviderReplayState
{
    internal const int MaximumItems = 128;
    internal const int MaximumItemBytes = 256 * 1024;
    internal const int MaximumTotalBytes = 1024 * 1024;
    internal const int MaximumJsonDepth = 32;
    internal const int MaximumJsonNodes = 4 * 1024;

    public AgentProviderReplayState(
        AgentProviderReplayBinding binding,
        AgentProviderReplayFormat format,
        ImmutableArray<AgentProviderReplayItem> items)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (items.IsDefault || items.Length is 0 or > MaximumItems)
        {
            throw new ArgumentException("The provider replay items are invalid.", nameof(items));
        }

        var protocolMatchesFormat = format switch
        {
            AgentProviderReplayFormat.AnthropicContentBlocks =>
                binding.Protocol == AiProviderProtocol.AnthropicMessages,
            AgentProviderReplayFormat.OpenAiResponseItems =>
                binding.Protocol is AiProviderProtocol.OpenAiResponses
                    or AiProviderProtocol.GitHubCopilot,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        if (!protocolMatchesFormat)
        {
            throw new ArgumentException(
                "The replay format does not match its provider protocol.",
                nameof(format));
        }

        long totalBytes = 0;
        var toolIndices = new HashSet<int>();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index]
                ?? throw new ArgumentException(
                    "Provider replay items cannot contain null values.",
                    nameof(items));
            if (item.Index != index || !BelongsTo(format, item.Kind))
            {
                throw new ArgumentException(
                    "Provider replay items must be contiguous and format-specific.",
                    nameof(items));
            }

            var isTool = item.Kind is AgentProviderReplayItemKind.AnthropicToolUse
                or AgentProviderReplayItemKind.OpenAiFunctionCall;
            if (isTool != item.ToolIndex.HasValue
                || (item.ToolIndex is { } toolIndex
                    && (toolIndex < 0 || !toolIndices.Add(toolIndex))))
            {
                throw new ArgumentException(
                    "Provider replay tool slots are invalid or duplicated.",
                    nameof(items));
            }

            totalBytes = checked(
                totalBytes + Encoding.UTF8.GetByteCount(item.PayloadJson));
            if (totalBytes > MaximumTotalBytes)
            {
                throw new ArgumentException(
                    "The provider replay state exceeds its byte limit.",
                    nameof(items));
            }
        }

        if (toolIndices.Count > 0
            && !toolIndices.SetEquals(Enumerable.Range(0, toolIndices.Count)))
        {
            throw new ArgumentException(
                "Provider replay tool slots must be zero-based and contiguous.",
                nameof(items));
        }

        Format = format;
        Items = items;
        ContainsSuppressedRawReasoning = items.Any(item => item.Kind is
            AgentProviderReplayItemKind.AnthropicSuppressedThinking
            or AgentProviderReplayItemKind.OpenAiReasoningWithSuppressedRaw);
    }

    public AgentProviderReplayBinding Binding { get; }

    public AgentProviderReplayFormat Format { get; }

    public ImmutableArray<AgentProviderReplayItem> Items { get; }

    public bool ContainsSuppressedRawReasoning { get; }

    public bool Matches(AgentProviderReplayBinding binding) =>
        Binding == binding;

    /// <summary>
    /// Opaque replay artifacts are model-specific, but visible transcript data
    /// remains portable between models on the same authenticated provider route.
    /// </summary>
    public bool MatchesRoute(AgentProviderReplayBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return Binding.ProfileId == binding.ProfileId
            && Binding.ProviderKind == binding.ProviderKind
            && Binding.Protocol == binding.Protocol
            && Binding.Endpoint == binding.Endpoint
            && string.Equals(
                Binding.RouteIdentity,
                binding.RouteIdentity,
                StringComparison.Ordinal);
    }

    internal bool MatchesMessage(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != AgentMessageRole.Assistant)
        {
            return false;
        }

        try
        {
            return Format switch
            {
                AgentProviderReplayFormat.AnthropicContentBlocks =>
                    MatchesAnthropicMessage(message),
                AgentProviderReplayFormat.OpenAiResponseItems =>
                    MatchesOpenAiMessage(message),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string ValidateItemPayload(string payloadJson)
    {
        if (Encoding.UTF8.GetByteCount(payloadJson) is 0 or > MaximumItemBytes)
        {
            throw new ArgumentException(
                "A provider replay item exceeds its byte limit.",
                nameof(payloadJson));
        }

        try
        {
            using var document = JsonDocument.Parse(
                payloadJson,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "A provider replay item must be a JSON object.",
                    nameof(payloadJson));
            }

            var remainingNodes = MaximumJsonNodes;
            CountNodes(document.RootElement, ref remainingNodes);
            return document.RootElement.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "A provider replay item is not valid bounded JSON.",
                nameof(payloadJson),
                exception);
        }
    }

    private static bool BelongsTo(
        AgentProviderReplayFormat format,
        AgentProviderReplayItemKind kind) => format switch
        {
            AgentProviderReplayFormat.AnthropicContentBlocks => kind is
                AgentProviderReplayItemKind.AnthropicSummarizedThinking
                or AgentProviderReplayItemKind.AnthropicSuppressedThinking
                or AgentProviderReplayItemKind.AnthropicRedactedThinking
                or AgentProviderReplayItemKind.AnthropicText
                or AgentProviderReplayItemKind.AnthropicToolUse,
            AgentProviderReplayFormat.OpenAiResponseItems => kind is
                AgentProviderReplayItemKind.OpenAiReasoning
                or AgentProviderReplayItemKind.OpenAiReasoningWithSuppressedRaw
                or AgentProviderReplayItemKind.OpenAiMessage
                or AgentProviderReplayItemKind.OpenAiFunctionCall,
            _ => false,
        };

    private bool MatchesAnthropicMessage(AgentMessage message)
    {
        var text = new StringBuilder();
        var summarizedReasoning = new StringBuilder();
        var hasSummarizedReasoning = false;
        var hasSuppressedReasoning = false;
        foreach (var item in Items)
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var value = document.RootElement;
            switch (item.Kind)
            {
                case AgentProviderReplayItemKind.AnthropicSummarizedThinking:
                    if (!IsAnthropicThinking(value, out var summary))
                    {
                        return false;
                    }

                    hasSummarizedReasoning = true;
                    summarizedReasoning.Append(summary);
                    break;
                case AgentProviderReplayItemKind.AnthropicSuppressedThinking:
                    if (!IsAnthropicThinking(value, out _))
                    {
                        return false;
                    }

                    hasSuppressedReasoning = true;
                    break;
                case AgentProviderReplayItemKind.AnthropicRedactedThinking:
                    if (value.EnumerateObject().Count() != 2
                        || !HasExactType(value, "redacted_thinking")
                        || !TryGetString(value, "data", allowEmpty: false, out _))
                    {
                        return false;
                    }

                    break;
                case AgentProviderReplayItemKind.AnthropicText:
                    if (value.EnumerateObject().Count() != 2
                        || !HasExactType(value, "text")
                        || !TryGetString(value, "text", allowEmpty: false, out var part))
                    {
                        return false;
                    }

                    text.Append(part);
                    break;
                case AgentProviderReplayItemKind.AnthropicToolUse:
                    if (!MatchesAnthropicTool(value, item, message))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        if (hasSummarizedReasoning && hasSuppressedReasoning)
        {
            return false;
        }

        return string.Equals(text.ToString(), message.Content, StringComparison.Ordinal)
            && (hasSuppressedReasoning
                ? message.ReasoningSummary is null
                : string.Equals(
                    summarizedReasoning.ToString(),
                    message.ReasoningSummary ?? string.Empty,
                    StringComparison.Ordinal));
    }

    private bool MatchesOpenAiMessage(AgentMessage message)
    {
        var text = new StringBuilder();
        foreach (var item in Items)
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var value = document.RootElement;
            switch (item.Kind)
            {
                case AgentProviderReplayItemKind.OpenAiReasoning:
                    if (!IsOpenAiReasoning(value)
                        || HasNonEmptyReasoningContent(value))
                    {
                        return false;
                    }

                    break;
                case AgentProviderReplayItemKind.OpenAiReasoningWithSuppressedRaw:
                    if (!IsOpenAiReasoning(value))
                    {
                        return false;
                    }

                    break;
                case AgentProviderReplayItemKind.OpenAiMessage:
                    if (!AppendOpenAiMessageText(value, text))
                    {
                        return false;
                    }

                    break;
                case AgentProviderReplayItemKind.OpenAiFunctionCall:
                    if (!MatchesOpenAiTool(value, item, message))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return string.Equals(text.ToString(), message.Content, StringComparison.Ordinal);
    }

    private static bool IsAnthropicThinking(
        JsonElement value,
        out string thinking)
    {
        thinking = string.Empty;
        return value.EnumerateObject().Count() == 3
            && HasExactType(value, "thinking")
            && TryGetString(value, "thinking", allowEmpty: true, out thinking)
            && TryGetString(value, "signature", allowEmpty: false, out _);
    }

    private static bool MatchesAnthropicTool(
        JsonElement value,
        AgentProviderReplayItem item,
        AgentMessage message)
    {
        if (value.EnumerateObject().Count() != 4
            || !HasExactType(value, "tool_use")
            || item.ToolIndex is not { } toolIndex
            || toolIndex >= message.ToolCalls.Length
            || !TryGetString(value, "id", allowEmpty: false, out var id)
            || !TryGetString(value, "name", allowEmpty: false, out var name)
            || !value.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var proposal = message.ToolCalls[toolIndex];
        return string.Equals(id, proposal.ProviderCallId, StringComparison.Ordinal)
            && string.Equals(name, proposal.ProviderName, StringComparison.Ordinal)
            && JsonElement.DeepEquals(input, proposal.Arguments);
    }

    private static bool IsOpenAiReasoning(JsonElement value) =>
        HasExactType(value, "reasoning")
        && TryGetString(value, "id", allowEmpty: false, out _);

    private static bool HasNonEmptyReasoningContent(JsonElement value)
    {
        if (!value.TryGetProperty("content", out var content)
            || content.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !TryGetString(part, "text", allowEmpty: true, out var text)
                || text.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AppendOpenAiMessageText(
        JsonElement value,
        StringBuilder text)
    {
        if (!HasExactType(value, "message")
            || !TryGetString(value, "id", allowEmpty: false, out _)
            || !value.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !TryGetString(part, "type", allowEmpty: false, out var type))
            {
                return false;
            }

            var propertyName = type switch
            {
                "output_text" => "text",
                "refusal" => "refusal",
                _ => null,
            };
            if (propertyName is null
                || !TryGetString(part, propertyName, allowEmpty: true, out var partText))
            {
                return false;
            }

            text.Append(partText);
        }

        return true;
    }

    private static bool MatchesOpenAiTool(
        JsonElement value,
        AgentProviderReplayItem item,
        AgentMessage message)
    {
        if (!HasExactType(value, "function_call")
            || item.ToolIndex is not { } toolIndex
            || toolIndex >= message.ToolCalls.Length
            || !TryGetString(value, "id", allowEmpty: false, out _)
            || !TryGetString(value, "call_id", allowEmpty: false, out var callId)
            || !TryGetString(value, "name", allowEmpty: false, out var name)
            || !TryGetString(
                value,
                "arguments",
                allowEmpty: false,
                out var arguments))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            var proposal = message.ToolCalls[toolIndex];
            return document.RootElement.ValueKind == JsonValueKind.Object
                && string.Equals(
                    callId,
                    proposal.ProviderCallId,
                    StringComparison.Ordinal)
                && string.Equals(
                    name,
                    proposal.ProviderName,
                    StringComparison.Ordinal)
                && JsonElement.DeepEquals(
                    document.RootElement,
                    proposal.Arguments);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactType(JsonElement value, string expected) =>
        TryGetString(value, "type", allowEmpty: false, out var type)
        && string.Equals(type, expected, StringComparison.Ordinal);

    private static bool TryGetString(
        JsonElement value,
        string propertyName,
        bool allowEmpty,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString()!;
        return (allowEmpty || result.Length > 0)
            && Encoding.UTF8.GetByteCount(result) <= MaximumItemBytes;
    }

    private static void CountNodes(JsonElement element, ref int remainingNodes)
    {
        if (--remainingNodes < 0)
        {
            throw new ArgumentException("A provider replay item exceeds its node limit.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                CountNodes(property.Value, ref remainingNodes);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CountNodes(item, ref remainingNodes);
            }
        }
    }
}
