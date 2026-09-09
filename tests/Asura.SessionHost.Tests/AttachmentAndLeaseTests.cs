using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class AttachmentAndLeaseTests
{
    [Fact]
    public async Task ConcurrentIdempotentReadAttachmentsReturnOneStableAttachment()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var initial = await harness.AttachAsync();
        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                initial.Attachment.Id,
                new NativeRendererHost("fake", 1, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var terminal = harness.Factory[harness.SessionId];
        terminal.BlockRendererDetach = true;

        var blocker = harness.Client.AttachAsync(
            new AttachSessionRequest(
                harness.SessionId,
                harness.ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 2),
                SessionHostTestHarness.AllCapabilities()),
            harness.HumanContext(),
            CancellationToken.None).AsTask();
        await terminal.RendererDetachStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var context = harness.HumanContext(
            idempotencyKey: new IdempotencyKey("concurrent-read-attachment"));
        var request = new AttachSessionRequest(
            harness.SessionId,
            harness.ClientId,
            AttachmentKind.ReadOnly,
            new ViewportDescriptor(800, 600, 2),
            SessionHostTestHarness.AllCapabilities());
        var first = harness.Client.AttachAsync(
            request,
            context,
            CancellationToken.None).AsTask();
        var duplicate = harness.Client.AttachAsync(
            request,
            context,
            CancellationToken.None).AsTask();

        terminal.ReleaseRendererDetach.TrySetResult();
        _ = (await blocker).Value();
        var results = await Task.WhenAll(first, duplicate);
        var firstResult = results[0].Value();
        var duplicateResult = results[1].Value();

        Assert.Equal(firstResult.Attachment.Id, duplicateResult.Attachment.Id);
        Assert.Equal(2, duplicateResult.Snapshot.Attachments.Count);
        Assert.Equal(1, terminal.DetachRendererCount);
    }

    [Fact]
    public async Task AttachmentFailureAfterRendererDetachRetainsUncertainReplay()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var initial = await harness.AttachAsync();
        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                initial.Attachment.Id,
                new NativeRendererHost("fake", 1, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var terminal = harness.Factory[harness.SessionId];
        terminal.ThrowAfterRendererDetach = true;
        var context = harness.HumanContext(
            idempotencyKey: new IdempotencyKey("failed-interactive-attachment"));
        var request = new AttachSessionRequest(
            harness.SessionId,
            harness.ClientId,
            AttachmentKind.Interactive,
            new ViewportDescriptor(800, 600, 2),
            SessionHostTestHarness.AllCapabilities());

        var uncertain = await harness.Client.AttachAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await harness.Client.AttachAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, terminal.DetachRendererCount);
    }

    [Fact]
    public async Task ReadAttachmentsCoexistAndVisualDetachKeepsSessionAlive()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var first = await harness.AttachAsync(AttachmentKind.ReadOnly, new ClientId("reader-1"));
        var second = await harness.AttachAsync(AttachmentKind.ReadOnly, new ClientId("reader-2"));

        Assert.NotEqual(first.Attachment.Id, second.Attachment.Id);
        Assert.Equal(2, second.Snapshot.Attachments.Count);

        var detached = await harness.Client.DetachAsync(
            new DetachSessionRequest(first.Attachment.Id, harness.SessionId),
            harness.HumanContext(new ClientId("reader-1")),
            CancellationToken.None);

        _ = detached.Value();
        var snapshot = (await harness.Client.GetSnapshotAsync(
            harness.SessionId,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Single(snapshot.Attachments);
        Assert.NotEqual(SessionLifecycle.Closed, snapshot.Descriptor.Lifecycle);
        Assert.False(harness.Factory[harness.SessionId].IsClosed);
    }

    [Fact]
    public async Task HumanLeasePreemptsAgentAndAgentCannotPreemptHuman()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var attachment = await harness.AttachAsync();

        var agentLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            SessionHostTestHarness.AgentContext(),
            CancellationToken.None)).Value();
        Assert.True(agentLease.Granted);

        var humanLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(
                harness.SessionId,
                attachment.Attachment.Id,
                TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.True(humanLease.Granted);
        Assert.True(humanLease.PreemptedAnotherHolder);

        var deniedAgent = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            SessionHostTestHarness.AgentContext(),
            CancellationToken.None)).Value();
        Assert.False(deniedAgent.Granted);
        Assert.Equal(humanLease.Lease?.Id, deniedAgent.Lease?.Id);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeAcquiredByAnotherActor()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        _ = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(1)),
            SessionHostTestHarness.AgentContext(),
            CancellationToken.None)).Value();

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var human = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(1)),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        Assert.True(human.Granted);
        Assert.False(human.PreemptedAnotherHolder);
    }
}
