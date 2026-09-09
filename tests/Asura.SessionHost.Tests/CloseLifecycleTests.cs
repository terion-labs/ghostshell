using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class CloseLifecycleTests
{
    [Fact]
    public async Task ConcurrentIdempotentCloseDispatchesEngineOnce()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var blockerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.FileFactory.AfterCreateAsync = async (_, _) =>
        {
            blockerEntered.TrySetResult();
            await releaseBlocker.Task.ConfigureAwait(false);
        };
        var blocker = harness.Client.EnsureFilePanelSessionAsync(
            new EnsureFilePanelSessionRequest(
                new SessionId("close-gate-blocker"),
                new SessionOwner(
                    HostMode.Desktop,
                    harness.WindowId,
                    harness.WorkspaceId,
                    harness.TabId,
                    new PanelInstanceId("close-gate-blocker-panel")),
                "Files",
                new FilePanelLocation(
                    "profile-1",
                    "test",
                    new FilePanelAddress.Hierarchical(FilePanelPath.Root))),
            harness.HumanContext(),
            CancellationToken.None).AsTask();
        await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var context = harness.HumanContext(
            idempotencyKey: new IdempotencyKey("concurrent-close"));
        var request = CloseScopeRequest.Session(
            harness.SessionId,
            CloseDecision.Request);
        var first = harness.Client.CloseAsync(
            request,
            context,
            CancellationToken.None).AsTask();
        var duplicate = harness.Client.CloseAsync(
            request,
            context,
            CancellationToken.None).AsTask();

        releaseBlocker.TrySetResult();
        _ = (await blocker).Value();
        var results = await Task.WhenAll(first, duplicate);

        Assert.All(results, result => Assert.IsType<HostResult<CloseScopeResult>.Success>(result));
        Assert.Equal(results[0].Value(), results[1].Value());
        Assert.Equal(1, harness.Factory[harness.SessionId].CloseCount);
    }

    [Fact]
    public async Task CloseExceptionAfterDispatchRetainsUncertainReplay()
    {
        await using var harness = new SessionHostTestHarness();
        harness.Factory.ThrowWhenClosing = true;
        await harness.OpenAsync();
        var context = harness.HumanContext(
            idempotencyKey: new IdempotencyKey("failed-close"));
        var request = CloseScopeRequest.Session(
            harness.SessionId,
            CloseDecision.Request);

        var uncertain = await harness.Client.CloseAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await harness.Client.CloseAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, harness.Factory[harness.SessionId].CloseCount);
    }

    [Fact]
    public async Task CancellationBeforeCloseDispatchLeavesKeyFresh()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var context = harness.HumanContext(
            idempotencyKey: new IdempotencyKey("pre-cancelled-close"));
        var request = CloseScopeRequest.Session(
            harness.SessionId,
            CloseDecision.Request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await harness.Client.CloseAsync(
            request,
            context,
            cancellation.Token);
        var retry = await harness.Client.CloseAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.IsType<HostResult<CloseScopeResult>.Success>(retry);
        Assert.Equal(1, harness.Factory[harness.SessionId].CloseCount);
    }

    [Fact]
    public async Task ActivePanelRequiresConfirmationAndCancellationChangesNothing()
    {
        await using var harness = new SessionHostTestHarness();
        harness.Factory.NewSessionsHaveActiveWork = true;
        await harness.OpenAsync();

        var requested = (await harness.Client.CloseAsync(
            CloseScopeRequest.Panel(harness.PanelId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var confirmation = Assert.IsType<CloseScopeResult.ConfirmationRequired>(requested);
        Assert.Single(confirmation.Sessions);
        Assert.Equal(0, harness.Factory[harness.SessionId].CloseCount);

        var cancelled = (await harness.Client.CloseAsync(
            CloseScopeRequest.Panel(harness.PanelId, CloseDecision.Cancel),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var completed = Assert.IsType<CloseScopeResult.Completed>(cancelled);
        Assert.Equal(SessionCloseOutcome.Cancelled, Assert.Single(completed.Sessions).Outcome);
        Assert.False(harness.Factory[harness.SessionId].IsClosed);
    }

    [Fact]
    public async Task ConfirmedActivePanelIsForceTerminated()
    {
        await using var harness = new SessionHostTestHarness();
        harness.Factory.NewSessionsHaveActiveWork = true;
        await harness.OpenAsync();

        var confirmed = (await harness.Client.CloseAsync(
            CloseScopeRequest.Panel(harness.PanelId, CloseDecision.Confirm),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var completed = Assert.IsType<CloseScopeResult.Completed>(confirmed);
        Assert.Equal(SessionCloseOutcome.ForceTerminated, Assert.Single(completed.Sessions).Outcome);
        Assert.Equal(PanelCloseMode.Force, harness.Factory[harness.SessionId].LastCloseMode);
        Assert.True(harness.Factory[harness.SessionId].IsClosed);
    }

    [Fact]
    public async Task InactiveSessionClosesGracefullyWithoutPrompt()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();

        var requested = (await harness.Client.CloseAsync(
            CloseScopeRequest.Window(harness.WindowId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var completed = Assert.IsType<CloseScopeResult.Completed>(requested);
        Assert.Equal(SessionCloseOutcome.GracefullyClosed, Assert.Single(completed.Sessions).Outcome);
        Assert.Equal(PanelCloseMode.Graceful, harness.Factory[harness.SessionId].LastCloseMode);
    }

    [Fact]
    public async Task EngineFailureAndCancellationAreDistinct()
    {
        await using var failedHarness = new SessionHostTestHarness();
        failedHarness.Factory.ThrowWhenClosing = true;
        await failedHarness.OpenAsync();
        var failed = (await failedHarness.Client.CloseAsync(
            CloseScopeRequest.Panel(failedHarness.PanelId, CloseDecision.Request),
            failedHarness.HumanContext(),
            CancellationToken.None)).Value();
        var failedResult = Assert.IsType<CloseScopeResult.Completed>(failed);
        Assert.Equal(SessionCloseOutcome.EngineFailed, Assert.Single(failedResult.Sessions).Outcome);

        await using var cancelledHarness = new SessionHostTestHarness();
        cancelledHarness.Factory.CloseOutcomeOverride = PanelCloseOutcome.Cancelled;
        await cancelledHarness.OpenAsync();
        var cancelled = (await cancelledHarness.Client.CloseAsync(
            CloseScopeRequest.Panel(cancelledHarness.PanelId, CloseDecision.Request),
            cancelledHarness.HumanContext(),
            CancellationToken.None)).Value();
        var cancelledResult = Assert.IsType<CloseScopeResult.Completed>(cancelled);
        Assert.Equal(SessionCloseOutcome.Cancelled, Assert.Single(cancelledResult.Sessions).Outcome);
    }

    [Fact]
    public async Task ServerClientDisconnectDetachesButExplicitCloseTerminates()
    {
        await using var harness = new SessionHostTestHarness(HostMode.Server);
        await harness.OpenAsync();
        var attachment = await harness.AttachAsync();
        await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                attachment.Attachment.Id,
                new NativeRendererHost("fake", 1, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None);

        _ = (await harness.Client.DisconnectClientAsync(
            harness.ClientId,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.False(harness.Factory[harness.SessionId].IsClosed);
        Assert.False(harness.Factory[harness.SessionId].RendererAttached);

        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Session(harness.SessionId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.True(harness.Factory[harness.SessionId].IsClosed);
    }

    [Fact]
    public async Task TabPreflightAggregatesActiveSessionsWithoutPartiallyClosing()
    {
        await using var harness = new SessionHostTestHarness();
        harness.Factory.NewSessionsHaveActiveWork = true;
        await harness.OpenAsync();
        var secondSession = new Asura.Core.SessionId("session-2");
        await harness.OpenAsync(
            sessionId: secondSession,
            panelId: new Asura.Core.PanelInstanceId("panel-2"));

        var requested = (await harness.Client.CloseAsync(
            CloseScopeRequest.Tab(harness.TabId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var confirmation = Assert.IsType<CloseScopeResult.ConfirmationRequired>(requested);
        Assert.Equal(2, confirmation.Sessions.Count);
        Assert.Equal(0, harness.Factory[harness.SessionId].CloseCount);
        Assert.Equal(0, harness.Factory[secondSession].CloseCount);
    }
}
