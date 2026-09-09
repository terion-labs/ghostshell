using System.Collections.Immutable;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

internal sealed class AnthropicAgentProvider(
    AiProviderProfile profile,
    string model,
    AiProviderHttpTransport transport,
    AiProviderRuntimeLimits limits) : IAgentProvider
{
    public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
        AgentProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(limits.StreamTimeout);
        await using var enumerator = StreamCoreAsync(request, operation.Token)
            .GetAsyncEnumerator(operation.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.Timeout,
                    innerException: exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException exception)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProviderUnavailable,
                    innerException: exception);
            }

            if (!moved)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    private async IAsyncEnumerable<AgentProviderEvent> StreamCoreAsync(
        AgentProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = WriteRequest(request);
        using var httpRequest = await transport.CreateRequestAsync(
            profile,
            HttpMethod.Post,
            "messages",
            "text/event-stream",
            body,
            cancellationToken).ConfigureAwait(false);
        using var response = await transport
            .SendAsync(profile, httpRequest, cancellationToken)
            .ConfigureAwait(false);
        AiProviderHttpTransport.ValidateContent(
            response,
            "text/event-stream",
            limits.MaximumStreamResponseBytes);
        yield return new AgentProviderEvent.ResponseStarted();

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var limited = new LimitedReadStream(
            responseStream,
            limits.MaximumStreamResponseBytes);
        var parser = SseParser.Create(
            limited,
            (_, data) =>
            {
                if (data.Length > limits.MaximumSseEventBytes)
                {
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.ResponseTooLarge);
                }

                return data.ToArray();
            });
        var state = new AnthropicStreamState(
            limits.MaximumProviderFragmentBytes,
            AiProviderReasoningPolicy.SupportsSummarizedThinking(model)
                && request.ReasoningEffort is AgentReasoningEffort.Low
                    or AgentReasoningEffort.Medium
                    or AgentReasoningEffort.High);
        var eventCount = 0;
        await foreach (var item in parser
                           .EnumerateAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            eventCount = checked(eventCount + 1);
            if (eventCount > limits.MaximumSseEvents)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            using var document = AiProviderJson.Parse(item.Data);
            foreach (var providerEvent in state.Apply(
                         item.EventType,
                         document.RootElement))
            {
                yield return providerEvent;
            }
        }

        var stopReason = state.Complete();
        if (state.Usage() is { } usage)
        {
            yield return new AgentProviderEvent.Usage(usage);
        }

        if (state.BuildReplayState(ReplayBinding()) is { } replayState)
        {
            yield return new AgentProviderEvent.ReplayStateFinalized(replayState);
        }

        yield return new AgentProviderEvent.ResponseCompleted(stopReason);
    }

    private byte[] WriteRequest(AgentProviderRequest request) =>
        AiProviderJson.Write(
            limits.MaximumRequestBytes,
            writer =>
            {
                EnsureImageSupport(request);
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteNumber("max_tokens", limits.MaximumOutputTokens);
                writer.WriteBoolean("stream", true);
                WriteReasoningConfiguration(writer, request.ReasoningEffort);
                var system = SystemPrompt(request);
                if (system.Length > 0)
                {
                    writer.WriteString("system", system);
                }

                writer.WriteStartArray("messages");
                for (var index = 0; index < request.Messages.Length; index++)
                {
                    var message = request.Messages[index];
                    if (message.Role == AgentMessageRole.System)
                    {
                        continue;
                    }

                    if (message.Role == AgentMessageRole.Tool)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", "user");
                        writer.WriteStartArray("content");
                        while (index < request.Messages.Length
                               && request.Messages[index].Role == AgentMessageRole.Tool)
                        {
                            WriteToolResult(writer, request.Messages[index]);
                            index++;
                        }

                        index--;
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        continue;
                    }

                    WriteMessage(writer, message);
                }

                writer.WriteEndArray();
                if (request.Tools.Length > 0)
                {
                    writer.WriteStartArray("tools");
                    foreach (var tool in request.Tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", tool.ProviderName);
                        writer.WriteString("description", tool.Description);
                        writer.WritePropertyName("input_schema");
                        tool.InputSchema.WriteTo(writer);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            });

    private void WriteReasoningConfiguration(
        Utf8JsonWriter writer,
        AgentReasoningEffort effort)
    {
        if (effort == AgentReasoningEffort.Automatic)
        {
            return;
        }

        if (!AiProviderReasoningPolicy.SupportedEfforts(profile, model).Contains(effort))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        if (effort == AgentReasoningEffort.Off)
        {
            writer.WriteStartObject("thinking");
            writer.WriteString("type", "disabled");
            writer.WriteEndObject();

            return;
        }

        writer.WriteStartObject("thinking");
        writer.WriteString("type", "adaptive");
        if (AiProviderReasoningPolicy.SupportsSummarizedThinking(model))
        {
            writer.WriteString("display", "summarized");
        }

        writer.WriteEndObject();
        writer.WriteStartObject("output_config");
        writer.WriteString(
            "effort",
            effort switch
            {
                AgentReasoningEffort.Low => "low",
                AgentReasoningEffort.Medium => "medium",
                AgentReasoningEffort.High => "high",
                AgentReasoningEffort.ExtraHigh
                    when AiProviderReasoningPolicy.SupportsNativeExtraHighThinking(model) =>
                    "xhigh",
                AgentReasoningEffort.ExtraHigh => "max",
                AgentReasoningEffort.Max => "max",
                _ => throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration),
            });
        writer.WriteEndObject();
    }

    private void EnsureImageSupport(AgentProviderRequest request)
    {
        if (!profile.Capabilities.SupportsImageInput
            && request.Messages.Any(message => message.Images.Length > 0))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }

    private void WriteMessage(Utf8JsonWriter writer, AgentMessage message)
    {
        if (message.Role == AgentMessageRole.Assistant
            && message.ProviderReplayState is { } replayState)
        {
            var binding = ReplayBinding();
            if (replayState.Matches(binding))
            {
                WriteReplayAssistant(writer, message, replayState);
                return;
            }

            if (!replayState.MatchesRoute(binding))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
            }
        }

        writer.WriteStartObject();
        switch (message.Role)
        {
            case AgentMessageRole.Summary:
            case AgentMessageRole.User:
                writer.WriteString("role", "user");
                if (message.Images.Length == 0)
                {
                    writer.WriteString("content", message.Content);
                    break;
                }

                writer.WriteStartArray("content");
                foreach (var image in message.Images)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image");
                    writer.WriteStartObject("source");
                    writer.WriteString("type", "base64");
                    writer.WriteString("media_type", image.MediaType);
                    writer.WriteString(
                        "data",
                        Convert.ToBase64String(image.Content));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                if (message.Content.Length > 0)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", message.Content);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case AgentMessageRole.Assistant:
                writer.WriteString("role", "assistant");
                if (message.ToolCalls.Length == 0)
                {
                    writer.WriteString("content", message.Content);
                    break;
                }

                writer.WriteStartArray("content");
                if (message.Content.Length > 0)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", message.Content);
                    writer.WriteEndObject();
                }

                foreach (var toolCall in message.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", toolCall.ProviderCallId);
                    writer.WriteString(
                        "name",
                        toolCall.ProviderName);
                    writer.WritePropertyName("input");
                    toolCall.Arguments.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            default:
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        writer.WriteEndObject();
    }

    private void WriteReplayAssistant(
        Utf8JsonWriter writer,
        AgentMessage message,
        AgentProviderReplayState replayState)
    {
        if (replayState.Format != AgentProviderReplayFormat.AnthropicContentBlocks)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        writer.WriteStartArray("content");
        var visibleText = new StringBuilder();
        var reasoningText = new StringBuilder();
        foreach (var item in replayState.Items)
        {
            using var document = AiProviderJson.Parse(
                Encoding.UTF8.GetBytes(item.PayloadJson));
            var block = document.RootElement;
            var type = AiProviderJson.RequiredBoundedString(block, "type", 64);
            var expectedType = item.Kind switch
            {
                AgentProviderReplayItemKind.AnthropicSummarizedThinking
                    or AgentProviderReplayItemKind.AnthropicSuppressedThinking =>
                    "thinking",
                AgentProviderReplayItemKind.AnthropicRedactedThinking =>
                    "redacted_thinking",
                AgentProviderReplayItemKind.AnthropicText => "text",
                AgentProviderReplayItemKind.AnthropicToolUse => "tool_use",
                _ => throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration),
            };
            if (!string.Equals(type, expectedType, StringComparison.Ordinal))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
            }

            switch (item.Kind)
            {
                case AgentProviderReplayItemKind.AnthropicText:
                    RequireReplayPropertyCount(block, 2);
                    visibleText.Append(RequiredReplayString(
                        block,
                        "text",
                        allowEmpty: false));
                    break;
                case AgentProviderReplayItemKind.AnthropicSummarizedThinking:
                case AgentProviderReplayItemKind.AnthropicSuppressedThinking:
                    RequireReplayPropertyCount(block, 3);
                    reasoningText.Append(RequiredReplayString(
                        block,
                        "thinking",
                        allowEmpty: true));
                    _ = RequiredReplayString(
                        block,
                        "signature",
                        allowEmpty: false);
                    break;
                case AgentProviderReplayItemKind.AnthropicRedactedThinking:
                    RequireReplayPropertyCount(block, 2);
                    _ = RequiredReplayString(block, "data", allowEmpty: false);
                    break;
                case AgentProviderReplayItemKind.AnthropicToolUse:
                    RequireReplayPropertyCount(block, 4);
                    ValidateReplayTool(
                        block,
                        message,
                        item.ToolIndex!.Value);
                    break;
                default:
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.InvalidConfiguration);
            }

            block.WriteTo(writer);
        }

        if (!string.Equals(
                visibleText.ToString(),
                message.Content,
                StringComparison.Ordinal)
            || (replayState.ContainsSuppressedRawReasoning
                ? message.ReasoningSummary is not null
                : !string.Equals(
                    reasoningText.ToString(),
                    message.ReasoningSummary ?? string.Empty,
                    StringComparison.Ordinal)))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string RequiredReplayString(
        JsonElement block,
        string propertyName,
        bool allowEmpty)
    {
        if (!block.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        var value = property.GetString()!;
        if ((!allowEmpty && value.Length == 0)
            || Encoding.UTF8.GetByteCount(value)
                > AgentProviderReplayState.MaximumItemBytes)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        return value;
    }

    private static void RequireReplayPropertyCount(
        JsonElement block,
        int expectedCount)
    {
        if (block.EnumerateObject().Count() != expectedCount)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }

    private static void ValidateReplayTool(
        JsonElement block,
        AgentMessage message,
        int toolIndex)
    {
        if (toolIndex >= message.ToolCalls.Length)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        var proposal = message.ToolCalls[toolIndex];
        var id = AiProviderJson.RequiredBoundedString(block, "id", 256);
        var name = AiProviderJson.RequiredBoundedString(block, "name", 128);
        var input = AiProviderJson.RequiredObject(block, "input");
        if (!string.Equals(id, proposal.ProviderCallId, StringComparison.Ordinal)
            || !string.Equals(name, proposal.ProviderName, StringComparison.Ordinal)
            || !JsonElement.DeepEquals(input, proposal.Arguments))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }

    private AgentProviderReplayBinding ReplayBinding() =>
        new(
            profile.Id,
            profile.Identity,
            profile.Protocol,
            model,
            profile.Endpoint,
            ReplayRouteIdentity());

    private string ReplayRouteIdentity()
    {
        var (route, credentialReference) = profile.Authentication switch
        {
            AiProviderAuthentication.ApiKey apiKey =>
                ("anthropic-messages-api-key", apiKey.Secret),
            AiProviderAuthentication.OAuth oauth =>
                ("anthropic-messages-oauth", oauth.Session),
            AiProviderAuthentication.None =>
                ("anthropic-messages-none", (SecretRef?)null),
            _ => ("anthropic-messages-unsupported", (SecretRef?)null),
        };
        if (credentialReference is null)
        {
            return route;
        }

        // Bind replay to the selected vault capability without persisting it.
        var bindingInput = Encoding.UTF8.GetBytes(
            $"{profile.Id.Value}\0{credentialReference.Value.Value}");
        var digest = Convert.ToHexString(SHA256.HashData(bindingInput))
            .ToLowerInvariant();
        return $"{route}:{digest}";
    }

    private static void WriteToolResult(
        Utf8JsonWriter writer,
        AgentMessage message)
    {
        if (message.ToolResult is not { } result)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        writer.WriteStartObject();
        writer.WriteString("type", "tool_result");
        writer.WriteString("tool_use_id", result.ProviderCallId);
        writer.WriteBoolean(
            "is_error",
            result.Status == AgentToolResultStatus.Failed);
        writer.WriteString("content", AiProviderJson.ToolResultContent(result));
        writer.WriteEndObject();
    }

    private static string SystemPrompt(AgentProviderRequest request)
    {
        var systemMessages = request.Messages
            .Where(message => message.Role == AgentMessageRole.System)
            .Select(message => message.Content)
            .ToArray();
        return string.Join("\n\n", systemMessages);
    }

    private sealed class AnthropicStreamState(
        int maximumFragmentBytes,
        bool emitReasoningSummaries)
    {
        private readonly List<ContentBlock> _blocks = [];
        private AgentProviderStopReason? _stopReason;
        private bool _messageStarted;
        private bool _messageStopped;
        private int _toolCount;
        private long? _inputTokens;
        private long? _outputTokens;
        private long _cachedInputTokens;
        private long _reasoningTokens;

        public IReadOnlyList<AgentProviderEvent> Apply(
            string eventType,
            JsonElement root)
        {
            if (_messageStopped
                || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var payloadType)
                || payloadType.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var type = payloadType.GetString()!;
            if (!string.Equals(eventType, type, StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            if (_stopReason is not null
                && type is not ("message_stop" or "ping"))
            {
                throw ProtocolError();
            }

            var events = new List<AgentProviderEvent>();
            switch (type)
            {
                case "message_start":
                    StartMessage(root);
                    break;
                case "content_block_start":
                    StartContentBlock(root, events);
                    break;
                case "content_block_delta":
                    ApplyContentDelta(root, events);
                    break;
                case "content_block_stop":
                    StopContentBlock(root, events);
                    break;
                case "message_delta":
                    ApplyMessageDelta(root);
                    break;
                case "message_stop":
                    StopMessage();
                    break;
                case "ping":
                    EnsureMessageStarted();
                    break;
                case "error":
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.ProviderUnavailable);
                default:
                    throw ProtocolError();
            }

            return events;
        }

        public AgentProviderStopReason Complete()
        {
            if (!_messageStarted || !_messageStopped || _stopReason is null)
            {
                throw ProtocolError();
            }

            return _stopReason.Value;
        }

        public AgentTokenUsage? Usage() =>
            _inputTokens is { } input && _outputTokens is { } output
                ? new AgentTokenUsage(
                    input,
                    output,
                    _cachedInputTokens,
                    _reasoningTokens)
                : null;

        public AgentProviderReplayState? BuildReplayState(
            AgentProviderReplayBinding binding)
        {
            if (!_messageStopped)
            {
                throw ProtocolError();
            }

            if (_blocks.Count == 0)
            {
                return null;
            }

            if (_blocks.Any(block => !block.CanReplay))
            {
                throw ProtocolError();
            }

            var items = _blocks
                .Select(block => block.BuildReplayItem())
                .ToImmutableArray();
            return new AgentProviderReplayState(
                binding,
                AgentProviderReplayFormat.AnthropicContentBlocks,
                items);
        }

        private void StartMessage(JsonElement root)
        {
            if (_messageStarted)
            {
                throw ProtocolError();
            }

            _messageStarted = true;
            if (root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("usage", out var usage)
                && usage.ValueKind != JsonValueKind.Null)
            {
                ReadUsage(usage, isInitial: true);
            }
        }

        private void StartContentBlock(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            EnsureMessageStarted();
            if (_blocks.Count > 0 && !_blocks[^1].IsStopped)
            {
                throw ProtocolError();
            }

            var index = ContentIndex(root);
            if (index != _blocks.Count)
            {
                throw ProtocolError();
            }

            var block = AiProviderJson.RequiredObject(root, "content_block");
            var type = AiProviderJson.RequiredBoundedString(block, "type", 64);
            switch (type)
            {
                case "text":
                    {
                        var initialText = ReadOptionalFragment(block, "text");
                        var state = ContentBlock.Text(index, initialText);
                        _blocks.Add(state);
                        if (!string.IsNullOrEmpty(initialText))
                        {
                            events.Add(new AgentProviderEvent.TextDelta(initialText));
                        }

                        break;
                    }
                case "tool_use":
                    {
                        var id = RequiredIdentifier(block, "id", 256);
                        var name = RequiredIdentifier(block, "name", 128);
                        var initialInput = AiProviderJson.RequiredObject(block, "input")
                            .GetRawText();
                        var toolIndex = _toolCount;
                        _toolCount = checked(_toolCount + 1);
                        var state = ContentBlock.Tool(
                            index,
                            toolIndex,
                            id,
                            name,
string.Equals(initialInput, "{}", StringComparison.Ordinal) ? null : initialInput);
                        _blocks.Add(state);
                        events.Add(new AgentProviderEvent.ToolCallStarted(
                            toolIndex,
                            id,
                            name));
                        if (!string.Equals(initialInput, "{}", StringComparison.Ordinal))
                        {
                            state.HasArguments = true;
                            events.Add(new AgentProviderEvent.ToolCallArgumentsDelta(
                                toolIndex,
                                initialInput));
                        }

                        break;
                    }
                case "thinking":
                    {
                        var initialSummary = ReadOptionalFragment(block, "thinking");
                        var initialSignature = ReadOptionalReplayFragment(
                            block,
                            "signature");
                        _blocks.Add(ContentBlock.Reasoning(
                            index,
                            initialSummary,
                            initialSignature,
                            emitReasoningSummaries));
                        if (emitReasoningSummaries
                            && !string.IsNullOrEmpty(initialSummary))
                        {
                            events.Add(new AgentProviderEvent.ReasoningSummaryDelta(
                                initialSummary));
                        }

                        break;
                    }
                case "redacted_thinking":
                    _blocks.Add(ContentBlock.RedactedReasoning(
                        index,
                        RequiredReplayFragment(block, "data")));
                    break;
                default:
                    throw ProtocolError();
            }
        }

        private void ApplyContentDelta(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            EnsureMessageStarted();
            var block = OpenBlock(root);
            var delta = AiProviderJson.RequiredObject(root, "delta");
            var deltaType = AiProviderJson.RequiredBoundedString(delta, "type", 64);
            if (block.Kind == ContentBlockKind.Text && string.Equals(deltaType, "text_delta", StringComparison.Ordinal))
            {
                var text = RequiredFragment(delta, "text");
                block.AppendContent(text);
                events.Add(new AgentProviderEvent.TextDelta(text));
                return;
            }

            if (block.Kind == ContentBlockKind.Tool && string.Equals(deltaType, "input_json_delta", StringComparison.Ordinal))
            {
                var partialJson = RequiredFragment(delta, "partial_json");
                block.AppendContent(partialJson);
                events.Add(new AgentProviderEvent.ToolCallArgumentsDelta(
                    block.ToolIndex!.Value,
                    partialJson));
                block.HasArguments = true;
                return;
            }

            if (block.Kind == ContentBlockKind.Reasoning
                && string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
            {
                var summary = RequiredFragment(delta, "thinking");
                block.AppendContent(summary);
                events.Add(new AgentProviderEvent.ReasoningSummaryDelta(summary));
                return;
            }

            if (block.Kind == ContentBlockKind.SuppressedReasoning
                && string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
            {
                block.AppendContent(RequiredFragment(delta, "thinking"));
                return;
            }

            if ((block.Kind is ContentBlockKind.Reasoning
                    or ContentBlockKind.SuppressedReasoning)
                && string.Equals(deltaType, "signature_delta", StringComparison.Ordinal))
            {
                block.AppendSignature(RequiredReplayFragment(delta, "signature"));
                return;
            }

            throw ProtocolError();
        }

        private void StopContentBlock(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            EnsureMessageStarted();
            var block = OpenBlock(root);
            if (block.Kind == ContentBlockKind.Text && block.Content.Length == 0)
            {
                throw ProtocolError();
            }

            block.IsStopped = true;
            if (block.Kind != ContentBlockKind.Tool)
            {
                return;
            }

            if (!block.HasArguments)
            {
                block.AppendContent("{}");
                events.Add(new AgentProviderEvent.ToolCallArgumentsDelta(
                    block.ToolIndex!.Value,
                    "{}"));
            }

            events.Add(new AgentProviderEvent.ToolCallCompleted(block.ToolIndex!.Value));
        }

        private void ApplyMessageDelta(JsonElement root)
        {
            EnsureMessageStarted();
            if (_blocks.Any(block => !block.IsStopped))
            {
                throw ProtocolError();
            }

            var delta = AiProviderJson.RequiredObject(root, "delta");
            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind != JsonValueKind.Null)
            {
                ReadUsage(usage, isInitial: false);
            }

            if (!delta.TryGetProperty("stop_reason", out var stopReason)
                || stopReason.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (stopReason.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var parsed = stopReason.GetString() switch
            {
                "end_turn" or "stop_sequence" => AgentProviderStopReason.EndTurn,
                "tool_use" => AgentProviderStopReason.ToolUse,
                "max_tokens" or "model_context_window_exceeded" =>
                    AgentProviderStopReason.MaximumTokens,
                "refusal" => AgentProviderStopReason.ContentFiltered,
                _ => throw ProtocolError(),
            };
            if (_stopReason is not null && _stopReason != parsed)
            {
                throw ProtocolError();
            }

            _stopReason = parsed;
        }

        private void ReadUsage(JsonElement usage, bool isInitial)
        {
            if (usage.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            if (isInitial)
            {
                var uncached = RequiredTokenCount(usage, "input_tokens");
                var cacheCreation = OptionalTokenCount(
                    usage,
                    "cache_creation_input_tokens");
                var cacheRead = OptionalTokenCount(usage, "cache_read_input_tokens");
                try
                {
                    _inputTokens = checked(uncached + cacheCreation + cacheRead);
                }
                catch (OverflowException)
                {
                    throw ProtocolError();
                }

                if (_inputTokens > AgentTokenUsage.MaximumTokenCount)
                {
                    throw ProtocolError();
                }

                _cachedInputTokens = cacheRead;
            }

            if (usage.TryGetProperty("output_tokens", out var output))
            {
                _outputTokens = TokenCount(output);
            }

            if (usage.TryGetProperty("output_tokens_details", out var details)
                && details.ValueKind != JsonValueKind.Null)
            {
                if (details.ValueKind != JsonValueKind.Object)
                {
                    throw ProtocolError();
                }

                _reasoningTokens = OptionalTokenCount(details, "thinking_tokens");
            }
        }

        private static long OptionalTokenCount(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value)
                || value.ValueKind == JsonValueKind.Null)
            {
                return 0;
            }

            return TokenCount(value);
        }

        private static long RequiredTokenCount(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value))
            {
                throw ProtocolError();
            }

            return TokenCount(value);
        }

        private static long TokenCount(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var count)
                || count is < 0 or > AgentTokenUsage.MaximumTokenCount)
            {
                throw ProtocolError();
            }

            return count;
        }

        private void StopMessage()
        {
            EnsureMessageStarted();
            if (_stopReason is null || _blocks.Any(block => !block.IsStopped))
            {
                throw ProtocolError();
            }

            _messageStopped = true;
        }

        private ContentBlock OpenBlock(JsonElement root)
        {
            var index = ContentIndex(root);
            if (index < 0 || index >= _blocks.Count || _blocks[index].IsStopped)
            {
                throw ProtocolError();
            }

            return _blocks[index];
        }

        private string? ReadOptionalFragment(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return ReadFragment(property);
        }

        private string RequiredFragment(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var property))
            {
                throw ProtocolError();
            }

            var value = ReadFragment(property);
            return value.Length == 0 ? throw ProtocolError() : value;
        }

        private static string? ReadOptionalReplayFragment(
            JsonElement parent,
            string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return ReadReplayFragment(property);
        }

        private static string RequiredReplayFragment(
            JsonElement parent,
            string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var property))
            {
                throw ProtocolError();
            }

            var value = ReadReplayFragment(property);
            return value.Length == 0 ? throw ProtocolError() : value;
        }

        private static string ReadReplayFragment(JsonElement property)
        {
            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (Encoding.UTF8.GetByteCount(value)
                > AgentProviderReplayState.MaximumItemBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            return value;
        }

        private string ReadFragment(JsonElement property)
        {
            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (Encoding.UTF8.GetByteCount(value) > maximumFragmentBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            return value;
        }

        private void EnsureMessageStarted()
        {
            if (!_messageStarted)
            {
                throw ProtocolError();
            }
        }

        private static int ContentIndex(JsonElement root)
        {
            if (!root.TryGetProperty("index", out var property)
                || property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out var index)
                || index < 0)
            {
                throw ProtocolError();
            }

            return index;
        }

        private static string RequiredIdentifier(
            JsonElement parent,
            string propertyName,
            int maximumLength)
        {
            var value = AiProviderJson.RequiredBoundedString(
                parent,
                propertyName,
                maximumLength);
            if (value.Any(char.IsWhiteSpace))
            {
                throw ProtocolError();
            }

            return value;
        }
    }

    private enum ContentBlockKind
    {
        Text,
        Tool,
        Reasoning,
        SuppressedReasoning,
        RedactedReasoning,
    }

    private sealed class ContentBlock
    {
        private int _contentBytes;
        private int _signatureBytes;

        private ContentBlock(
            int index,
            ContentBlockKind kind,
            int? toolIndex,
            string? initialContent = null,
            string? initialSignature = null,
            string? providerCallId = null,
            string? providerName = null,
            bool suppressedRawReasoning = false)
        {
            Index = index;
            Kind = kind;
            ToolIndex = toolIndex;
            AppendContent(initialContent);
            AppendSignature(initialSignature);
            ProviderCallId = providerCallId;
            ProviderName = providerName;
            ContainsSuppressedRawReasoning = suppressedRawReasoning;
        }

        public int Index { get; }

        public ContentBlockKind Kind { get; }

        public int? ToolIndex { get; }

        public StringBuilder Content { get; } = new();

        public StringBuilder Signature { get; } = new();

        public string? ProviderCallId { get; }

        public string? ProviderName { get; }

        public bool ContainsSuppressedRawReasoning { get; }

        public bool CanReplay => Kind is not (ContentBlockKind.Reasoning
            or ContentBlockKind.SuppressedReasoning)
            || Signature.Length > 0;

        public bool HasArguments { get; set; }

        public bool IsStopped { get; set; }

        public void AppendContent(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (_contentBytes > AgentProviderReplayState.MaximumItemBytes - byteCount)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            Content.Append(value);
            _contentBytes += byteCount;
        }

        public void AppendSignature(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (_signatureBytes > AgentProviderReplayState.MaximumItemBytes - byteCount)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            Signature.Append(value);
            _signatureBytes += byteCount;
        }

        public static ContentBlock Text(int index, string? initialText) =>
            new(index, ContentBlockKind.Text, toolIndex: null, initialText);

        public static ContentBlock Tool(
            int index,
            int toolIndex,
            string providerCallId,
            string providerName,
            string? initialInput) =>
            new(
                index,
                ContentBlockKind.Tool,
                toolIndex,
                initialInput,
                providerCallId: providerCallId,
                providerName: providerName);

        public static ContentBlock Reasoning(
            int index,
            string? initialThinking,
            string? initialSignature,
            bool isSummarized) =>
            new(
                index,
                isSummarized
                    ? ContentBlockKind.Reasoning
                    : ContentBlockKind.SuppressedReasoning,
                toolIndex: null,
                initialThinking,
                initialSignature,
                suppressedRawReasoning: !isSummarized);

        public static ContentBlock RedactedReasoning(int index, string data) =>
            new(
                index,
                ContentBlockKind.RedactedReasoning,
                toolIndex: null,
                initialSignature: data);

        public AgentProviderReplayItem BuildReplayItem()
        {
            if (!IsStopped)
            {
                throw ProtocolError();
            }

            var kind = Kind switch
            {
                ContentBlockKind.Text => AgentProviderReplayItemKind.AnthropicText,
                ContentBlockKind.Tool => AgentProviderReplayItemKind.AnthropicToolUse,
                ContentBlockKind.Reasoning =>
                    AgentProviderReplayItemKind.AnthropicSummarizedThinking,
                ContentBlockKind.SuppressedReasoning =>
                    AgentProviderReplayItemKind.AnthropicSuppressedThinking,
                ContentBlockKind.RedactedReasoning =>
                    AgentProviderReplayItemKind.AnthropicRedactedThinking,
                _ => throw ProtocolError(),
            };
            var json = AiProviderJson.Write(
                AgentProviderReplayState.MaximumItemBytes,
                writer =>
                {
                    writer.WriteStartObject();
                    switch (kind)
                    {
                        case AgentProviderReplayItemKind.AnthropicText:
                            writer.WriteString("type", "text");
                            writer.WriteString("text", Content.ToString());
                            break;
                        case AgentProviderReplayItemKind.AnthropicToolUse:
                            writer.WriteString("type", "tool_use");
                            writer.WriteString("id", ProviderCallId);
                            writer.WriteString("name", ProviderName);
                            writer.WritePropertyName("input");
                            using (var arguments = JsonDocument.Parse(Content.ToString()))
                            {
                                arguments.RootElement.WriteTo(writer);
                            }

                            break;
                        case AgentProviderReplayItemKind.AnthropicSummarizedThinking:
                        case AgentProviderReplayItemKind.AnthropicSuppressedThinking:
                            if (Signature.Length == 0)
                            {
                                throw ProtocolError();
                            }

                            writer.WriteString("type", "thinking");
                            writer.WriteString("thinking", Content.ToString());
                            writer.WriteString("signature", Signature.ToString());
                            break;
                        case AgentProviderReplayItemKind.AnthropicRedactedThinking:
                            writer.WriteString("type", "redacted_thinking");
                            writer.WriteString("data", Signature.ToString());
                            break;
                        default:
                            throw ProtocolError();
                    }

                    writer.WriteEndObject();
                });
            return new AgentProviderReplayItem(
                Index,
                kind,
                Encoding.UTF8.GetString(json),
                ToolIndex);
        }
    }

    private static AiProviderClientException ProtocolError() =>
        AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
}
