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

/// <summary>
/// Native OpenAI Responses API adapter. This intentionally does not reuse the
/// Chat Completions shape: response items and function-call outputs have distinct
/// identities and lifecycle events that must survive agent continuations.
/// </summary>
internal sealed class OpenAiResponsesAgentProvider(
    AiProviderProfile profile,
    string model,
    AiProviderHttpTransport transport,
    AiProviderRuntimeLimits limits,
    AgentServiceTier serviceTier = AgentServiceTier.Automatic) : IAgentProvider
{
    private static readonly Uri OpenAiCodexEndpoint =
        new("https://chatgpt.com/backend-api/codex/");

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
            "responses",
            "text/event-stream",
            body,
            cancellationToken).ConfigureAwait(false);
        if (profile.Identity == AiProviderKind.GitHubCopilot)
        {
            var initiatedByAgent = request.Messages.LastOrDefault()?.Role
                == AgentMessageRole.Tool;
            httpRequest.Headers.TryAddWithoutValidation(
                "X-Initiator",
                initiatedByAgent ? "agent" : "user");
        }

        using var response = await transport
            .SendAsync(profile, httpRequest, cancellationToken)
            .ConfigureAwait(false);
        AiProviderHttpTransport.ValidateContent(
            response,
            "text/event-stream",
            limits.MaximumStreamResponseBytes,
            allowMissingMediaType: profile.Identity == AiProviderKind.OpenAi
                && profile.Authentication is AiProviderAuthentication.OAuth);
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
        var state = new ResponsesStreamState(
            limits.MaximumProviderFragmentBytes,
            limits.MaximumRequestBytes,
            requiresEncryptedReasoning: profile.Identity == AiProviderKind.OpenAi);
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

            if (item.Data.AsSpan().SequenceEqual("[DONE]"u8))
            {
                state.MarkDone();
                continue;
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
                EnsureReasoningEffortSupport(request.ReasoningEffort);
                writer.WriteStartObject();
                writer.WriteString("model", model);
                WriteServiceTier(writer);
                if (profile.Identity == AiProviderKind.OpenAi)
                {
                    writer.WriteBoolean("store", false);
                }
                writer.WriteBoolean("stream", true);
                if (profile.Identity != AiProviderKind.OpenAi
                    || profile.Authentication is not AiProviderAuthentication.OAuth)
                {
                    // ChatGPT's Codex subscription endpoint accepts a deliberately
                    // narrower Responses shape than the public OpenAI API.
                    writer.WriteNumber("max_output_tokens", limits.MaximumOutputTokens);
                }

                writer.WriteBoolean("parallel_tool_calls", true);
                if (profile.Identity == AiProviderKind.OpenAi
                    && profile.Authentication is AiProviderAuthentication.OAuth)
                {
                    writer.WriteStartObject("text");
                    writer.WriteString("verbosity", "low");
                    writer.WriteEndObject();
                }

                WriteReasoning(writer, request.ReasoningEffort);
                if (profile.Capabilities.SupportsReasoning
                    && request.ReasoningEffort != AgentReasoningEffort.Off)
                {
                    writer.WriteStartArray("include");
                    writer.WriteStringValue("reasoning.encrypted_content");
                    writer.WriteEndArray();
                }
                var instructions = SystemInstructions(request);
                if (instructions.Length > 0)
                {
                    writer.WriteString("instructions", instructions);
                }

                writer.WriteStartArray("input");
                foreach (var message in request.Messages)
                {
                    WriteInput(writer, message);
                }

                writer.WriteEndArray();
                if (request.Tools.Length > 0)
                {
                    writer.WriteStartArray("tools");
                    foreach (var tool in request.Tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "function");
                        writer.WriteString("name", tool.ProviderName);
                        writer.WriteString("description", tool.Description);
                        writer.WritePropertyName("parameters");
                        tool.InputSchema.WriteTo(writer);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteString("tool_choice", "auto");
                }

                writer.WriteEndObject();
            });

    private void WriteServiceTier(Utf8JsonWriter writer)
    {
        if (serviceTier == AgentServiceTier.Automatic
            && AiProviderServiceTierPolicy.SupportedTiers(profile, model).IsEmpty)
        {
            return;
        }

        AiProviderServiceTierPolicy.EnsureSupported(profile, model, serviceTier);
        writer.WriteString(
            "service_tier",
            serviceTier switch
            {
                AgentServiceTier.Automatic => "auto",
                AgentServiceTier.Default => "default",
                AgentServiceTier.Flex => "flex",
                AgentServiceTier.Priority => "priority",
                _ => throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration),
            });
    }

    private void EnsureReasoningEffortSupport(AgentReasoningEffort effort)
    {
        if (!AiProviderReasoningPolicy.SupportedEfforts(profile, model).Contains(effort))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }

    private void WriteReasoning(
        Utf8JsonWriter writer,
        AgentReasoningEffort effort)
    {
        if (effort == AgentReasoningEffort.Automatic)
        {
            if (SupportsReasoningSummary())
            {
                writer.WriteStartObject("reasoning");
                writer.WriteString("summary", "detailed");
                writer.WriteEndObject();
            }

            return;
        }

        writer.WriteStartObject("reasoning");
        writer.WriteString(
            "effort",
            effort switch
            {
                AgentReasoningEffort.Off => "none",
                AgentReasoningEffort.Minimal => "minimal",
                AgentReasoningEffort.Low => "low",
                AgentReasoningEffort.Medium => "medium",
                AgentReasoningEffort.High => "high",
                AgentReasoningEffort.ExtraHigh => "xhigh",
                AgentReasoningEffort.Max => "max",
                _ => throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration),
            });
        if (effort != AgentReasoningEffort.Off && SupportsReasoningSummary())
        {
            writer.WriteString("summary", "detailed");
        }

        writer.WriteEndObject();
    }

    private bool SupportsReasoningSummary() =>
        !string.Equals(
            model,
            "gpt-5.3-codex-spark",
            StringComparison.OrdinalIgnoreCase);

    private void WriteInput(Utf8JsonWriter writer, AgentMessage message)
    {
        switch (message.Role)
        {
            case AgentMessageRole.System:
                return;
            case AgentMessageRole.Summary:
            case AgentMessageRole.User:
                WriteUserInput(writer, message);
                return;
            case AgentMessageRole.Assistant:
                if (message.ProviderReplayState is { } replayState)
                {
                    var binding = ReplayBinding();
                    if (replayState.Matches(binding))
                    {
                        WriteReplayAssistant(writer, message, replayState);
                    }
                    else if (replayState.MatchesRoute(binding))
                    {
                        WriteAssistantInput(writer, message);
                    }
                    else
                    {
                        throw AiProviderClientException.Create(
                            AiProviderRuntimeErrorCode.InvalidConfiguration);
                    }
                }
                else
                {
                    WriteAssistantInput(writer, message);
                }

                return;
            case AgentMessageRole.Tool when message.ToolResult is { } result:
                writer.WriteStartObject();
                writer.WriteString("type", "function_call_output");
                writer.WriteString("call_id", result.ProviderCallId);
                writer.WriteString("output", AiProviderJson.ToolResultContent(result));
                writer.WriteEndObject();
                return;
            default:
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }

    private static void WriteUserInput(Utf8JsonWriter writer, AgentMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteStartArray("content");
        if (message.Content.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "input_text");
            writer.WriteString("text", message.Content);
            writer.WriteEndObject();
        }

        foreach (var image in message.Images)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "input_image");
            writer.WriteString("image_url", DataUrl(image));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
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

    private static string DataUrl(AgentImageAttachment image) =>
        $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Content)}";

    private static void WriteAssistantInput(Utf8JsonWriter writer, AgentMessage message)
    {
        if (message.Content.Length > 0)
        {
            WriteMessage(writer, "assistant", message.Content);
        }

        foreach (var toolCall in message.ToolCalls)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function_call");
            writer.WriteString("call_id", toolCall.ProviderCallId);
            writer.WriteString("name", toolCall.ProviderName);
            writer.WriteString("arguments", toolCall.Arguments.GetRawText());
            writer.WriteEndObject();
        }
    }

    private void WriteReplayAssistant(
        Utf8JsonWriter writer,
        AgentMessage message,
        AgentProviderReplayState replayState)
    {
        if (replayState.Format != AgentProviderReplayFormat.OpenAiResponseItems)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        var replayedText = new StringBuilder();
        foreach (var item in replayState.Items)
        {
            using var document = AiProviderJson.Parse(
                Encoding.UTF8.GetBytes(item.PayloadJson));
            var value = document.RootElement;
            var type = AiProviderJson.RequiredBoundedString(value, "type", 64);
            var expectedType = item.Kind switch
            {
                AgentProviderReplayItemKind.OpenAiReasoning
                    or AgentProviderReplayItemKind.OpenAiReasoningWithSuppressedRaw =>
                    "reasoning",
                AgentProviderReplayItemKind.OpenAiMessage => "message",
                AgentProviderReplayItemKind.OpenAiFunctionCall => "function_call",
                _ => throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration),
            };
            if (!string.Equals(type, expectedType, StringComparison.Ordinal))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
            }

            if (item.ToolIndex is { } toolIndex)
            {
                ValidateReplayTool(value, message, toolIndex);
            }
            else if (item.Kind == AgentProviderReplayItemKind.OpenAiMessage)
            {
                replayedText.Append(ReadReplayMessageText(value));
            }

            if (profile.Identity == AiProviderKind.OpenAi
                && item.Kind is AgentProviderReplayItemKind.OpenAiReasoning
                    or AgentProviderReplayItemKind.OpenAiReasoningWithSuppressedRaw
                && !HasEncryptedReasoning(value))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
            }

            value.WriteTo(writer);
        }

        if (!string.Equals(
                replayedText.ToString(),
                message.Content,
                StringComparison.Ordinal))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }
    }

    private static string ReadReplayMessageText(JsonElement item)
    {
        var role = AiProviderJson.RequiredBoundedString(item, "role", 64);
        if (!string.Equals(role, "assistant", StringComparison.Ordinal))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        var content = AiProviderJson.RequiredArray(item, "content");
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
            }

            var type = AiProviderJson.RequiredBoundedString(part, "type", 64);
            var propertyName = type switch
            {
                "output_text" => "text",
                "refusal" => "refusal",
                _ => throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration),
            };
            text.Append(RequiredReplayText(
                part,
                propertyName,
                AgentProviderReplayState.MaximumItemBytes));
        }

        return text.ToString();
    }

    private static string RequiredReplayText(
        JsonElement parent,
        string propertyName,
        int maximumBytes)
    {
        if (!parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }

        var value = property.GetString()!;
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes
            || value.Any(character =>
                char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t'))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }

        return value;
    }

    private static bool HasEncryptedReasoning(JsonElement item) =>
        item.TryGetProperty("encrypted_content", out var encrypted)
        && encrypted.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(encrypted.GetString());

    private static void ValidateReplayTool(
        JsonElement item,
        AgentMessage message,
        int toolIndex)
    {
        if (toolIndex >= message.ToolCalls.Length)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        var proposal = message.ToolCalls[toolIndex];
        var callId = AiProviderJson.RequiredBoundedString(item, "call_id", 256);
        var name = AiProviderJson.RequiredBoundedString(item, "name", 128);
        var arguments = AiProviderJson.RequiredBoundedString(
            item,
            "arguments",
            AgentProviderReplayState.MaximumItemBytes);
        if (!string.Equals(callId, proposal.ProviderCallId, StringComparison.Ordinal)
            || !string.Equals(name, proposal.ProviderName, StringComparison.Ordinal))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        try
        {
            using var argumentsDocument = JsonDocument.Parse(arguments);
            if (!JsonElement.DeepEquals(
                    argumentsDocument.RootElement,
                    proposal.Arguments))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
            }
        }
        catch (JsonException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration,
                innerException: exception);
        }
    }

    private AgentProviderReplayBinding ReplayBinding() =>
        new(
            profile.Id,
            profile.Identity,
            profile.Protocol,
            model,
            ReplayEndpoint(),
            ReplayRouteIdentity());

    private Uri ReplayEndpoint() => (profile.Identity, profile.Authentication) switch
    {
        (AiProviderKind.OpenAi, AiProviderAuthentication.OAuth) =>
            OpenAiCodexEndpoint,
        (AiProviderKind.GitHubCopilot, AiProviderAuthentication.OAuth) =>
            AiProviderCatalog.Get(AiProviderKind.GitHubCopilot).DefaultEndpoint,
        _ => profile.Endpoint,
    };

    private string ReplayRouteIdentity()
    {
        var (route, credentialReference) =
            (profile.Identity, profile.Authentication) switch
            {
                (AiProviderKind.OpenAi, AiProviderAuthentication.ApiKey apiKey) =>
                    ("openai-responses-api-key", apiKey.Secret),
                (AiProviderKind.OpenAi, AiProviderAuthentication.OAuth oauth) =>
                    ("openai-codex-oauth", oauth.Session),
                (AiProviderKind.GitHubCopilot, AiProviderAuthentication.OAuth oauth) =>
                    ("github-copilot-oauth-responses", oauth.Session),
                (_, AiProviderAuthentication.ApiKey apiKey) =>
                    ("compatible-responses-api-key", apiKey.Secret),
                (_, AiProviderAuthentication.None) =>
                    ("responses-none", (SecretRef?)null),
                _ => ("responses-unsupported", (SecretRef?)null),
            };

        if (credentialReference is null)
        {
            return route;
        }

        // Persist only a one-way binding, never the vault capability reference.
        // Repointing a profile at another vault entry must invalidate old replay.
        var bindingInput = Encoding.UTF8.GetBytes(
            $"{profile.Id.Value}\0{credentialReference.Value.Value}");
        var digest = Convert.ToHexString(SHA256.HashData(bindingInput))
            .ToLowerInvariant();
        return $"{route}:{digest}";
    }

    private static void WriteMessage(
        Utf8JsonWriter writer,
        string role,
        string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString(
            "type",
string.Equals(role, "assistant", StringComparison.Ordinal) ? "output_text" : "input_text");
        writer.WriteString("text", content);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string SystemInstructions(AgentProviderRequest request) =>
        string.Join(
            "\n\n",
            request.Messages
                .Where(message => message.Role == AgentMessageRole.System)
                .Select(message => message.Content));

    private sealed class ResponsesStreamState(
        int maximumFragmentBytes,
        int maximumArgumentsBytes,
        bool requiresEncryptedReasoning)
    {
        private readonly List<ResponsesToolCall> _toolCalls = [];
        private readonly Dictionary<string, ResponsesToolCall> _toolCallsByItemId =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<int, ResponsesReplaySlot> _replaySlots = [];
        private readonly Dictionary<int, StringBuilder> _messageTextByOutputIndex = [];
        private readonly Dictionary<int, StringBuilder> _reasoningSummaryByOutputIndex = [];
        private readonly Dictionary<int, int> _reasoningSummaryBytesByOutputIndex = [];
        private readonly HashSet<int> _reasoningSummaryPartsAwaitingBreak = [];
        private AgentProviderStopReason? _stopReason;
        private bool _created;
        private bool _done;

        public IReadOnlyList<AgentProviderEvent> Apply(
            string eventType,
            JsonElement root)
        {
            if (_done || _stopReason is not null || root.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            var type = AiProviderJson.RequiredBoundedString(root, "type", 128);
            if (!string.Equals(eventType, "message", StringComparison.Ordinal)
                && !string.Equals(eventType, type, StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            var events = new List<AgentProviderEvent>();
            switch (type)
            {
                case "response.created":
                    if (_created)
                    {
                        throw ProtocolError();
                    }

                    _created = true;
                    break;
                case "response.queued":
                case "response.in_progress":
                    break;
                case "response.output_text.delta":
                case "response.refusal.delta":
                    AppendText(root, events);
                    break;
                case "response.output_item.added":
                    AddOutputItem(root, events);
                    break;
                case "response.function_call_arguments.delta":
                    AppendToolArguments(root, events);
                    break;
                case "response.function_call_arguments.done":
                    CompleteToolArguments(root, events);
                    break;
                case "response.output_item.done":
                    CompleteOutputItem(root, events);
                    break;
                case "response.completed":
                    CompleteResponse(root, incomplete: false, events);
                    break;
                case "response.incomplete":
                    CompleteResponse(root, incomplete: true, events);
                    break;
                case "response.failed":
                case "error":
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.ProviderUnavailable);
                case "response.content_part.added":
                case "response.content_part.done":
                case "response.output_text.done":
                case "response.refusal.done":
                case "response.reasoning_summary_part.added":
                case "response.reasoning_summary_text.done":
                    break;
                case "response.reasoning_summary_part.done":
                    CompleteReasoningSummaryPart(root);
                    break;
                case "response.reasoning_summary_text.delta":
                    AppendReasoningSummary(root, events);
                    break;
                case "response.reasoning_text.delta":
                    ObserveRawReasoning(root, "delta");
                    break;
                case "response.reasoning_text.done":
                    ObserveRawReasoning(root, "text");
                    break;
                default:
                    throw ProtocolError();
            }

            return events;
        }

        public void MarkDone()
        {
            if (_done)
            {
                throw ProtocolError();
            }

            _done = true;
        }

        public AgentProviderStopReason Complete()
        {
            if (!_created
                || _stopReason is null
                || _toolCalls.Any(toolCall => !toolCall.IsCompleted))
            {
                throw ProtocolError();
            }

            return _stopReason.Value;
        }

        public AgentProviderReplayState? BuildReplayState(
            AgentProviderReplayBinding binding)
        {
            if (_stopReason is null)
            {
                throw ProtocolError();
            }

            FinalizeSyntheticMessages();
            if (_replaySlots.Count == 0)
            {
                return null;
            }

            if (_replaySlots.Values.Any(slot => slot.PayloadJson is null))
            {
                throw ProtocolError();
            }

            if (!_replaySlots.Keys.SequenceEqual(
                    Enumerable.Range(0, _replaySlots.Count)))
            {
                throw ProtocolError();
            }

            ValidateFinalizedMessageText();
            if (requiresEncryptedReasoning
                && _replaySlots.Values.Any(slot =>
                    slot.IsReasoning && !slot.HasEncryptedReasoning))
            {
                throw ProtocolError();
            }

            var items = _replaySlots.Values
                .Select((slot, index) => new AgentProviderReplayItem(
                    index,
                    slot.ReplayKind,
                    slot.PayloadJson!,
                    slot.ToolIndex))
                .ToImmutableArray();
            return new AgentProviderReplayState(
                binding,
                AgentProviderReplayFormat.OpenAiResponseItems,
                items);
        }

        private void AppendText(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            var delta = RequiredFragment(root, "delta", maximumFragmentBytes);
            var outputIndex = OptionalOutputIndex(root)
                ?? FindOrCreateMessageSlot(root);
            if (!_replaySlots.TryGetValue(outputIndex, out var slot)
                || slot.Kind != AgentProviderReplayItemKind.OpenAiMessage)
            {
                throw ProtocolError();
            }

            if (!_messageTextByOutputIndex.TryGetValue(outputIndex, out var text))
            {
                text = new StringBuilder();
                _messageTextByOutputIndex.Add(outputIndex, text);
            }

            text.Append(delta);
            if (delta.Length > 0)
            {
                events.Add(new AgentProviderEvent.TextDelta(delta));
            }
        }

        private void ObserveRawReasoning(JsonElement root, string valueProperty)
        {
            var outputIndex = OutputIndex(root);
            if (!_replaySlots.TryGetValue(outputIndex, out var slot)
                || !slot.IsReasoning)
            {
                throw ProtocolError();
            }

            var itemId = RequiredIdentifier(root, "item_id", 256);
            if (!string.Equals(itemId, slot.Id, StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            _ = RequiredFragment(root, valueProperty, maximumFragmentBytes);
            slot.MarkSuppressedRawReasoning();
        }

        private void AppendReasoningSummary(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            var outputIndex = OutputIndex(root);
            var itemId = RequiredIdentifier(root, "item_id", 256);
            if (!_replaySlots.TryGetValue(outputIndex, out var slot)
                || !slot.IsReasoning
                || !string.Equals(itemId, slot.Id, StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            var delta = RequiredFragment(root, "delta", maximumFragmentBytes);
            if (_reasoningSummaryPartsAwaitingBreak.Remove(outputIndex))
            {
                AppendReasoningSummaryValue(outputIndex, "\n\n", events);
            }

            AppendReasoningSummaryValue(outputIndex, delta, events);
        }

        private void CompleteReasoningSummaryPart(JsonElement root)
        {
            var outputIndex = OutputIndex(root);
            var itemId = RequiredIdentifier(root, "item_id", 256);
            if (!_replaySlots.TryGetValue(outputIndex, out var slot)
                || !slot.IsReasoning
                || !string.Equals(itemId, slot.Id, StringComparison.Ordinal)
                || !_reasoningSummaryByOutputIndex.TryGetValue(outputIndex, out var summary)
                || summary.Length == 0)
            {
                throw ProtocolError();
            }

            // A summary part is a paragraph boundary, not a text fragment.
            // Delay the separator until the next delta so a completed final
            // part does not leave trailing blank lines in the durable message.
            if (!_reasoningSummaryPartsAwaitingBreak.Add(outputIndex))
            {
                throw ProtocolError();
            }
        }

        private void AppendFinalReasoningSummary(
            int outputIndex,
            JsonElement item,
            ICollection<AgentProviderEvent> events)
        {
            var finalSummary = ReadFinalReasoningSummary(item);
            if (finalSummary.Length == 0)
            {
                return;
            }

            var streamedSummary = _reasoningSummaryByOutputIndex.TryGetValue(
                outputIndex,
                out var streamed)
                ? streamed.ToString()
                : string.Empty;
            if (string.Equals(finalSummary, streamedSummary, StringComparison.Ordinal))
            {
                return;
            }

            // The final reasoning item is authoritative. Most routes stream the
            // summary, but the ChatGPT Codex route may expose it only here. When
            // a streamed prefix exists, append only the missing suffix so the
            // reducer never receives duplicate visible reasoning.
            if (!finalSummary.StartsWith(streamedSummary, StringComparison.Ordinal))
            {
                return;
            }

            AppendReasoningSummaryValue(
                outputIndex,
                finalSummary[streamedSummary.Length..],
                events);
        }

        private void AppendReasoningSummaryValue(
            int outputIndex,
            string value,
            ICollection<AgentProviderEvent> events)
        {
            if (value.Length == 0)
            {
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            var currentBytes = _reasoningSummaryBytesByOutputIndex.GetValueOrDefault(
                outputIndex);
            if (currentBytes > maximumArgumentsBytes - byteCount)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            if (!_reasoningSummaryByOutputIndex.TryGetValue(outputIndex, out var summary))
            {
                summary = new StringBuilder();
                _reasoningSummaryByOutputIndex.Add(outputIndex, summary);
            }

            summary.Append(value);
            _reasoningSummaryBytesByOutputIndex[outputIndex] = currentBytes + byteCount;
            AddBoundedReasoningEvents(value, events);
        }

        private void AddBoundedReasoningEvents(
            string value,
            ICollection<AgentProviderEvent> events)
        {
            var start = 0;
            var chunkLength = 0;
            var chunkBytes = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (rune.Utf8SequenceLength > maximumFragmentBytes)
                {
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.ResponseTooLarge);
                }

                if (chunkBytes > maximumFragmentBytes - rune.Utf8SequenceLength)
                {
                    events.Add(new AgentProviderEvent.ReasoningSummaryDelta(
                        value.Substring(start, chunkLength)));
                    start += chunkLength;
                    chunkLength = 0;
                    chunkBytes = 0;
                }

                chunkLength += rune.Utf16SequenceLength;
                chunkBytes += rune.Utf8SequenceLength;
            }

            if (start < value.Length)
            {
                events.Add(new AgentProviderEvent.ReasoningSummaryDelta(value[start..]));
            }
        }

        private string ReadFinalReasoningSummary(JsonElement item)
        {
            if (!item.TryGetProperty("summary", out var summary)
                || summary.ValueKind == JsonValueKind.Null)
            {
                return string.Empty;
            }

            if (summary.ValueKind != JsonValueKind.Array)
            {
                throw ProtocolError();
            }

            var text = new StringBuilder();
            var byteCount = 0;
            foreach (var part in summary.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object
                    || !string.Equals(AiProviderJson.RequiredBoundedString(part, "type", 64)
, "summary_text", StringComparison.Ordinal))
                {
                    throw ProtocolError();
                }

                var partText = RequiredFragment(
                    part,
                    "text",
                    maximumArgumentsBytes);
                var separatorBytes = text.Length == 0 ? 0 : 2;
                var partBytes = Encoding.UTF8.GetByteCount(partText);
                if (byteCount > maximumArgumentsBytes - separatorBytes - partBytes)
                {
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.ResponseTooLarge);
                }

                if (text.Length > 0)
                {
                    text.Append("\n\n");
                }

                text.Append(partText);
                byteCount += separatorBytes + partBytes;
            }

            return text.ToString();
        }

        private void AddOutputItem(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            var item = AiProviderJson.RequiredObject(root, "item");
            var type = AiProviderJson.RequiredBoundedString(item, "type", 64);
            var outputIndex = OutputIndex(root);
            if (outputIndex != _replaySlots.Count
                || _replaySlots.ContainsKey(outputIndex))
            {
                throw ProtocolError();
            }

            if (string.Equals(type, "reasoning", StringComparison.Ordinal))
            {
                _replaySlots.Add(
                    outputIndex,
                    ResponsesReplaySlot.Create(
                        AgentProviderReplayItemKind.OpenAiReasoning,
                        item));
                return;
            }

            if (string.Equals(type, "message", StringComparison.Ordinal))
            {
                _replaySlots.Add(
                    outputIndex,
                    ResponsesReplaySlot.Create(
                        AgentProviderReplayItemKind.OpenAiMessage,
                        item));
                return;
            }

            if (!string.Equals(type, "function_call", StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            var itemId = RequiredIdentifier(item, "id", 256);
            var callId = RequiredIdentifier(item, "call_id", 256);
            var name = RequiredIdentifier(item, "name", 128);
            if (_toolCallsByItemId.ContainsKey(itemId))
            {
                throw ProtocolError();
            }

            var toolCall = new ResponsesToolCall(
                _toolCalls.Count,
                maximumFragmentBytes,
                maximumArgumentsBytes);
            _toolCalls.Add(toolCall);
            _toolCallsByItemId.Add(itemId, toolCall);
            _replaySlots.Add(
                outputIndex,
                ResponsesReplaySlot.CreateTool(toolCall.Index, item));
            events.Add(new AgentProviderEvent.ToolCallStarted(
                toolCall.Index,
                callId,
                name));

            var arguments = OptionalFragment(
                item,
                "arguments",
                maximumFragmentBytes);
            if (!string.IsNullOrEmpty(arguments))
            {
                toolCall.Append(arguments, events);
            }
        }

        private void AppendToolArguments(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            var itemId = RequiredIdentifier(root, "item_id", 256);
            var toolCall = FindToolCall(itemId);
            var delta = RequiredFragment(root, "delta", maximumFragmentBytes);
            toolCall.Append(delta, events);
        }

        private void CompleteToolArguments(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            var itemId = RequiredIdentifier(root, "item_id", 256);
            var arguments = RequiredFragment(root, "arguments", maximumArgumentsBytes);
            FindToolCall(itemId).Complete(arguments, events);
        }

        private void CompleteOutputItem(
            JsonElement root,
            ICollection<AgentProviderEvent> events)
        {
            var item = AiProviderJson.RequiredObject(root, "item");
            var type = AiProviderJson.RequiredBoundedString(item, "type", 64);
            var outputIndex = OutputIndex(root);
            if (!_replaySlots.TryGetValue(outputIndex, out var replaySlot)
                || !replaySlot.Accepts(type))
            {
                throw ProtocolError();
            }

            replaySlot.Finalize(item);

            if (string.Equals(type, "reasoning", StringComparison.Ordinal))
            {
                AppendFinalReasoningSummary(outputIndex, item, events);
                return;
            }

            if (!string.Equals(type, "function_call", StringComparison.Ordinal))
            {
                return;
            }

            var itemId = RequiredIdentifier(item, "id", 256);
            var arguments = OptionalFragment(
                    item,
                    "arguments",
                    maximumArgumentsBytes)
                ?? string.Empty;
            FindToolCall(itemId).Complete(arguments, events);
        }

        private void CompleteResponse(
            JsonElement root,
            bool incomplete,
            ICollection<AgentProviderEvent> events)
        {
            var response = AiProviderJson.RequiredObject(root, "response");
            if (_toolCalls.Any(toolCall => !toolCall.IsCompleted))
            {
                throw ProtocolError();
            }

            if (!incomplete)
            {
                var status = AiProviderJson.OptionalBoundedString(response, "status", 64);
                if (status is not null && !string.Equals(status, "completed", StringComparison.Ordinal))
                {
                    throw ProtocolError();
                }

                BackfillReplayItems(response, events);
                _stopReason = _toolCalls.Count > 0
                    ? AgentProviderStopReason.ToolUse
                    : AgentProviderStopReason.EndTurn;
                AppendUsage(response, events);
                return;
            }

            var details = AiProviderJson.RequiredObject(response, "incomplete_details");
            BackfillReplayItems(response, events);
            _stopReason = AiProviderJson.RequiredBoundedString(details, "reason", 64) switch
            {
                "max_output_tokens" => AgentProviderStopReason.MaximumTokens,
                "content_filter" => AgentProviderStopReason.ContentFiltered,
                _ => throw ProtocolError(),
            };
            AppendUsage(response, events);
        }

        private void BackfillReplayItems(
            JsonElement response,
            ICollection<AgentProviderEvent> events)
        {
            if (!response.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var lastOutputIndex = -1;
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw ProtocolError();
                }

                var type = AiProviderJson.RequiredBoundedString(item, "type", 64);
                var id = AiProviderJson.RequiredBoundedString(item, "id", 256);
                var match = _replaySlots.SingleOrDefault(candidate =>
                    candidate.Value.Matches(type, id));
                if (match.Value is null || match.Key <= lastOutputIndex)
                {
                    throw ProtocolError();
                }

                lastOutputIndex = match.Key;
                var slot = match.Value;

                if (string.Equals(type, "reasoning", StringComparison.Ordinal))
                {
                    slot.BackfillEncryptedReasoning(item);
                    AppendFinalReasoningSummary(match.Key, item, events);
                }
                else if (slot.PayloadJson is null)
                {
                    slot.Finalize(item);
                }
            }
        }

        private static void AppendUsage(
            JsonElement response,
            ICollection<AgentProviderEvent> events)
        {
            if (!response.TryGetProperty("usage", out var usage)
                || usage.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (usage.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            var inputTokens = RequiredTokenCount(usage, "input_tokens");
            var outputTokens = RequiredTokenCount(usage, "output_tokens");
            var cachedInputTokens = OptionalDetailTokenCount(
                usage,
                "input_tokens_details",
                "cached_tokens");
            var reasoningTokens = OptionalDetailTokenCount(
                usage,
                "output_tokens_details",
                "reasoning_tokens");
            if (cachedInputTokens > inputTokens || reasoningTokens > outputTokens)
            {
                throw ProtocolError();
            }

            events.Add(new AgentProviderEvent.Usage(new AgentTokenUsage(
                inputTokens,
                outputTokens,
                cachedInputTokens,
                reasoningTokens)));
        }

        private static long OptionalDetailTokenCount(
            JsonElement usage,
            string detailName,
            string tokenName)
        {
            if (!usage.TryGetProperty(detailName, out var details)
                || details.ValueKind == JsonValueKind.Null)
            {
                return 0;
            }

            if (details.ValueKind != JsonValueKind.Object
                || !details.TryGetProperty(tokenName, out var tokenCount))
            {
                throw ProtocolError();
            }

            return TokenCount(tokenCount);
        }

        private static long RequiredTokenCount(JsonElement usage, string name)
        {
            if (!usage.TryGetProperty(name, out var value))
            {
                throw ProtocolError();
            }

            return TokenCount(value);
        }

        private static long TokenCount(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var tokenCount)
                || tokenCount is < 0 or > AgentTokenUsage.MaximumTokenCount)
            {
                throw ProtocolError();
            }

            return tokenCount;
        }

        private ResponsesToolCall FindToolCall(string itemId)
        {
            if (_toolCallsByItemId.TryGetValue(itemId, out var toolCall))
            {
                return toolCall;
            }

            throw ProtocolError();
        }

        private static int OutputIndex(JsonElement root)
        {
            if (!root.TryGetProperty("output_index", out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var index)
                || index < 0)
            {
                throw ProtocolError();
            }

            return index;
        }

        private static int? OptionalOutputIndex(JsonElement root)
        {
            if (!root.TryGetProperty("output_index", out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var index)
                || index < 0)
            {
                throw ProtocolError();
            }

            return index;
        }

        private int FindOrCreateMessageSlot(JsonElement root)
        {
            var existing = _replaySlots
                .SingleOrDefault(pair =>
                    pair.Value.Kind == AgentProviderReplayItemKind.OpenAiMessage);
            if (existing.Value is not null)
            {
                return existing.Key;
            }

            var itemId = RequiredIdentifier(root, "item_id", 256);
            var outputIndex = _replaySlots.Count == 0
                ? 0
                : checked(_replaySlots.Keys.Max() + 1);
            var initial = JsonSerializer.SerializeToElement(
                new OpenAiSyntheticMessage(
                    "message",
                    itemId,
                    "assistant",
                    "completed",
                    []),
                AgentProviderJsonContext.Default.OpenAiSyntheticMessage);
            _replaySlots.Add(
                outputIndex,
                ResponsesReplaySlot.Create(
                    AgentProviderReplayItemKind.OpenAiMessage,
                    initial));
            return outputIndex;
        }

        private void FinalizeSyntheticMessages()
        {
            foreach (var (outputIndex, text) in _messageTextByOutputIndex)
            {
                var slot = _replaySlots[outputIndex];
                if (slot.PayloadJson is not null)
                {
                    continue;
                }

                var item = JsonSerializer.SerializeToElement(
                    new OpenAiSyntheticMessage(
                        "message",
                        slot.Id,
                        "assistant",
                        "completed",
                        [new OpenAiSyntheticContent(
                            "output_text",
                            text.ToString(),
                            [])]),
                    AgentProviderJsonContext.Default.OpenAiSyntheticMessage);
                slot.Finalize(item);
            }
        }

        private void ValidateFinalizedMessageText()
        {
            foreach (var (outputIndex, slot) in _replaySlots)
            {
                if (slot.Kind != AgentProviderReplayItemKind.OpenAiMessage)
                {
                    continue;
                }

                var streamedText = _messageTextByOutputIndex.TryGetValue(
                    outputIndex,
                    out var value)
                    ? value.ToString()
                    : string.Empty;
                if (!string.Equals(
                        slot.ReadMessageText(),
                        streamedText,
                        StringComparison.Ordinal))
                {
                    throw ProtocolError();
                }
            }
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

        private static string RequiredFragment(
            JsonElement parent,
            string propertyName,
            int maximumBytes)
        {
            if (!parent.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            return value;
        }

        private static string? OptionalFragment(
            JsonElement parent,
            string propertyName,
            int maximumBytes)
        {
            if (!parent.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            return value;
        }
    }

    private sealed class ResponsesReplaySlot
    {
        private ResponsesReplaySlot(
            AgentProviderReplayItemKind kind,
            int? toolIndex,
            JsonElement initialItem)
        {
            Kind = kind;
            ToolIndex = toolIndex;
            Type = AiProviderJson.RequiredBoundedString(initialItem, "type", 64);
            Id = AiProviderJson.RequiredBoundedString(initialItem, "id", 256);
        }

        public AgentProviderReplayItemKind Kind { get; }

        public AgentProviderReplayItemKind ReplayKind =>
            IsReasoning && ContainsSuppressedRawReasoning
                ? AgentProviderReplayItemKind.OpenAiReasoningWithSuppressedRaw
                : Kind;

        public int? ToolIndex { get; }

        public string Type { get; }

        public string Id { get; private set; }

        public string? PayloadJson { get; private set; }

        public bool ContainsSuppressedRawReasoning { get; private set; }

        public bool IsReasoning =>
            Kind == AgentProviderReplayItemKind.OpenAiReasoning;

        public bool HasEncryptedReasoning { get; private set; }

        public static ResponsesReplaySlot Create(
            AgentProviderReplayItemKind kind,
            JsonElement initialItem) =>
            new(kind, toolIndex: null, initialItem);

        public static ResponsesReplaySlot CreateTool(
            int toolIndex,
            JsonElement initialItem) =>
            new(
                AgentProviderReplayItemKind.OpenAiFunctionCall,
                toolIndex,
                initialItem);

        public bool Accepts(string type) =>
            string.Equals(Type, type, StringComparison.Ordinal);

        public bool Matches(string type, string id) =>
            Accepts(type) && string.Equals(Id, id, StringComparison.Ordinal);

        public void Finalize(JsonElement item)
        {
            var type = AiProviderJson.RequiredBoundedString(item, "type", 64);
            var id = AiProviderJson.RequiredBoundedString(item, "id", 256);
            if (!Accepts(type)
                || Kind != AgentProviderReplayItemKind.OpenAiMessage
                    && !string.Equals(Id, id, StringComparison.Ordinal)
                || PayloadJson is not null)
            {
                throw ProtocolError();
            }

            Id = id;
            PayloadJson = AgentProviderReplayState.ValidateItemPayload(
                item.GetRawText());
            if (IsReasoning)
            {
                HasEncryptedReasoning = HasEncryptedReasoningValue(item);
                ContainsSuppressedRawReasoning |= HasRawReasoningContent(item);
            }
        }

        public void BackfillEncryptedReasoning(JsonElement item)
        {
            var type = AiProviderJson.RequiredBoundedString(item, "type", 64);
            var id = AiProviderJson.RequiredBoundedString(item, "id", 256);
            if (!IsReasoning || !Matches(type, id))
            {
                throw ProtocolError();
            }

            if (PayloadJson is null)
            {
                Finalize(item);
                return;
            }

            ContainsSuppressedRawReasoning |= HasRawReasoningContent(item);
            if (HasEncryptedReasoning)
            {
                return;
            }

            var encryptedContent = ReadEncryptedReasoning(item);
            if (encryptedContent is null)
            {
                return;
            }

            using var stored = JsonDocument.Parse(PayloadJson);
            var merged = AiProviderJson.Write(
                AgentProviderReplayState.MaximumItemBytes,
                writer =>
                {
                    writer.WriteStartObject();
                    foreach (var property in stored.RootElement.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }

                    writer.WriteString("encrypted_content", encryptedContent);
                    writer.WriteEndObject();
                });
            PayloadJson = AgentProviderReplayState.ValidateItemPayload(
                Encoding.UTF8.GetString(merged));
            HasEncryptedReasoning = true;
        }

        public void MarkSuppressedRawReasoning()
        {
            if (!IsReasoning)
            {
                throw ProtocolError();
            }

            ContainsSuppressedRawReasoning = true;
        }

        public string ReadMessageText()
        {
            if (Kind != AgentProviderReplayItemKind.OpenAiMessage
                || PayloadJson is null)
            {
                throw ProtocolError();
            }

            using var document = JsonDocument.Parse(PayloadJson);
            try
            {
                return ReadReplayMessageText(document.RootElement);
            }
            catch (AiProviderClientException exception)
            {
                throw ProtocolError(exception);
            }
        }

        private static bool HasRawReasoningContent(JsonElement item)
        {
            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                throw ProtocolError();
            }

            var hasRawContent = false;
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object
                    || !string.Equals(AiProviderJson.RequiredBoundedString(part, "type", 64)
, "reasoning_text", StringComparison.Ordinal))
                {
                    throw ProtocolError();
                }

                var text = AiProviderJson.RequiredBoundedString(
                    part,
                    "text",
                    AgentProviderReplayState.MaximumItemBytes);
                hasRawContent |= text.Length > 0;
            }

            return hasRawContent;
        }

        private static bool HasEncryptedReasoningValue(JsonElement item) =>
            ReadEncryptedReasoning(item) is not null;

        private static string? ReadEncryptedReasoning(JsonElement item)
        {
            if (!item.TryGetProperty("encrypted_content", out var encrypted)
                || encrypted.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (encrypted.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = encrypted.GetString()!;
            if (string.IsNullOrWhiteSpace(value)
                || Encoding.UTF8.GetByteCount(value)
                    > AgentProviderReplayState.MaximumItemBytes)
            {
                throw ProtocolError();
            }

            return value;
        }
    }

    private sealed class ResponsesToolCall(
        int index,
        int maximumFragmentBytes,
        int maximumArgumentsBytes)
    {
        private readonly StringBuilder _arguments = new();
        private int _argumentBytes;

        public int Index { get; } = index;

        public bool IsCompleted { get; private set; }

        public void Append(
            string value,
            ICollection<AgentProviderEvent> events)
        {
            if (IsCompleted || Encoding.UTF8.GetByteCount(value) > maximumFragmentBytes)
            {
                throw ProtocolError();
            }

            if (value.Length == 0)
            {
                return;
            }

            _argumentBytes = checked(_argumentBytes + Encoding.UTF8.GetByteCount(value));
            if (_argumentBytes > maximumArgumentsBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            _arguments.Append(value);
            events.Add(new AgentProviderEvent.ToolCallArgumentsDelta(Index, value));
        }

        public void Complete(
            string finalArguments,
            ICollection<AgentProviderEvent> events)
        {
            if (IsCompleted)
            {
                if (!string.Equals(
                        _arguments.ToString(),
                        finalArguments,
                        StringComparison.Ordinal))
                {
                    throw ProtocolError();
                }

                return;
            }

            if (_arguments.Length == 0)
            {
                Append(finalArguments.Length == 0 ? "{}" : finalArguments, events);
            }
            else if (!string.Equals(
                         _arguments.ToString(),
                         finalArguments,
                         StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            IsCompleted = true;
            events.Add(new AgentProviderEvent.ToolCallCompleted(Index));
        }
    }

    private static AiProviderClientException ProtocolError(Exception? innerException = null) =>
        AiProviderClientException.Create(
            AiProviderRuntimeErrorCode.ProtocolError,
            innerException: innerException);
}
