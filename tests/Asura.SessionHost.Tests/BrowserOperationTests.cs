using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

public sealed class BrowserOperationTests
{
    [Fact]
    public async Task CancellationDuringBrowserSessionCreationRetainsUncertainReplay()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        var creationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        browsers.AfterCreateAsync = async (_, cancellationToken) =>
        {
            creationEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        };
        await using var host = CreateHost(browsers);
        var request = BrowserOpenRequest("browser-create-cancelled");
        var context = Context(
            idempotencyKey: new IdempotencyKey("browser-create-cancelled"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureBrowserSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await creationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var uncertain = await pending;
        var replay = await host.EnsureBrowserSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, browsers.CreateCount);
    }

    [Fact]
    public async Task BrowserSessionSnapshotFailureDisposesEngineAndRetainsUncertainReplay()
    {
        var browsers = new FakeBrowserPanelSessionFactory
        {
            BeforeSnapshotForNewSessions = static _ =>
                ValueTask.FromException(new IOException("fake snapshot failure")),
        };
        await using var host = CreateHost(browsers);
        var request = BrowserOpenRequest("browser-create-failed");
        var context = Context(
            idempotencyKey: new IdempotencyKey("browser-create-failed"));

        var uncertain = await host.EnsureBrowserSessionAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await host.EnsureBrowserSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, browsers[request.SessionId].DisposeCount);
        Assert.Equal(1, browsers.CreateCount);
    }

    [Fact]
    public async Task ConcurrentBrowserCreationCompletesKnownSuccessAfterCallerCancellation()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotToken = CancellationToken.None;
        browsers.BeforeSnapshotForNewSessions = async cancellationToken =>
        {
            snapshotToken = cancellationToken;
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(browsers);
        var request = BrowserOpenRequest("browser-create-known");
        var context = Context(
            idempotencyKey: new IdempotencyKey("browser-create-known"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureBrowserSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var concurrentReplay = await host.EnsureBrowserSessionAsync(
            request,
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        var completed = await pending;
        var completedReplay = await host.EnsureBrowserSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            concurrentReplay.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completed);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completedReplay);
        Assert.False(snapshotToken.CanBeCanceled);
        Assert.Equal(1, browsers.CreateCount);
    }

    [Fact]
    public async Task CancellationBeforeBrowserSessionCreationLeavesKeyFresh()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var request = BrowserOpenRequest("browser-create-pre-cancelled");
        var context = Context(
            idempotencyKey: new IdempotencyKey("browser-create-pre-cancelled"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await host.EnsureBrowserSessionAsync(
            request,
            context,
            cancellation.Token);
        var retry = await host.EnsureBrowserSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(retry);
        Assert.Equal(1, browsers.CreateCount);
    }

    [Fact]
    public async Task ConcurrentExistingBrowserOpensEnforceStoredFingerprintInsideGate()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        var terminals = new FakeTerminalSessionFactory();
        await using var host = CreateHost(browsers, terminalFactory: terminals);
        var request = BrowserOpenRequest("existing-browser-race");
        _ = (await host.EnsureBrowserSessionAsync(
            request,
            Context(),
            CancellationToken.None)).Value();
        terminals.BlockCreation = true;
        var blocker = host.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                new SessionId("browser-gate-blocker"),
                Owner("browser-gate-blocker-panel"),
                "Terminal",
                new TerminalLaunchRequest("/tmp")),
            Context(),
            CancellationToken.None).AsTask();
        await terminals.CreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var context = Context(
            idempotencyKey: new IdempotencyKey("existing-browser-race"));
        var changed = request with
        {
            InitialAddress = Address("https://changed.example.test/"),
        };
        var first = host.EnsureBrowserSessionAsync(
            request,
            context,
            CancellationToken.None).AsTask();
        var competing = host.EnsureBrowserSessionAsync(
            changed,
            context,
            CancellationToken.None).AsTask();

        terminals.AllowCreation.TrySetResult();
        _ = (await blocker).Value();
        var results = await Task.WhenAll(first, competing);

        Assert.Single(results, result => result is HostResult<SessionSnapshot>.Success);
        var rejected = Assert.Single(
            results.OfType<HostResult<SessionSnapshot>.Failure>());
        Assert.Equal(HostErrorCode.IdempotencyKeyReused, rejected.Error.Code);
        Assert.Equal(1, browsers.CreateCount);
    }

    [Fact]
    public async Task BrowserOpenReservationRejectsCrossFamilyTerminalOpen()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        var terminals = new FakeTerminalSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        browsers.BeforeSnapshotForNewSessions = async _ =>
        {
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(browsers, terminalFactory: terminals);
        var context = Context(
            idempotencyKey: new IdempotencyKey("browser-cross-family"));

        var browser = host.EnsureBrowserSessionAsync(
            BrowserOpenRequest("browser-cross-family"),
            context,
            CancellationToken.None).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = await host.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                new SessionId("browser-cross-family-terminal"),
                Owner("browser-cross-family-terminal-panel"),
                "Terminal",
                new TerminalLaunchRequest("/tmp")),
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        Assert.Equal(HostErrorCode.IdempotencyKeyReused, rejected.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(await browser);
        Assert.Equal(1, browsers.CreateCount);
        Assert.Equal(0, terminals.CreateCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureRejectsBrowserSessionCapabilityProfileMismatch(
        bool createdSessionIsWider)
    {
        var advertisedCapabilities = BrowserCapabilities();
        var createdCapabilities = createdSessionIsWider
            ? new CapabilitySet(
            [
                .. advertisedCapabilities.Values,
                SessionCapabilities.BrowserSnapshot,
            ])
            : new CapabilitySet(
                advertisedCapabilities.Values.Where(
                    capability => !string.Equals(capability, SessionCapabilities.BrowserStop, StringComparison.Ordinal)));
        var browsers = new FakeBrowserPanelSessionFactory(
            advertisedCapabilities,
            createdCapabilities);
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("capability-mismatch");

        var rejected = await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("mismatched-panel"),
                "Browser",
                Address("https://example.test/")),
            Context(),
            CancellationToken.None);
        var notRegistered = await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.EngineFailed, rejected.Error().Code);
        Assert.Equal("engine_failed", rejected.Error().StableCode);
        Assert.False(rejected.Error().Retryable);
        Assert.Equal(1, browsers[sessionId].DisposeCount);
        Assert.Equal(HostErrorCode.NotFound, notRegistered.Error().Code);
    }

    [Fact]
    public async Task EnsureRegistersBrowserSessionWhenCapabilityProfilesMatch()
    {
        var capabilities = BrowserCapabilities();
        var browsers = new FakeBrowserPanelSessionFactory(
            capabilities,
            capabilities);
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("matching-capabilities");

        var opened = await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("matching-panel"),
                "Browser",
                Address("https://example.test/")),
            Context(),
            CancellationToken.None);

        Assert.IsType<HostResult<SessionSnapshot>.Success>(opened);
        Assert.Equal(0, browsers[sessionId].DisposeCount);
    }

    [Fact]
    public async Task HostOwnsBrowserLifecycleAndDispatchesTypedNavigation()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var initialAddress = Address("https://example.test/");
        var opened = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureBrowserSessionAsync(
                new EnsureBrowserSessionRequest(
                    sessionId,
                    Owner("panel-1"),
                    "Browser",
                    initialAddress),
                Context(),
                CancellationToken.None));

        var hello = (await host.NegotiateAsync(
            new ClientHello([1], BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.BrowserNavigate));

        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(1024, 768, 1),
                BrowserCapabilities()),
            Context(expectedRevision: opened.ResultingRevision),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(initialAddress);
        var attached = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None);

        Assert.IsType<HostResult<Unit>.Success>(attached);
        Assert.Equal(1, browsers[sessionId].AttachCount);

        var destination = Address("https://docs.example.test/guide");
        var idempotency = new IdempotencyKey("browser-navigation-1");
        var navigationContext = Context(idempotencyKey: idempotency);
        var navigated = Assert.IsType<
            HostResult<BrowserResult<BrowserSessionState>>.Success>(
            await host.NavigateBrowserAsync(
                new BrowserNavigateRequest(sessionId, destination),
                navigationContext,
                CancellationToken.None));
        var replayed = Assert.IsType<
            HostResult<BrowserResult<BrowserSessionState>>.Success>(
            await host.NavigateBrowserAsync(
                new BrowserNavigateRequest(sessionId, destination),
                navigationContext,
                CancellationToken.None));

        Assert.True(navigated.Value.IsSuccess, navigated.Value.Error?.Message);
        Assert.Equal(destination, navigated.Value.Value?.Address);
        Assert.Equal(navigated.ResultingRevision, replayed.ResultingRevision);
        Assert.Equal(1, renderer.NavigateCount);

        var backContext = Context(
            idempotencyKey: new IdempotencyKey("browser-back-1"));
        var forwardContext = Context(
            idempotencyKey: new IdempotencyKey("browser-forward-1"));
        var reloadContext = Context(
            idempotencyKey: new IdempotencyKey("browser-reload-1"));
        var stopContext = Context(
            idempotencyKey: new IdempotencyKey("browser-stop-1"));
        Assert.True((await host.GoBackBrowserAsync(
            sessionId,
            backContext,
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.GoBackBrowserAsync(
            sessionId,
            backContext,
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.GoForwardBrowserAsync(
            sessionId,
            forwardContext,
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.GoForwardBrowserAsync(
            sessionId,
            forwardContext,
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.ReloadBrowserAsync(
            sessionId,
            reloadContext,
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.ReloadBrowserAsync(
            sessionId,
            reloadContext,
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.StopBrowserAsync(
            sessionId,
            stopContext,
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.StopBrowserAsync(
            sessionId,
            stopContext,
            CancellationToken.None)).Value().IsSuccess);
        var state = (await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None)).Value();

        Assert.Equal(1, renderer.BackCount);
        Assert.Equal(1, renderer.ForwardCount);
        Assert.Equal(1, renderer.ReloadCount);
        Assert.Equal(1, renderer.StopCount);
        Assert.Equal(destination, state.Value?.Address);

        var detached = await host.DetachAsync(
            new DetachSessionRequest(attachment.Attachment.Id, sessionId),
            Context(),
            CancellationToken.None);
        var unavailable = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, initialAddress),
            Context(),
            CancellationToken.None);

        Assert.IsType<HostResult<Unit>.Success>(detached);
        Assert.Equal(1, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.LeaseDenied, unavailable.Error().Code);
    }

    [Fact]
    public async Task CancellationAfterBrowserDispatchLeavesAnUncertainReplay()
    {
        var fixture = await OpenAttachedBrowserAsync("cancelled-after-dispatch");
        await using var host = fixture.Host;
        fixture.Renderer.NavigateEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.NavigateRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new BrowserNavigateRequest(
            fixture.SessionId,
            Address("https://docs.example.test/cancelled"));
        var context = Context(
            idempotencyKey: new IdempotencyKey("cancelled-browser-navigation"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.NavigateBrowserAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await fixture.Renderer.NavigateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var uncertain = await pending;
        var replay = await host.NavigateBrowserAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
    }

    [Fact]
    public async Task CancellationBeforeBrowserDispatchDoesNotReserveTheKey()
    {
        var fixture = await OpenAttachedBrowserAsync("cancelled-before-dispatch");
        await using var host = fixture.Host;
        var request = new BrowserNavigateRequest(
            fixture.SessionId,
            Address("https://docs.example.test/retry"));
        var context = Context(
            idempotencyKey: new IdempotencyKey("pre-dispatch-browser-cancellation"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await host.NavigateBrowserAsync(
            request,
            context,
            cancellation.Token);
        var retry = await host.NavigateBrowserAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.IsType<HostResult<BrowserResult<BrowserSessionState>>.Success>(retry);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
    }

    [Fact]
    public async Task KnownBrowserResultCompletesReplayAfterCallerCancellation()
    {
        var fixture = await OpenAttachedBrowserAsync("known-after-cancellation");
        await using var host = fixture.Host;
        fixture.Renderer.NavigateEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.NavigateRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Renderer.IgnoreNavigateCancellation = true;
        var request = new BrowserNavigateRequest(
            fixture.SessionId,
            Address("https://docs.example.test/known"));
        var context = Context(
            idempotencyKey: new IdempotencyKey("known-browser-navigation"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.NavigateBrowserAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await fixture.Renderer.NavigateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        fixture.Renderer.NavigateRelease.TrySetResult();

        var completed = await pending;
        var replay = await host.NavigateBrowserAsync(
            request,
            context,
            CancellationToken.None);

        Assert.IsType<HostResult<BrowserResult<BrowserSessionState>>.Success>(completed);
        Assert.IsType<HostResult<BrowserResult<BrowserSessionState>>.Success>(replay);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
    }

    [Fact]
    public async Task RendererAttachmentRequiresTheExactInteractiveHumanClient()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("panel-1"),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();

        var wrongClient = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(new ClientId("other-client")),
            CancellationToken.None);
        var wrongAttachment = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                new AttachmentId("missing-attachment"),
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.LeaseDenied, wrongClient.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, wrongAttachment.Error().Code);
        Assert.Equal(0, browsers[sessionId].AttachCount);
    }

    [Fact]
    public async Task ReplacingOrDisconnectingInteractiveClientDetachesBrowserRenderer()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("panel-1"),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var firstAttachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                firstAttachment.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None)).Value();

        var replacement = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(1200, 800, 2),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var staleAttachment = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                firstAttachment.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                replacement.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None)).Value();

        Assert.Equal(1, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.LeaseDenied, staleAttachment.Error().Code);
        Assert.Equal(2, browsers[sessionId].AttachCount);

        _ = (await host.DisconnectClientAsync(
            ClientId,
            Context(),
            CancellationToken.None)).Value();
        var unavailable = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, address),
            Context(),
            CancellationToken.None);

        Assert.Equal(2, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.LeaseDenied, unavailable.Error().Code);
    }

    [Fact]
    public async Task NormalBrowserOperationsRequireTheCurrentInteractiveHumanPrincipal()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("panel-1"),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(address);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None)).Value();

        var agentContext = new OperationContext(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("agent-1"),
                ActorKind.Agent,
                "Agent",
                ClientId),
            CancellationId: CancellationId.New());
        var agentResults = new[]
        {
            await host.ReadBrowserStateAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.NavigateBrowserAsync(
                new BrowserNavigateRequest(
                    sessionId,
                    Address("https://blocked.example.test/")),
                agentContext,
                CancellationToken.None),
            await host.GoBackBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.GoForwardBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.ReloadBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.StopBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
        };

        Assert.All(
            agentResults,
            result => Assert.Equal(HostErrorCode.LeaseDenied, result.Error().Code));
        Assert.Equal(0, renderer.NavigateCount);
        Assert.Equal(0, renderer.BackCount);
        Assert.Equal(0, renderer.ForwardCount);
        Assert.Equal(0, renderer.ReloadCount);
        Assert.Equal(0, renderer.StopCount);

        var mismatchedHuman = new OperationContext(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("different-id"),
                ActorKind.Human,
                "Impersonated user",
                ClientId),
            CancellationId: CancellationId.New());
        var mismatched = await host.ReadBrowserStateAsync(
            sessionId,
            mismatchedHuman,
            CancellationToken.None);
        Assert.Equal(HostErrorCode.LeaseDenied, mismatched.Error().Code);

        _ = (await host.DetachAsync(
            new DetachSessionRequest(attachment.Attachment.Id, sessionId),
            Context(),
            CancellationToken.None)).Value();
        var detached = await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.LeaseDenied, detached.Error().Code);
    }

    [Fact]
    public async Task ContextGuardsRunBeforeBrowserDispatchAndCloseDisposesTheRenderer()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers, clock);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        var opened = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureBrowserSessionAsync(
                new EnsureBrowserSessionRequest(
                    sessionId,
                    Owner("panel-1"),
                    "Browser",
                    address),
                Context(),
                CancellationToken.None));
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(address);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None)).Value();

        var stale = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, Address("https://stale.example.test/")),
            Context(expectedRevision: opened.ResultingRevision),
            CancellationToken.None);
        var expired = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, Address("https://expired.example.test/")),
            Context(deadline: clock.GetUtcNow()),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, Address("https://cancelled.example.test/")),
            Context(),
            cancellation.Token);

        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);
        Assert.Equal(HostErrorCode.DeadlineExceeded, expired.Error().Code);
        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.Equal(0, renderer.NavigateCount);

        var closed = (await host.CloseAsync(
            CloseScopeRequest.Panel(
                new PanelInstanceId("panel-1"),
                CloseDecision.Request),
            Context(),
            CancellationToken.None)).Value();
        var afterClose = await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None);

        Assert.IsType<CloseScopeResult.Completed>(closed);
        Assert.Equal(1, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.SessionClosed, afterClose.Error().Code);
    }

    [Fact]
    public async Task LoadingBrowserClosesWithoutDestructiveConfirmation()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("loading-browser");
        var panelId = new PanelInstanceId("loading-browser-panel");
        var address = Address("https://example.test/watch?v=one");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner(panelId.Value),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(address);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None)).Value();
        renderer.BeginLoading(address);

        var closed = (await host.CloseAsync(
            CloseScopeRequest.Panel(panelId, CloseDecision.Request),
            Context(),
            CancellationToken.None)).Value();

        var completed = Assert.IsType<CloseScopeResult.Completed>(closed);
        Assert.Equal(
            SessionCloseOutcome.GracefullyClosed,
            Assert.Single(completed.Sessions).Outcome);
    }

    private static readonly ClientId ClientId = new("client-1");

    private static async ValueTask<AttachedBrowserFixture> OpenAttachedBrowserAsync(
        string identity)
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        var host = CreateHost(browsers);
        var sessionId = new SessionId($"browser-{identity}");
        var initialAddress = Address("https://example.test/");
        try
        {
            var opened = (await host.EnsureBrowserSessionAsync(
                new EnsureBrowserSessionRequest(
                    sessionId,
                    Owner($"panel-{identity}"),
                    "Browser",
                    initialAddress),
                Context(),
                CancellationToken.None)).Value();
            var attachment = (await host.AttachAsync(
                new AttachSessionRequest(
                    sessionId,
                    ClientId,
                    AttachmentKind.Interactive,
                    new ViewportDescriptor(800, 600, 1),
                    BrowserCapabilities()),
                Context(expectedRevision: opened.Descriptor.Revision),
                CancellationToken.None)).Value();
            var renderer = new FakeBrowserRenderer(initialAddress);
            _ = (await host.AttachBrowserRendererAsync(
                new AttachBrowserRendererRequest(
                    sessionId,
                    attachment.Attachment.Id,
                    renderer),
                Context(),
                CancellationToken.None)).Value();
            return new AttachedBrowserFixture(host, sessionId, renderer);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static InMemorySessionHostClient CreateHost(
        IBrowserPanelSessionFactory browserFactory,
        TimeProvider? timeProvider = null,
        ITerminalSessionFactory? terminalFactory = null) => new(
        terminalFactory ?? new FakeTerminalSessionFactory(),
        new DesktopLifecyclePolicy(),
        timeProvider ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch),
        browserPanelFactory: browserFactory);

    private static EnsureBrowserSessionRequest BrowserOpenRequest(string id) => new(
        new SessionId(id),
        Owner($"{id}-panel"),
        "Browser",
        Address("https://example.test/"));

    private static BrowserAddress Address(string value) =>
        new(new Uri(value, UriKind.Absolute));

    private static OperationContext Context(
        ClientId? clientId = null,
        long? expectedRevision = null,
        IdempotencyKey? idempotencyKey = null,
        DateTimeOffset? deadline = null)
    {
        var actualClientId = clientId ?? ClientId;
        return new OperationContext(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId(actualClientId.Value),
                ActorKind.Human,
                "Test user",
                actualClientId),
            expectedRevision,
            idempotencyKey,
            CancellationId.New(),
            deadline);
    }

    private static SessionOwner Owner(string panelId) => new(
        HostMode.Desktop,
        new WindowInstanceId("window-1"),
        new WorkspaceInstanceId("workspace-1"),
        new TabInstanceId("tab-1"),
        new PanelInstanceId(panelId));

    private static CapabilitySet BrowserCapabilities() => new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.AttachInteractive,
        SessionCapabilities.BrowserReadState,
        SessionCapabilities.BrowserNavigate,
        SessionCapabilities.BrowserBack,
        SessionCapabilities.BrowserForward,
        SessionCapabilities.BrowserReload,
        SessionCapabilities.BrowserStop,
        SessionCapabilities.BrowserAgentInputBarrier,
    ]);

    private sealed record AttachedBrowserFixture(
        InMemorySessionHostClient Host,
        SessionId SessionId,
        FakeBrowserRenderer Renderer);
}
