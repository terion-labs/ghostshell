using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

internal sealed class OpenAiCompatibleAgentProvider(
    AiProviderProfile profile,
    string model,
    AiProviderHttpTransport transport,
    AiProviderRuntimeLimits limits,
    AgentServiceTier serviceTier = AgentServiceTier.Automatic) : IAgentProvider
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
            "chat/completions",
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
        var state = new OpenAiStreamState(limits.MaximumProviderFragmentBytes);
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
                break;
            }

            using var document = AiProviderJson.Parse(item.Data);
            foreach (var providerEvent in state.Apply(document.RootElement))
            {
                yield return providerEvent;
            }
        }

        foreach (var providerEvent in state.Complete())
        {
            yield return providerEvent;
        }
    }

    private byte[] WriteRequest(AgentProviderRequest request) =>
        AiProviderJson.Write(
            limits.MaximumRequestBytes,
            writer =>
            {
                if (request.Messages.Any(message => message.ProviderReplayState is not null))
                {
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.InvalidConfiguration);
                }

                if (!profile.Capabilities.SupportsImageInput
                    && request.Messages.Any(message => message.Images.Length > 0))
                {
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.InvalidConfiguration);
                }

                if (request.ReasoningEffort != AgentReasoningEffort.Automatic)
                {
                    // This protocol is implemented by unrelated providers with
                    // incompatible reasoning controls. Never silently reinterpret
                    // a user-selected effort as a vendor-specific extension.
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.InvalidConfiguration);
                }

                writer.WriteStartObject();
                writer.WriteString("model", model);
                if (serviceTier != AgentServiceTier.Automatic)
                {
                    AiProviderServiceTierPolicy.EnsureSupported(
                        profile,
                        model,
                        serviceTier);
                    writer.WriteString(
                        "service_tier",
                        serviceTier switch
                        {
                            AgentServiceTier.Default => "default",
                            AgentServiceTier.Priority => "priority",
                            _ => throw AiProviderClientException.Create(
                                AiProviderRuntimeErrorCode.InvalidConfiguration),
                        });
                }
                writer.WriteBoolean("stream", true);
                writer.WriteStartArray("messages");
                foreach (var message in request.Messages)
                {
                    WriteMessage(writer, message);
                }

                writer.WriteEndArray();
                if (request.Tools.Length > 0)
                {
                    writer.WriteStartArray("tools");
                    foreach (var tool in request.Tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "function");
                        writer.WriteStartObject("function");
                        writer.WriteString("name", tool.ProviderName);
                        writer.WriteString("description", tool.Description);
                        writer.WritePropertyName("parameters");
                        tool.InputSchema.WriteTo(writer);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteString("tool_choice", "auto");
                }

                writer.WriteEndObject();
            });

    private static void WriteMessage(Utf8JsonWriter writer, AgentMessage message)
    {
        writer.WriteStartObject();
        switch (message.Role)
        {
            case AgentMessageRole.System:
                writer.WriteString("role", "system");
                writer.WriteString("content", message.Content);
                break;
            case AgentMessageRole.Summary:
            case AgentMessageRole.User:
                writer.WriteString("role", "user");
                if (message.Images.Length == 0)
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

                foreach (var image in message.Images)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString(
                        "url",
                        $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Content)}");
                    writer.WriteEndObject();
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

                if (message.Content.Length == 0)
                {
                    writer.WriteNull("content");
                }
                else
                {
                    writer.WriteString("content", message.Content);
                }

                writer.WriteStartArray("tool_calls");
                foreach (var toolCall in message.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", toolCall.ProviderCallId);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString(
                        "name",
                        toolCall.ProviderName);
                    writer.WriteString(
                        "arguments",
                        toolCall.Arguments.GetRawText());
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case AgentMessageRole.Tool when message.ToolResult is { } result:
                writer.WriteString("role", "tool");
                writer.WriteString("tool_call_id", result.ProviderCallId);
                writer.WriteString(
                    "content",
                    AiProviderJson.ToolResultContent(result));
                break;
            default:
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.InvalidConfiguration);
        }

        writer.WriteEndObject();
    }

    private sealed class OpenAiStreamState(int maximumFragmentBytes)
    {
        private readonly List<OpenAiToolCall> _toolCalls = [];
        private AgentProviderStopReason? _stopReason;
        private AgentTokenUsage? _usage;
        private bool _done;

        public IReadOnlyList<AgentProviderEvent> Apply(JsonElement root)
        {
            if (_done || root.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            var hasUsage = ReadUsage(root);
            var choices = AiProviderJson.RequiredArray(root, "choices");
            using var enumerator = choices.EnumerateArray();
            if (!enumerator.MoveNext())
            {
                return hasUsage && _stopReason is not null
                    ? []
                    : throw ProtocolError();
            }

            if (_stopReason is not null)
            {
                throw ProtocolError();
            }

            var choice = enumerator.Current;
            if (enumerator.MoveNext() || choice.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            if (choice.TryGetProperty("index", out var choiceIndex)
                && (choiceIndex.ValueKind != JsonValueKind.Number
                    || !choiceIndex.TryGetInt32(out var parsedChoiceIndex)
                    || parsedChoiceIndex != 0))
            {
                throw ProtocolError();
            }

            var events = new List<AgentProviderEvent>();
            if (choice.TryGetProperty("delta", out var delta)
                && delta.ValueKind != JsonValueKind.Null)
            {
                if (delta.ValueKind != JsonValueKind.Object)
                {
                    throw ProtocolError();
                }

                AppendText(delta, "content", events);
                AppendText(delta, "refusal", events);
                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind != JsonValueKind.Null)
                {
                    if (toolCalls.ValueKind != JsonValueKind.Array)
                    {
                        throw ProtocolError();
                    }

                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        ApplyToolCall(toolCall, events);
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind != JsonValueKind.Null)
            {
                if (finishReason.ValueKind != JsonValueKind.String)
                {
                    throw ProtocolError();
                }

                var parsed = finishReason.GetString() switch
                {
                    "stop" => AgentProviderStopReason.EndTurn,
                    "tool_calls" => AgentProviderStopReason.ToolUse,
                    "length" => AgentProviderStopReason.MaximumTokens,
                    "content_filter" => AgentProviderStopReason.ContentFiltered,
                    _ => throw ProtocolError(),
                };
                if (_stopReason is not null && _stopReason != parsed)
                {
                    throw ProtocolError();
                }

                _stopReason = parsed;
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

        public IReadOnlyList<AgentProviderEvent> Complete()
        {
            if (!_done || _stopReason is null)
            {
                throw ProtocolError();
            }

            var events = new List<AgentProviderEvent>();
            foreach (var toolCall in _toolCalls)
            {
                toolCall.Complete(events);
            }

            if (_usage is not null)
            {
                events.Add(new AgentProviderEvent.Usage(_usage));
            }

            events.Add(new AgentProviderEvent.ResponseCompleted(_stopReason.Value));
            return events;
        }

        private bool ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage)
                || usage.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            if (_usage is not null || usage.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            var input = RequiredTokenCount(usage, "prompt_tokens");
            var output = RequiredTokenCount(usage, "completion_tokens");
            var cached = OptionalDetailTokenCount(
                usage,
                "prompt_tokens_details",
                "cached_tokens");
            var reasoning = OptionalDetailTokenCount(
                usage,
                "completion_tokens_details",
                "reasoning_tokens");
            if (cached > input || reasoning > output)
            {
                throw ProtocolError();
            }

            _usage = new AgentTokenUsage(input, output, cached, reasoning);
            return true;
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
                || !details.TryGetProperty(tokenName, out var value))
            {
                throw ProtocolError();
            }

            return TokenCount(value);
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
                || !value.TryGetInt64(out var count)
                || count is < 0 or > AgentTokenUsage.MaximumTokenCount)
            {
                throw ProtocolError();
            }

            return count;
        }

        private void AppendText(
            JsonElement delta,
            string propertyName,
            ICollection<AgentProviderEvent> events)
        {
            if (!delta.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (value.Length > 0)
            {
                ValidateFragment(value, maximumFragmentBytes);
                events.Add(new AgentProviderEvent.TextDelta(value));
            }
        }

        private void ApplyToolCall(
            JsonElement toolCall,
            ICollection<AgentProviderEvent> events)
        {
            if (toolCall.ValueKind != JsonValueKind.Object
                || !toolCall.TryGetProperty("index", out var indexProperty)
                || indexProperty.ValueKind != JsonValueKind.Number
                || !indexProperty.TryGetInt32(out var index)
                || index < 0
                || index > _toolCalls.Count)
            {
                throw ProtocolError();
            }

            if (index == _toolCalls.Count)
            {
                _toolCalls.Add(new OpenAiToolCall(index));
            }

            var state = _toolCalls[index];
            state.AcceptIdentity(
                AiProviderJson.OptionalBoundedString(toolCall, "id", 256),
                ReadFunctionString(toolCall, "name", 128));
            var arguments = ReadFunctionString(
                toolCall,
                "arguments",
                maximumFragmentBytes,
                allowControls: true);
            state.AcceptArguments(arguments, events);
        }

        private static string? ReadFunctionString(
            JsonElement toolCall,
            string propertyName,
            int maximumLength,
            bool allowControls = false)
        {
            if (!toolCall.TryGetProperty("function", out var function)
                || function.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (function.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError();
            }

            if (!function.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw ProtocolError();
            }

            var value = property.GetString()!;
            if (Encoding.UTF8.GetByteCount(value) > maximumLength
                || (!allowControls && value.Any(char.IsControl)))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            return value;
        }

        private static void ValidateFragment(string value, int maximumBytes)
        {
            if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }
        }
    }

    private sealed class OpenAiToolCall(int index)
    {
        private string? _id;
        private string? _name;
        private bool _started;
        private bool _hasArguments;

        public void AcceptIdentity(string? id, string? name)
        {
            _id = MergeStable(_id, id);
            _name = MergeStable(_name, name);
        }

        public void AcceptArguments(
            string? arguments,
            ICollection<AgentProviderEvent> events)
        {
            if (arguments is null)
            {
                return;
            }

            EnsureStarted(events);
            if (arguments.Length > 0)
            {
                events.Add(new AgentProviderEvent.ToolCallArgumentsDelta(index, arguments));
                _hasArguments = true;
            }
        }

        public void Complete(ICollection<AgentProviderEvent> events)
        {
            EnsureStarted(events);
            if (!_hasArguments)
            {
                throw ProtocolError();
            }

            events.Add(new AgentProviderEvent.ToolCallCompleted(index));
        }

        private void EnsureStarted(ICollection<AgentProviderEvent> events)
        {
            if (_started)
            {
                return;
            }

            if (_id is null || _name is null)
            {
                throw ProtocolError();
            }

            events.Add(new AgentProviderEvent.ToolCallStarted(index, _id, _name));
            _started = true;
        }

        private static string? MergeStable(string? current, string? incoming)
        {
            if (incoming is null)
            {
                return current;
            }

            if (current is not null && !string.Equals(current, incoming, StringComparison.Ordinal))
            {
                throw ProtocolError();
            }

            return incoming;
        }
    }

    private static AiProviderClientException ProtocolError() =>
        AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
}
