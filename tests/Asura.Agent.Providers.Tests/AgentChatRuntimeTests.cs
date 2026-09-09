using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Asura.Infrastructure;

namespace Asura.Agent.Providers.Tests;

public sealed class AgentChatRuntimeTests
{
    private static readonly DateTimeOffset StoredAt =
        new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TextStreamPublishesProvisionalChangesBeforeAtomicCommit()
    {
        using var vault = new InMemorySecretVault();
        var profile = Profile("provider-one", "Provider one");
        using var stream = new GatedReadStream(
            OpenAiTextDelta("Hel"),
            OpenAiTextDelta("lo") + OpenAiFinished());
        using var handler = new ChatHttpMessageHandler(
            (_, _) => Task.FromResult(StreamingResponse(stream)));
        using var runtime = CreateRuntime(vault, handler, profile);
        var snapshots = new ConcurrentQueue<AgentChatSnapshot>();
        var provisionalSeen = new TaskCompletionSource<AgentChatSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Changed += (_, _) =>
        {
            var snapshot = runtime.Snapshot;
            snapshots.Enqueue(snapshot);
            if (string.Equals(snapshot.ProvisionalAssistantText, "Hel", StringComparison.Ordinal))
            {
                provisionalSeen.TrySetResult(snapshot);
            }
        };

        var send = runtime.SendAsync(
            profile.Id,
            "Say hello.",
            CancellationToken.None).AsTask();
        var provisional = await provisionalSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AgentChatState.Streaming, provisional.State);
        Assert.Equal(profile.Id, provisional.ProviderId);
        Assert.Equal("Hel", provisional.ProvisionalAssistantText);
        Assert.Collection(
            provisional.Messages,
            message => Assert.Equal(
                new AgentChatMessage(AgentChatMessageRole.User, "Say hello."),
                message));
        stream.Release();

        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal("agent_chat_completed", result.Code);
        Assert.Equal(AgentChatState.Ready, runtime.Snapshot.State);
        Assert.Equal(string.Empty, runtime.Snapshot.ProvisionalAssistantText);
        Assert.Collection(
            runtime.Snapshot.Messages,
            message => Assert.Equal(
                new AgentChatMessage(AgentChatMessageRole.User, "Say hello."),
                message),
            message => Assert.Equal(
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "Hello",
                    RequestedReasoningEffort: AgentReasoningEffort.Automatic),
                message));
        Assert.Contains(
            snapshots,
            snapshot => snapshot.State == AgentChatState.Streaming
                && string.Equals(snapshot.ProvisionalAssistantText, "Hel", StringComparison.Ordinal));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConsecutiveTurnsPreserveConversationAndNeverSendTools()
    {
        using var vault = new InMemorySecretVault();
        var profile = Profile("provider-one", "Provider one");
        var responses = new Queue<HttpResponseMessage>(
        [
            SseResponse(OpenAiTextStream("first answer")),
            SseResponse(OpenAiTextStream("second answer")),
        ]);
        using var handler = new ChatHttpMessageHandler(
            (_, _) => Task.FromResult(responses.Dequeue()));
        using var runtime = CreateRuntime(vault, handler, profile);

        var first = await runtime.SendAsync(
            profile.Id,
            "first question",
            CancellationToken.None);
        var second = await runtime.SendAsync(
            profile.Id,
            "second question",
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Collection(
            runtime.Snapshot.Messages,
            message => Assert.Equal(
                new AgentChatMessage(AgentChatMessageRole.User, "first question"),
                message),
            message => Assert.Equal(
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "first answer",
                    RequestedReasoningEffort: AgentReasoningEffort.Automatic),
                message),
            message => Assert.Equal(
                new AgentChatMessage(AgentChatMessageRole.User, "second question"),
                message),
            message => Assert.Equal(
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "second answer",
                    RequestedReasoningEffort: AgentReasoningEffort.Automatic),
                message));

        var secondRequest = handler.Requests[1];
        using var body = JsonDocument.Parse(secondRequest.Body);
        var root = body.RootElement;
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("tool_choice", out _));
        Assert.Collection(
            root.GetProperty("messages").EnumerateArray(),
            message =>
            {
                Assert.Equal("system", message.GetProperty("role").GetString());
                var systemPrompt = message.GetProperty("content").GetString()!;
                Assert.Contains("chat-only", systemPrompt, StringComparison.Ordinal);
                Assert.Contains("no tools", systemPrompt, StringComparison.Ordinal);
                Assert.Contains("no access to terminals", systemPrompt, StringComparison.Ordinal);
            },
            message => AssertMessage(message, "user", "first question"),
            message => AssertMessage(message, "assistant", "first answer"),
            message => AssertMessage(message, "user", "second question"));
    }

    [Fact]
    public async Task ProviderToolCallCannotEscalateChatIntoToolAuthority()
    {
        using var vault = new InMemorySecretVault();
        var profile = Profile("provider-one", "Provider one");
        using var handler = new ChatHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(OpenAiToolStream())));
        using var runtime = CreateRuntime(vault, handler, profile);

        var result = await runtime.SendAsync(
            profile.Id,
            "Run a terminal command.",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_chat_failed", result.Code);
        Assert.Equal(AgentChatState.Failed, runtime.Snapshot.State);
        Assert.Empty(runtime.Snapshot.Messages);
        Assert.Equal(string.Empty, runtime.Snapshot.ProvisionalAssistantText);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.False(body.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task SwitchingProviderRequiresClearAndClearResetsNativeConversation()
    {
        using var vault = new InMemorySecretVault();
        var firstProfile = Profile("provider-one", "Provider one", order: 0);
        var secondProfile = Profile("provider-two", "Provider two", order: 1);
        var responses = new Queue<HttpResponseMessage>(
        [
            SseResponse(OpenAiTextStream("first answer")),
            SseResponse(OpenAiTextStream("fresh answer")),
        ]);
        using var handler = new ChatHttpMessageHandler(
            (_, _) => Task.FromResult(responses.Dequeue()));
        using var runtime = CreateRuntime(vault, handler, firstProfile, secondProfile);

        var first = await runtime.SendAsync(
            firstProfile.Id,
            "first question",
            CancellationToken.None);
        var rejected = await runtime.SendAsync(
            secondProfile.Id,
            "cross-provider question",
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(rejected.IsSuccess);
        Assert.Equal("agent_chat_provider_changed", rejected.Code);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(firstProfile.Id, runtime.Snapshot.ProviderId);
        Assert.True(runtime.Clear());
        Assert.Equal(AgentChatState.Ready, runtime.Snapshot.State);
        Assert.Null(runtime.Snapshot.ProviderId);
        Assert.Empty(runtime.Snapshot.Messages);
        Assert.Equal(string.Empty, runtime.Snapshot.ProvisionalAssistantText);

        var afterClear = await runtime.SendAsync(
            secondProfile.Id,
            "fresh question",
            CancellationToken.None);

        Assert.True(afterClear.IsSuccess);
        Assert.Equal(secondProfile.Id, runtime.Snapshot.ProviderId);
        Assert.Collection(
            runtime.Snapshot.Messages,
            message => Assert.Equal(
                new AgentChatMessage(AgentChatMessageRole.User, "fresh question"),
                message),
            message => Assert.Equal(
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "fresh answer",
                    RequestedReasoningEffort: AgentReasoningEffort.Automatic),
                message));
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Collection(
            body.RootElement.GetProperty("messages").EnumerateArray(),
            message => Assert.Equal("system", message.GetProperty("role").GetString()),
            message => AssertMessage(message, "user", "fresh question"));
    }

    [Fact]
    public async Task CancellationRollsBackWholeTurnAndDiscardsProvisionalText()
    {
        using var vault = new InMemorySecretVault();
        var profile = Profile("provider-one", "Provider one");
        using var cancelledStream = new GatedReadStream(
            OpenAiTextDelta("partial"),
            OpenAiFinished());
        var responses = new Queue<HttpResponseMessage>(
        [
            SseResponse(OpenAiTextStream("committed answer")),
            StreamingResponse(cancelledStream),
        ]);
        using var handler = new ChatHttpMessageHandler(
            (_, _) => Task.FromResult(responses.Dequeue()));
        using var runtime = CreateRuntime(vault, handler, profile);
        var first = await runtime.SendAsync(
            profile.Id,
            "committed question",
            CancellationToken.None);
        Assert.True(first.IsSuccess);
        var committedMessages = runtime.Snapshot.Messages.ToArray();
        var observedStates = new ConcurrentQueue<AgentChatState>();
        var provisionalSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Changed += (_, _) =>
        {
            observedStates.Enqueue(runtime.Snapshot.State);
            if (string.Equals(runtime.Snapshot.ProvisionalAssistantText, "partial", StringComparison.Ordinal))
            {
                provisionalSeen.TrySetResult();
            }
        };

        var send = runtime.SendAsync(
            profile.Id,
            "cancelled question",
            CancellationToken.None).AsTask();
        await provisionalSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.Cancel());
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_chat_cancelled", result.Code);
        Assert.Contains(AgentChatState.Cancelling, observedStates);
        Assert.Equal(AgentChatState.Cancelled, runtime.Snapshot.State);
        Assert.Equal(committedMessages, runtime.Snapshot.Messages.ToArray());
        Assert.Equal(string.Empty, runtime.Snapshot.ProvisionalAssistantText);
        Assert.True(runtime.Clear());
        Assert.Equal(AgentChatState.Ready, runtime.Snapshot.State);
        Assert.Empty(runtime.Snapshot.Messages);
    }

    [Fact]
    public async Task ProviderFailureUsesStableSanitizedStatusAndRollsBackTurn()
    {
        const string sentinel = "provider-private-diagnostic";
        using var vault = new InMemorySecretVault();
        var profile = Profile("provider-one", "Provider one");
        using var handler = new ChatHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    $"{{\"error\":\"{sentinel}\"}}",
                    Encoding.UTF8,
                    "application/json"),
            }));
        using var runtime = CreateRuntime(vault, handler, profile);

        var result = await runtime.SendAsync(
            profile.Id,
            "failed question",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_chat_failed", result.Code);
        Assert.DoesNotContain(sentinel, result.Message, StringComparison.Ordinal);
        Assert.Equal(AgentChatState.Failed, runtime.Snapshot.State);
        Assert.Empty(runtime.Snapshot.Messages);
        Assert.Equal(string.Empty, runtime.Snapshot.ProvisionalAssistantText);
        Assert.DoesNotContain(
            sentinel,
            runtime.Snapshot.Status,
            StringComparison.Ordinal);
        Assert.Equal(
            "The provider request failed. Review its endpoint and credential.",
            runtime.Snapshot.Status);
    }

    [Fact]
    public async Task ConcurrentSendIsRejectedAsBusyAndClearWaitsForCancellation()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var vault = new InMemorySecretVault();
        var profile = Profile("provider-one", "Provider one");
        using var handler = new ChatHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SseResponse(OpenAiTextStream("late answer"));
            });
        using var runtime = CreateRuntime(vault, handler, profile);

        var active = runtime.SendAsync(
            profile.Id,
            "first question",
            CancellationToken.None).AsTask();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rejected = await runtime.SendAsync(
            profile.Id,
            "second question",
            CancellationToken.None);

        Assert.False(rejected.IsSuccess);
        Assert.Equal("agent_chat_busy", rejected.Code);
        Assert.Equal(1, handler.CallCount);
        Assert.False(runtime.Clear());
        Assert.True(runtime.Cancel());
        var cancelled = await active.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cancelled.IsSuccess);
        Assert.Equal("agent_chat_cancelled", cancelled.Code);
        Assert.Empty(runtime.Snapshot.Messages);
        Assert.True(runtime.Clear());
    }

    [Fact]
    public async Task DisposeIsIdempotentCancelsAuthorityAndRejectsFurtherOperations()
    {
        using var vault = new InMemorySecretVault();
        var profile = Profile("provider-one", "Provider one");
        using var handler = new ChatHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(OpenAiTextStream("unused"))));
        var runtime = CreateRuntime(vault, handler, profile);

        runtime.Dispose();
        runtime.Dispose();

        Assert.True(handler.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => runtime.Clear());
        Assert.Throws<ObjectDisposedException>(() => runtime.Cancel());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => runtime.SendAsync(
                profile.Id,
                "after dispose",
                CancellationToken.None).AsTask());
        Assert.Equal(0, handler.CallCount);
    }

    private static CatalogAiProviderRuntime CreateRuntime(
        ISecretVault vault,
        HttpMessageHandler handler,
        params AiProviderProfile[] profiles)
    {
        var factory = new AiProviderFactory(vault, handler);
        return new CatalogAiProviderRuntime(
            new FixedDefinitionCatalog(Snapshot(profiles)),
            factory);
    }

    private static AiProviderProfile Profile(
        string id,
        string name,
        int order = 0) =>
        new(
            new AiProviderProfileId(id),
            AiProviderProfile.CurrentSchemaVersion,
            name,
            AiProviderKind.OpenAiCompatible,
            new Uri("http://127.0.0.1:4242/v1/"),
            new AiProviderAuthentication.None(),
            "test-model",
            order);

    private static DefinitionCatalogSnapshot Snapshot(
        params AiProviderProfile[] profiles) =>
        DefinitionCatalogSnapshot.Empty with
        {
            AiProviderProfiles = [.. profiles
                .Select(profile => new StoredDefinition<AiProviderProfile>(
                    profile,
                    1,
                    StoredAt,
                    StoredAt))],
        };

    private static HttpResponseMessage SseResponse(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "text/event-stream"),
        };

    private static HttpResponseMessage StreamingResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static string OpenAiTextStream(string value) =>
        OpenAiTextDelta(value) + OpenAiFinished();

    private static string OpenAiTextDelta(string value) =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":"
        + JsonSerializer.Serialize(value)
        + "},\"finish_reason\":null}]}\n\n";

    private static string OpenAiFinished() =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{},"
        + "\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static string OpenAiToolStream() =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{"
        + "\"index\":0,\"id\":\"call-terminal\",\"type\":\"function\","
        + "\"function\":{\"name\":\"run_terminal\",\"arguments\":\"{}\"}}]},"
        + "\"finish_reason\":null}]}\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},"
        + "\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static void AssertMessage(
        JsonElement message,
        string role,
        string content)
    {
        Assert.Equal(role, message.GetProperty("role").GetString());
        Assert.Equal(content, message.GetProperty("content").GetString());
    }

    private sealed class ChatHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<CapturedRequest> _requests = [];

        public bool IsDisposed { get; private set; }

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _requests.Count;
                }
            }
        }

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            lock (_gate)
            {
                _requests.Add(new CapturedRequest(request.RequestUri!, body));
            }

            var response = await respond(request, cancellationToken).ConfigureAwait(false);
            response.RequestMessage ??= request;
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed record CapturedRequest(Uri Uri, string Body);

    private sealed class GatedReadStream : Stream
    {
        private readonly byte[] _first;
        private readonly byte[] _second;
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _firstPosition;
        private int _secondPosition;

        public GatedReadStream(string first, string second)
        {
            _first = Encoding.UTF8.GetBytes(first);
            _second = Encoding.UTF8.GetBytes(second);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _released.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_firstPosition < _first.Length)
            {
                return Copy(_first, ref _firstPosition, buffer.Span);
            }

            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _secondPosition < _second.Length
                ? Copy(_second, ref _secondPosition, buffer.Span)
                : 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _released.TrySetCanceled();
            base.Dispose(disposing);
        }

        private static int Copy(
            byte[] source,
            ref int position,
            Span<byte> destination)
        {
            var length = Math.Min(source.Length - position, destination.Length);
            source.AsSpan(position, length).CopyTo(destination);
            position += length;
            return length;
        }
    }

    private sealed class FixedDefinitionCatalog(
        DefinitionCatalogSnapshot snapshot)
        : IDefinitionCatalog
    {
        public DefinitionCatalogSnapshot Snapshot { get; } = snapshot;

        public event EventHandler? Changed;

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
            ConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
            LayoutDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
            ScreenDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
            WorkspaceDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
            ThemePreference definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
            TerminalProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
            KeymapProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
            FileProviderProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
            AiProviderProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
            McpServerProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
            QuickTerminalSettings definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
