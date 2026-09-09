using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Asura.Agent;

internal enum ProviderStreamErrorCode
{
    InvalidTransition,
    InvalidValue,
    UnknownTool,
    DuplicateToolCall,
    LimitExceeded,
    InvalidToolArguments,
    IncompleteResponse,
    InconsistentStopReason,
}

internal sealed class ProviderStreamException(
    ProviderStreamErrorCode code,
    string message) : Exception(message)
{
    public ProviderStreamErrorCode Code { get; } = code;

}

internal sealed record ReducedToolCall(
    int Index,
    string ProviderCallId,
    string Name,
    JsonElement Arguments);

internal sealed record ReducedProviderTurn(
    string AssistantText,
    string? ReasoningSummary,
    AgentTokenUsage? Usage,
    ImmutableArray<ReducedToolCall> ToolCalls,
    AgentProviderStopReason StopReason,
    AgentProviderReplayState? ProviderReplayState);

internal sealed class ProviderTurnReducer
{
    private const int MaximumProviderCallIdLength = 256;

    private readonly AgentKernelLimits _limits;
    private readonly Dictionary<string, string> _toolNamesByProviderName;
    private readonly HashSet<string> _providerCallIds = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, ToolCallBuilder> _toolCalls = [];
    private readonly StringBuilder _assistantText = new();
    private readonly StringBuilder _reasoningSummary = new();
    private ReducerState _state;
    private int _assistantTextBytes;
    private int _reasoningSummaryBytes;
    private int _eventCount;
    private int _toolArgumentBytes;
    private AgentProviderStopReason? _stopReason;
    private AgentTokenUsage? _usage;
    private AgentProviderReplayState? _providerReplayState;

    public ProviderTurnReducer(
        IReadOnlyDictionary<string, string> toolNamesByProviderName,
        AgentKernelLimits limits)
    {
        ArgumentNullException.ThrowIfNull(toolNamesByProviderName);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _toolNamesByProviderName = new Dictionary<string, string>(
            toolNamesByProviderName.Count,
            StringComparer.Ordinal);
        foreach (var (providerName, internalName) in toolNamesByProviderName)
        {
            AgentToolDefinition.ValidateProviderName(
                providerName,
                nameof(toolNamesByProviderName));
            AgentToolDefinition.ValidateIdentifier(
                internalName,
                nameof(toolNamesByProviderName),
                AgentToolDefinition.MaximumNameLength);
            _toolNamesByProviderName.Add(providerName, internalName);
        }
    }

    public void Apply(AgentProviderEvent providerEvent)
    {
        if (providerEvent is null)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidValue,
                "The provider emitted a null event.");
        }

        _eventCount = checked(_eventCount + 1);
        if (_eventCount > _limits.MaximumProviderEventsPerTurn)
        {
            throw Failure(
                ProviderStreamErrorCode.LimitExceeded,
                "The provider response exceeded its event limit.");
        }

        if (_state == ReducerState.Completed)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidTransition,
                "The provider emitted an event after response completion.");
        }

        if (_providerReplayState is not null
            && providerEvent is not AgentProviderEvent.ResponseCompleted)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidTransition,
                "The finalized provider replay state must be the final event before completion.");
        }

        switch (providerEvent)
        {
            case AgentProviderEvent.ResponseStarted:
                StartResponse();
                break;
            case AgentProviderEvent.TextDelta textDelta:
                AppendText(textDelta.Value);
                break;
            case AgentProviderEvent.ReasoningSummaryDelta reasoningDelta:
                AppendReasoningSummary(reasoningDelta.Value);
                break;
            case AgentProviderEvent.ToolCallStarted toolCallStarted:
                StartToolCall(toolCallStarted);
                break;
            case AgentProviderEvent.ToolCallArgumentsDelta argumentsDelta:
                AppendToolArguments(argumentsDelta);
                break;
            case AgentProviderEvent.ToolCallCompleted toolCallCompleted:
                CompleteToolCall(toolCallCompleted.Index);
                break;
            case AgentProviderEvent.Usage usage:
                ApplyUsage(usage.Value);
                break;
            case AgentProviderEvent.ReplayStateFinalized replayState:
                ApplyReplayState(replayState.Value);
                break;
            case AgentProviderEvent.ResponseCompleted responseCompleted:
                CompleteResponse(responseCompleted.StopReason);
                break;
            default:
                throw Failure(
                    ProviderStreamErrorCode.InvalidTransition,
                    "The provider emitted an unsupported event.");
        }
    }

    public ReducedProviderTurn Build()
    {
        if (_state != ReducerState.Completed || _stopReason is null)
        {
            throw Failure(
                ProviderStreamErrorCode.IncompleteResponse,
                "The provider response did not complete.");
        }

        return new ReducedProviderTurn(
            _assistantText.ToString(),
            _reasoningSummary.Length == 0
                ? null
                : _reasoningSummary.ToString(),
            _usage,
            [.. _toolCalls.Values.Select(toolCall => toolCall.Build())],
            _stopReason.Value,
            _providerReplayState);
    }

    private void StartResponse()
    {
        if (_state != ReducerState.WaitingForStart)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidTransition,
                "The provider response started more than once.");
        }

        _state = ReducerState.Streaming;
    }

    private void AppendText(string value)
    {
        EnsureStreaming();
        var byteCount = ValidateFragment(
            value,
            _limits.MaximumProviderTextFragmentBytes,
            "The provider text fragment is invalid.");
        if (_assistantTextBytes > _limits.MaximumAssistantTextBytes - byteCount)
        {
            throw Failure(
                ProviderStreamErrorCode.LimitExceeded,
                "The provider response text exceeded its byte limit.");
        }

        _assistantText.Append(value);
        _assistantTextBytes += byteCount;
    }

    private void AppendReasoningSummary(string value)
    {
        EnsureStreaming();
        var byteCount = ValidateFragment(
            value,
            _limits.MaximumProviderTextFragmentBytes,
            "The provider reasoning-summary fragment is invalid.");
        if (_reasoningSummaryBytes
            > _limits.MaximumReasoningSummaryBytes - byteCount)
        {
            throw Failure(
                ProviderStreamErrorCode.LimitExceeded,
                "The provider reasoning summary exceeded its byte limit.");
        }

        _reasoningSummary.Append(value);
        _reasoningSummaryBytes += byteCount;
    }

    private void ApplyUsage(AgentTokenUsage usage)
    {
        EnsureStreaming();
        if (usage is null || _usage is not null)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidValue,
                "The provider supplied invalid or duplicate token usage.");
        }

        _usage = usage;
    }

    private void ApplyReplayState(AgentProviderReplayState replayState)
    {
        EnsureStreaming();
        if (replayState is null || _providerReplayState is not null)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidValue,
                "The provider supplied invalid or duplicate replay state.");
        }

        _providerReplayState = replayState;
    }

    private void StartToolCall(AgentProviderEvent.ToolCallStarted toolCall)
    {
        EnsureStreaming();
        if (toolCall.Index < 0 || toolCall.Index != _toolCalls.Count)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidValue,
                "Tool-call indices must be zero-based and contiguous.");
        }

        if (_toolCalls.Count == _limits.MaximumToolCallsPerTurn)
        {
            throw Failure(
                ProviderStreamErrorCode.LimitExceeded,
                "The provider response exceeded its tool-call limit.");
        }

        ValidateProviderIdentifier(
            toolCall.ProviderCallId,
            MaximumProviderCallIdLength,
            "The provider tool-call ID is invalid.");
        if (!AgentToolDefinition.IsValidProviderName(toolCall.Name))
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidValue,
                "The provider tool name is invalid.");
        }

        if (!_toolNamesByProviderName.TryGetValue(
                toolCall.Name,
                out var internalToolName))
        {
            throw Failure(
                ProviderStreamErrorCode.UnknownTool,
                "The provider requested an unknown tool.");
        }

        if (!_providerCallIds.Add(toolCall.ProviderCallId))
        {
            throw Failure(
                ProviderStreamErrorCode.DuplicateToolCall,
                "The provider reused a tool-call ID.");
        }

        _toolCalls.Add(
            toolCall.Index,
            new ToolCallBuilder(
                toolCall.Index,
                toolCall.ProviderCallId,
                internalToolName,
                _limits));
    }

    private void AppendToolArguments(AgentProviderEvent.ToolCallArgumentsDelta argumentsDelta)
    {
        EnsureStreaming();
        var toolCall = RequireOpenToolCall(argumentsDelta.Index);
        var fragmentBytes = toolCall.Append(argumentsDelta.Value);
        if (_toolArgumentBytes
            > _limits.MaximumTotalToolArgumentBytesPerTurn - fragmentBytes)
        {
            throw Failure(
                ProviderStreamErrorCode.LimitExceeded,
                "The provider response exceeded its total tool-argument byte limit.");
        }

        _toolArgumentBytes += fragmentBytes;
    }

    private void CompleteToolCall(int index)
    {
        EnsureStreaming();
        var toolCall = RequireOpenToolCall(index);
        toolCall.Complete();
    }

    private void CompleteResponse(AgentProviderStopReason stopReason)
    {
        EnsureStreaming();
        if (!Enum.IsDefined(stopReason))
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidValue,
                "The provider supplied an unknown stop reason.");
        }

        if (_toolCalls.Values.Any(toolCall => !toolCall.IsComplete))
        {
            throw Failure(
                ProviderStreamErrorCode.IncompleteResponse,
                "The provider completed while a tool call was still open.");
        }

        var hasToolCalls = _toolCalls.Count > 0;
        if (hasToolCalls != (stopReason == AgentProviderStopReason.ToolUse))
        {
            throw Failure(
                ProviderStreamErrorCode.InconsistentStopReason,
                "The provider stop reason does not match the response content.");
        }

        _stopReason = stopReason;
        _state = ReducerState.Completed;
    }

    private ToolCallBuilder RequireOpenToolCall(int index)
    {
        if (!_toolCalls.TryGetValue(index, out var toolCall))
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidValue,
                "The provider referenced an unknown tool-call index.");
        }

        if (toolCall.IsComplete)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidTransition,
                "The provider modified a completed tool call.");
        }

        return toolCall;
    }

    private void EnsureStreaming()
    {
        if (_state != ReducerState.Streaming)
        {
            throw Failure(
                ProviderStreamErrorCode.InvalidTransition,
                "The provider emitted response content before starting the response.");
        }
    }

    private static int ValidateFragment(string value, int maximumBytes, string message)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw Failure(ProviderStreamErrorCode.InvalidValue, message);
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > maximumBytes)
        {
            throw Failure(ProviderStreamErrorCode.LimitExceeded, message);
        }

        return byteCount;
    }

    private static void ValidateProviderIdentifier(
        string value,
        int maximumLength,
        string message)
    {
        if (!AgentToolDefinition.IsValidIdentifier(value, maximumLength))
        {
            throw Failure(ProviderStreamErrorCode.InvalidValue, message);
        }
    }

    private static ProviderStreamException Failure(
        ProviderStreamErrorCode code,
        string message) =>
        new(code, message);

    private enum ReducerState
    {
        WaitingForStart,
        Streaming,
        Completed,
    }

    private sealed class ToolCallBuilder(
        int index,
        string providerCallId,
        string name,
        AgentKernelLimits limits)
    {
        private readonly StringBuilder _arguments = new();
        private int _argumentBytes;
        private JsonElement _parsedArguments;

        public bool IsComplete { get; private set; }

        public int Append(string value)
        {
            var byteCount = ValidateFragment(
                value,
                limits.MaximumToolArgumentFragmentBytes,
                "The provider tool-argument fragment is invalid.");
            if (_argumentBytes > limits.MaximumToolArgumentBytes - byteCount)
            {
                throw Failure(
                    ProviderStreamErrorCode.LimitExceeded,
                    "The provider tool arguments exceeded their byte limit.");
            }

            _arguments.Append(value);
            _argumentBytes += byteCount;
            return byteCount;
        }

        public void Complete()
        {
            if (_arguments.Length == 0)
            {
                throw Failure(
                    ProviderStreamErrorCode.InvalidToolArguments,
                    "The provider tool arguments were empty.");
            }

            try
            {
                using var document = JsonDocument.Parse(
                    _arguments.ToString(),
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = limits.MaximumJsonDepth,
                    });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw Failure(
                        ProviderStreamErrorCode.InvalidToolArguments,
                        "The provider tool arguments must be a JSON object.");
                }

                var remainingNodes = limits.MaximumJsonNodes;
                ValidateJson(document.RootElement, ref remainingNodes);
                _parsedArguments = document.RootElement.Clone();
                IsComplete = true;
            }
            catch (JsonException)
            {
                throw Failure(
                    ProviderStreamErrorCode.InvalidToolArguments,
                    "The provider tool arguments were not valid bounded JSON.");
            }
        }

        public ReducedToolCall Build()
        {
            if (!IsComplete)
            {
                throw Failure(
                    ProviderStreamErrorCode.IncompleteResponse,
                    "The provider tool call did not complete.");
            }

            return new ReducedToolCall(
                index,
                providerCallId,
                name,
                _parsedArguments.Clone());
        }

        private static void ValidateJson(JsonElement element, ref int remainingNodes)
        {
            if (--remainingNodes < 0)
            {
                throw Failure(
                    ProviderStreamErrorCode.LimitExceeded,
                    "The provider tool arguments exceeded their node limit.");
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw Failure(
                            ProviderStreamErrorCode.InvalidToolArguments,
                            "The provider tool arguments contained a duplicate property.");
                    }

                    ValidateJson(property.Value, ref remainingNodes);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ValidateJson(item, ref remainingNodes);
                }
            }
        }
    }
}
