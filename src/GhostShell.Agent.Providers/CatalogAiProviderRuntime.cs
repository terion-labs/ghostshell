using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Providers;

public sealed class CatalogAiProviderRuntime :
    IAiProviderProfileRuntime,
    IAgentChatRuntime
{
    private const int MaximumPromptLength = 64 * 1024;
    private const string ChatSystemPrompt =
        "You are GhostSHELL's chat-only assistant. You have no tools and no access to terminals, files, processes, browsers, sessions, credentials, or remote machines. Never claim that you performed an action.";
    private static readonly ImmutableArray<AiProviderModelDescriptor> OpenAiCodexModels =
    [
        new("gpt-5.6-sol", "GPT-5.6 Sol", contextWindowTokens: 272_000),
        new("gpt-5.6-terra", "GPT-5.6 Terra", contextWindowTokens: 272_000),
        new("gpt-5.6-luna", "GPT-5.6 Luna", contextWindowTokens: 272_000),
        new("gpt-5.5", "GPT-5.5", contextWindowTokens: 272_000),
        new("gpt-5.4", "GPT-5.4", contextWindowTokens: 272_000),
        new("gpt-5.4-mini", "GPT-5.4 mini", contextWindowTokens: 272_000),
        new("gpt-5.3-codex", "GPT-5.3 Codex", contextWindowTokens: 272_000),
        new("gpt-5.3-codex-spark", "GPT-5.3 Codex Spark", contextWindowTokens: 128_000),
    ];

    private readonly object _gate = new();
    private readonly IDefinitionCatalog _catalog;
    private readonly AiProviderFactory _factory;
    private readonly Func<Uri, AiProviderFactory>? _routedFactoryFactory;
    private readonly Dictionary<string, AiProviderFactory> _routedFactories = [];
    private readonly Dictionary<AiProviderProfileId, IReadOnlyList<AiProviderModelDescriptor>>
        _discoveredModels = [];
    private IReadOnlyList<AiProviderProfileDescriptor> _profiles = [];
    private IReadOnlyList<AiProviderRuntimeDiagnostic> _diagnostics = [];
    private NativeAgentSession _chatSession = CreateChatSession();
    private CancellationTokenSource? _chatTurnCancellation;
    private AgentChatSnapshot _chatSnapshot = EmptyChatSnapshot();
    private bool _disposed;

    public CatalogAiProviderRuntime(
        IDefinitionCatalog catalog,
        ISecretVault secretVault,
        AiProviderRuntimeLimits? limits = null,
        AiProviderOAuthOptions? oauthOptions = null)
        : this(
            catalog,
            new AiProviderFactory(
                secretVault ?? throw new ArgumentNullException(nameof(secretVault)),
                limits,
                oauthOptions),
            proxy => new AiProviderFactory(
                secretVault,
                AiProviderHttpTransport.CreateHandler(CreateWebProxy(proxy)),
                limits,
                oauthOptions))
    {
    }

    internal CatalogAiProviderRuntime(
        IDefinitionCatalog catalog,
        AiProviderFactory factory)
        : this(catalog, factory, routedFactoryFactory: null)
    {
    }

    private CatalogAiProviderRuntime(
        IDefinitionCatalog catalog,
        AiProviderFactory factory,
        Func<Uri, AiProviderFactory>? routedFactoryFactory)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _routedFactoryFactory = routedFactoryFactory;
        _catalog.Changed += OnCatalogChanged;
        Refresh(_catalog.Snapshot);
    }

    public event EventHandler? ProfilesChanged;

    public event EventHandler? Changed;

    public IReadOnlyList<AiProviderProfileDescriptor> Profiles
    {
        get
        {
            lock (_gate)
            {
                return _profiles;
            }
        }
    }

    public IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics;
            }
        }
    }

    public AgentChatSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _chatSnapshot;
            }
        }
    }

    public async ValueTask<AiProviderTestResult> TestAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var models = profile.Identity == AiProviderKind.OpenAi
                && profile.Authentication is AiProviderAuthentication.OAuth
                    ? await _factory.ListOpenAiCodexModelsAsync(
                        profile,
                        cancellationToken).ConfigureAwait(false)
                    : await _factory.ListModelsAsync(profile, cancellationToken)
                        .ConfigureAwait(false);
            if (!models.Any(model =>
                    string.Equals(
                        model.Id,
                        profile.DefaultModel,
                        StringComparison.Ordinal)))
            {
                return new AiProviderTestResult(
                    false,
                    "ai_provider_model_unavailable",
                    "The endpoint is reachable, but the configured default model was not returned.",
                    models,
                    AiProviderRuntimeErrorCode.ModelUnavailable);
            }

            return new AiProviderTestResult(
                true,
                "ai_provider_test_succeeded",
                $"Connected to {profile.Name}; {models.Count} model(s) are available.",
                models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.Cancelled));
        }
        catch (AiProviderClientException exception)
        {
            return Failure(exception);
        }
        catch (ArgumentException)
        {
            return Failure(AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProviderUnavailable));
        }
    }

    public async ValueTask<AiProviderModelDiscoveryResult> DiscoverModelsAsync(
        AiProviderProfileId profileId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var profile = _catalog.Snapshot.AiProviderProfiles
            .Select(stored => stored.Value)
            .SingleOrDefault(candidate => candidate.Id == profileId);
        if (profile is null || !profile.IsEnabled)
        {
            return new AiProviderModelDiscoveryResult(
                false,
                "ai_provider_profile_unavailable",
                "The selected AI-provider profile is unavailable.",
                []);
        }

        try
        {
            IReadOnlyList<AiProviderModelDescriptor> models;
            if (profile.Identity == AiProviderKind.OpenAi
                && profile.Authentication is AiProviderAuthentication.OAuth)
            {
                models = await _factory.ListOpenAiCodexModelsAsync(
                    profile,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                models = await _factory.ListModelsAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (models.All(model => !string.Equals(
                    model.Id,
                    profile.DefaultModel,
                    StringComparison.Ordinal)))
            {
                models =
                [
                    new AiProviderModelDescriptor(
                        profile.DefaultModel,
                        profile.DefaultModel),
                    .. models,
                ];
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _discoveredModels[profile.Id] = Array.AsReadOnly(models.ToArray());
            }

            Refresh(_catalog.Snapshot);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return new AiProviderModelDiscoveryResult(
                true,
                "ai_provider_models_discovered",
                $"{models.Count} model(s) are available.",
                models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiProviderClientException exception)
        {
            return new AiProviderModelDiscoveryResult(
                false,
                exception.StableCode,
                exception.Message,
                []);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return new AiProviderModelDiscoveryResult(
                false,
                "ai_provider_model_discovery_failed",
                "Models could not be loaded from this provider.",
                []);
        }
    }

    public IAgentProvider CreateProvider(
        AiProviderProfileId profileId,
        string? model = null) =>
        PinProvider(profileId).CreateProvider(model);

    public CatalogAiProviderBinding PinProvider(
        AiProviderProfileId profileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stored = _catalog.Snapshot.AiProviderProfiles
            .SingleOrDefault(candidate => candidate.Value.Id == profileId)
            ?? throw new KeyNotFoundException(
                "The requested AI-provider profile is unavailable.");
        if (!stored.Value.IsEnabled)
        {
            throw new KeyNotFoundException(
                "The requested AI-provider profile is disabled.");
        }

        return new CatalogAiProviderBinding(
            this,
            stored.Value,
            stored.Revision);
    }

    internal bool IsCurrent(CatalogAiProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stored = _catalog.Snapshot.AiProviderProfiles
            .SingleOrDefault(candidate =>
                candidate.Value.Id == binding.ProfileId);
        return stored is not null
            && stored.Value.IsEnabled
            && stored.Revision == binding.Revision;
    }

    internal IAgentProvider CreateProvider(
        CatalogAiProviderBinding binding,
        AiProviderProfile profile,
        string? model = null,
        AgentServiceTier serviceTier = AgentServiceTier.Automatic)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (binding.ProfileId != profile.Id)
        {
            throw new ArgumentException(
                "The provider binding does not match its immutable profile.",
                nameof(binding));
        }

        if (!IsCurrent(binding))
        {
            throw new InvalidOperationException(
                "The pinned AI-provider profile changed and must be rebound.");
        }

        return _factory.Create(profile, model, serviceTier);
    }

    internal IAgentProvider CreateProvider(
        CatalogAiProviderBinding binding,
        AiProviderProfile profile,
        string? model,
        AgentServiceTier serviceTier,
        Uri networkProxy)
    {
        ArgumentNullException.ThrowIfNull(networkProxy);
        if (!networkProxy.IsAbsoluteUri
            || !string.Equals(networkProxy.Scheme, "socks5", StringComparison.OrdinalIgnoreCase)
            || !networkProxy.IsLoopback
            || networkProxy.Port is < 1 or > 65535)
        {
            throw new ArgumentException(
                "The workspace network proxy must be a loopback SOCKS5 endpoint.",
                nameof(networkProxy));
        }

        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (binding.ProfileId != profile.Id || !IsCurrent(binding))
        {
            throw new InvalidOperationException(
                "The pinned AI-provider profile changed and must be rebound.");
        }

        AiProviderFactory routedFactory;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_routedFactoryFactory is null)
            {
                throw new InvalidOperationException(
                    "Routed AI-provider connections are unavailable in this composition.");
            }

            var key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(networkProxy.AbsoluteUri)));
            if (!_routedFactories.TryGetValue(key, out routedFactory!))
            {
                routedFactory = _routedFactoryFactory(networkProxy);
                _routedFactories.Add(key, routedFactory);
            }
        }

        return routedFactory.Create(profile, model, serviceTier);
    }

    internal static WebProxy CreateWebProxy(Uri endpoint)
    {
        var proxy = new WebProxy(new UriBuilder(endpoint)
        {
            UserName = string.Empty,
            Password = string.Empty,
        }.Uri);
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            var userInfo = endpoint.UserInfo.Split(':', 2);
            if (userInfo.Length != 2)
            {
                throw new ArgumentException(
                    "The workspace network proxy credentials are invalid.",
                    nameof(endpoint));
            }

            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(userInfo[0]),
                Uri.UnescapeDataString(userInfo[1]));
        }

        return proxy;
    }

    public async ValueTask<AgentChatSendResult> SendAsync(
        AiProviderProfileId providerId,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Length > MaximumPromptLength)
        {
            return new AgentChatSendResult(
                false,
                "agent_chat_prompt_too_large",
                "The prompt exceeds the chat input limit.");
        }

        var profile = _catalog.Snapshot.AiProviderProfiles
            .Select(stored => stored.Value)
            .SingleOrDefault(candidate => candidate.Id == providerId);
        if (profile is null || !profile.IsEnabled)
        {
            return new AgentChatSendResult(
                false,
                "agent_chat_provider_unavailable",
                "Choose an enabled AI-provider profile.");
        }

        CancellationTokenSource turnCancellation;
        NativeAgentSession session;
        IReadOnlyList<AgentChatMessage> baseMessages;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_chatTurnCancellation is not null)
            {
                return new AgentChatSendResult(
                    false,
                    "agent_chat_busy",
                    "Wait for the current response or cancel it.");
            }

            if (_chatSnapshot.ProviderId is { } currentProvider
                && currentProvider != providerId
                && _chatSnapshot.Messages.Count > 0)
            {
                return new AgentChatSendResult(
                    false,
                    "agent_chat_provider_changed",
                    "Clear the current chat before switching providers.");
            }

            turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _chatTurnCancellation = turnCancellation;
            session = _chatSession;
            baseMessages = _chatSnapshot.Messages;
            _chatSnapshot = _chatSnapshot with
            {
                State = AgentChatState.Streaming,
                ProviderId = providerId,
                Messages = Array.AsReadOnly(
                    _chatSnapshot.Messages
                        .Append(new AgentChatMessage(AgentChatMessageRole.User, prompt))
                        .ToArray()),
                ProvisionalAssistantText = string.Empty,
                Status = $"Waiting for {profile.Name}…",
            };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            turnCancellation.Token);
        var watchTask = WatchProvisionalTextAsync(
            session,
            session.Snapshot().LastSequence,
            turnCancellation,
            watchCancellation.Token);
        AgentTurnResult? result = null;
        AgentTurnErrorCode? directError = null;
        try
        {
            result = await session.RunTurnAsync(
                prompt,
                [],
                _factory.Create(profile),
                turnCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested)
        {
            directError = AgentTurnErrorCode.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            directError = AgentTurnErrorCode.ProviderFailure;
        }
        finally
        {
            watchCancellation.Cancel();
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (watchCancellation.IsCancellationRequested)
            {
            }
        }

        if (result?.ToolProposals.Length > 0)
        {
            session.Cancel();
        }

        AgentChatSendResult sendResult;
        var notify = true;
        lock (_gate)
        {
            if (!ReferenceEquals(_chatTurnCancellation, turnCancellation))
            {
                notify = !_disposed;
                sendResult = new AgentChatSendResult(
                    false,
                    "agent_chat_cancelled",
                    "The chat response was cancelled.");
            }
            else
            {
                _chatTurnCancellation = null;
                var snapshot = session.Snapshot();
                var messages = ProjectChatMessages(snapshot);
                if (result?.Succeeded == true && result.ToolProposals.Length == 0)
                {
                    _chatSnapshot = new AgentChatSnapshot(
                        AgentChatState.Ready,
                        providerId,
                        messages,
                        string.Empty,
                        $"Ready · {profile.Name}");
                    sendResult = new AgentChatSendResult(
                        true,
                        "agent_chat_completed",
                        "The response completed.");
                }
                else
                {
                    var errorCode = directError ?? result?.ErrorCode;
                    var cancelled = errorCode == AgentTurnErrorCode.Cancelled
                        || turnCancellation.IsCancellationRequested;
                    _chatSnapshot = new AgentChatSnapshot(
                        cancelled ? AgentChatState.Cancelled : AgentChatState.Failed,
                        providerId,
                        baseMessages,
                        string.Empty,
                        cancelled
                            ? "Response cancelled."
                            : FailureStatus(errorCode));
                    sendResult = new AgentChatSendResult(
                        false,
                        cancelled ? "agent_chat_cancelled" : "agent_chat_failed",
                        _chatSnapshot.Status);
                }
            }
        }

        turnCancellation.Dispose();
        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return sendResult;
    }

    public bool Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation = _chatTurnCancellation;
            if (cancellation is null)
            {
                return false;
            }

            _chatSnapshot = _chatSnapshot with
            {
                State = AgentChatState.Cancelling,
                ProvisionalAssistantText = string.Empty,
                Status = "Cancelling response…",
            };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _chatSession.Cancel();
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The native-session fence can let SendAsync finish and dispose this source
            // between the active-turn capture above and this call. Cancellation still won.
        }

        return true;
    }

    public bool Clear()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_chatTurnCancellation is not null)
            {
                return false;
            }

            _chatSession = CreateChatSession();
            _chatSnapshot = EmptyChatSnapshot();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public ValueTask ReloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Refresh(_catalog.Snapshot);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        CancellationTokenSource? chatCancellation;
        AiProviderFactory[] routedFactories;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _catalog.Changed -= OnCatalogChanged;
            chatCancellation = _chatTurnCancellation;
            _chatTurnCancellation = null;
            routedFactories = [.. _routedFactories.Values];
            _routedFactories.Clear();
        }

        _chatSession.Cancel();
        chatCancellation?.Cancel();
        // The active SendAsync invocation owns this source and may still need its token while
        // unwinding. It disposes the source after observing the cancellation.
        _factory.Dispose();
        foreach (var routedFactory in routedFactories)
        {
            routedFactory.Dispose();
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Refresh(_catalog.Snapshot);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh(DefinitionCatalogSnapshot snapshot)
    {
        var profiles = snapshot.AiProviderProfiles
            .Select(stored => stored.Value)
            .OrderBy(profile => profile.Order)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDescriptor)
            .ToArray();
        var diagnostics = snapshot.AiProviderProfiles
            .Select(stored => stored.Value)
            .Where(profile =>
                !profile.IsEnabled
                || !AiProviderCatalog.Get(profile.Identity).IsRuntimeSupported)
            .OrderBy(profile => profile.Order)
            .Select(profile => profile.IsEnabled
                ? new AiProviderRuntimeDiagnostic(
                    profile.Id,
                    AiProviderRuntimeDiagnosticSeverity.Error,
                    "ai_provider_runtime_unavailable",
                    "This AI-provider protocol is not implemented and will not be selected.")
                : new AiProviderRuntimeDiagnostic(
                    profile.Id,
                    AiProviderRuntimeDiagnosticSeverity.Information,
                    "ai_provider_disabled",
                    "This AI-provider profile is disabled and will not be selected."))
            .ToArray();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _profiles = Array.AsReadOnly(profiles);
            _diagnostics = Array.AsReadOnly(diagnostics);
        }
    }

    private AiProviderProfileDescriptor ToDescriptor(AiProviderProfile profile)
    {
        var models = DiscoveredOrAvailableModels(profile)
            .Select(model => new AiProviderModelDescriptor(
                model.Id,
                model.DisplayName,
                AiProviderReasoningPolicy.SupportedEfforts(profile, model.Id),
                AiProviderServiceTierPolicy.SupportedTiers(profile, model.Id),
                model.ContextWindowTokens ?? ContextWindowTokens(profile, model.Id)))
            .ToArray();
        var defaultModel = models.Single(model => string.Equals(
            model.Id,
            profile.DefaultModel,
            StringComparison.Ordinal));
        return new AiProviderProfileDescriptor(
            profile.Id,
            profile.Name,
            profile.ProviderKind,
            profile.Endpoint,
            profile.DefaultModel,
            profile.Order,
            profile.IsEnabled
                && AiProviderCatalog.Get(profile.Identity).IsRuntimeSupported,
            profile.Authentication is AiProviderAuthentication.ApiKey
                or AiProviderAuthentication.OAuth,
            profile.Capabilities.SupportsImageInput
                && AiProviderCatalog.Get(profile.Identity).IsRuntimeSupported,
            SupportedReasoningEfforts: defaultModel.SupportedReasoningEfforts,
            Models: models);
    }

    private IReadOnlyList<AiProviderModelDescriptor> DiscoveredOrAvailableModels(
        AiProviderProfile profile)
    {
        lock (_gate)
        {
            return _discoveredModels.TryGetValue(profile.Id, out var models)
                ? models
                : AvailableModels(profile);
        }
    }

    private static IReadOnlyList<AiProviderModelDescriptor> AvailableModels(
        AiProviderProfile profile)
    {
        if (profile.Identity == AiProviderKind.OpenAi
            && profile.Authentication is AiProviderAuthentication.OAuth)
        {
            var models = OpenAiCodexModels;
            if (models.Any(model => string.Equals(
                    model.Id,
                    profile.DefaultModel,
                    StringComparison.Ordinal)))
            {
                return models;
            }

            return models.Insert(
                0,
                new AiProviderModelDescriptor(
                    profile.DefaultModel,
                    profile.DefaultModel));
        }

        return
        [
            new AiProviderModelDescriptor(
                profile.DefaultModel,
                profile.DefaultModel),
        ];
    }

    private static int? ContextWindowTokens(
        AiProviderProfile profile,
        string modelId)
    {
        if (profile.Identity != AiProviderKind.OpenAi
            || profile.Authentication is not AiProviderAuthentication.OAuth)
        {
            return null;
        }

        return string.Equals(
            modelId,
            "gpt-5.3-codex-spark",
            StringComparison.Ordinal)
                ? 128_000
                : modelId.StartsWith("gpt-5", StringComparison.Ordinal)
                    ? 272_000
                    : null;
    }

    private static AiProviderTestResult Failure(AiProviderClientException exception) => new(
        false,
        exception.StableCode,
        exception.Message,
        [],
        exception.Code,
        exception.RetryAfter);

    private async Task WatchProvisionalTextAsync(
        NativeAgentSession session,
        long afterSequence,
        CancellationTokenSource turnCancellation,
        CancellationToken cancellationToken)
    {
        await foreach (var item in session.WatchAsync(
                           new AgentEventWatchRequest(afterSequence, 64),
                           cancellationToken).ConfigureAwait(false))
        {
            if (item is not AgentRunStreamItem.EventBatch batch)
            {
                continue;
            }

            var fragments = batch.Events
                .Where(agentEvent => agentEvent.Kind == AgentRunEventKind.ProvisionalText)
                .Select(agentEvent => agentEvent.ProvisionalText)
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray();
            if (fragments.Length == 0)
            {
                continue;
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_chatTurnCancellation, turnCancellation)
                    || _chatSnapshot.State != AgentChatState.Streaming)
                {
                    continue;
                }

                _chatSnapshot = _chatSnapshot with
                {
                    ProvisionalAssistantText =
                        _chatSnapshot.ProvisionalAssistantText + string.Concat(fragments),
                };
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static IReadOnlyList<AgentChatMessage> ProjectChatMessages(
        AgentSessionSnapshot snapshot) =>
        Array.AsReadOnly(snapshot.Conversation
            .Where(message => message.Role is AgentMessageRole.User or AgentMessageRole.Assistant)
            .Select(message => new AgentChatMessage(
                message.Role == AgentMessageRole.User
                    ? AgentChatMessageRole.User
                    : AgentChatMessageRole.Assistant,
                message.Content,
                message.ReasoningSummary,
                message.Usage is { } usage
                    ? new AgentChatUsage(
                        usage.InputTokens,
                        usage.OutputTokens,
                        usage.CachedInputTokens,
                        usage.ReasoningTokens,
                        usage.TotalTokens)
                    : null,
                Array.AsReadOnly(message.Images
                    .Select(image => new AgentChatImage(
                        image.FileName,
                        image.MediaType,
                        image.Content.Length))
                    .ToArray()) is { Count: > 0 } images
                    ? images
                    : null,
                message.RequestedReasoningEffort))
            .ToArray());

    private static NativeAgentSession CreateChatSession() => new(
        AgentRunId.New(),
        [new AgentMessage(AgentMessageRole.System, ChatSystemPrompt)]);

    private static AgentChatSnapshot EmptyChatSnapshot() => new(
        AgentChatState.Ready,
        null,
        [],
        string.Empty,
        "Choose a provider to start a chat-only session.");

    private static string FailureStatus(AgentTurnErrorCode? errorCode) => errorCode switch
    {
        AgentTurnErrorCode.LimitExceeded =>
            "The response exceeded the native agent safety limits.",
        AgentTurnErrorCode.InvalidProviderStream =>
            "The provider returned an invalid streaming response.",
        AgentTurnErrorCode.ProviderFailure =>
            "The provider request failed. Review its endpoint and credential.",
        AgentTurnErrorCode.AlreadyRunning or AgentTurnErrorCode.ProviderOperationLimit =>
            "The native agent is already processing a response.",
        _ => "The native chat response could not be completed.",
    };
}
