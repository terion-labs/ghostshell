using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class RevisionAndStreamTests
{
    [Fact]
    public async Task StreamResumesAfterCursorWithStrictlyIncreasingSequence()
    {
        await using var harness = new SessionHostTestHarness();
        var opened = await harness.OpenAsync();
        var cursor = opened.LastSequence;
        var attachment = await harness.AttachAsync(AttachmentKind.ReadOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = harness.Client.WatchAsync(
                new WatchSessionRequest(harness.SessionId, cursor),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        Assert.True(await events.MoveNextAsync());
        var sessionEvent = Assert.IsType<SessionStreamItem.Event>(events.Current).Value;
        Assert.Equal(SessionEventKind.AttachmentAdded, sessionEvent.Kind);
        Assert.Equal(attachment.EventCursor, sessionEvent.Sequence);
        Assert.True(sessionEvent.Sequence > cursor);
    }

    [Fact]
    public async Task OldCursorGetsExplicitResynchronizationSnapshot()
    {
        await using var harness = new SessionHostTestHarness(eventRetention: 2);
        await harness.OpenAsync();
        var attachment = await harness.AttachAsync();
        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(
                harness.SessionId,
                attachment.Attachment.Id,
                TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        _ = (await harness.Client.ReleaseInputLeaseAsync(
            new ReleaseInputLeaseRequest(harness.SessionId, lease.Id),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = harness.Client.WatchAsync(
                new WatchSessionRequest(harness.SessionId, 0),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());
        var resync = Assert.IsType<SessionStreamItem.ResynchronizationRequired>(events.Current);
        Assert.Equal(resync.Snapshot.LastSequence, resync.ResumeAfterSequence);
    }

    [Fact]
    public async Task StaleRevisionAndElapsedDeadlineDoNotMutateSession()
    {
        await using var harness = new SessionHostTestHarness();
        var opened = await harness.OpenAsync();
        var staleContext = harness.HumanContext(expectedRevision: opened.Descriptor.Revision - 1);
        var stale = await harness.Client.AttachAsync(
            new AttachSessionRequest(
                harness.SessionId,
                harness.ClientId,
                AttachmentKind.ReadOnly,
                ViewportDescriptor.Empty,
                SessionHostTestHarness.AllCapabilities()),
            staleContext,
            CancellationToken.None);
        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);

        var deadline = await harness.Client.AttachAsync(
            new AttachSessionRequest(
                harness.SessionId,
                new ClientId("late-client"),
                AttachmentKind.ReadOnly,
                ViewportDescriptor.Empty,
                SessionHostTestHarness.AllCapabilities()),
            harness.HumanContext(deadline: harness.Clock.GetUtcNow()),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.DeadlineExceeded, deadline.Error().Code);

        var snapshot = (await harness.Client.GetSnapshotAsync(
            harness.SessionId,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Empty(snapshot.Attachments);
    }

    [Fact]
    public async Task IdempotentOpenReplaysOnceAndRejectsDifferentFingerprint()
    {
        await using var harness = new SessionHostTestHarness();
        var key = new IdempotencyKey("open-once");
        var context = harness.HumanContext(idempotencyKey: key);
        var first = await harness.OpenAsync(context);
        var replay = await harness.OpenAsync(context);

        Assert.Equal(first, replay);
        Assert.Equal(1, harness.Factory.CreateCount);

        var reused = await harness.Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                new SessionId("different-session"),
                first.Descriptor.Owner with { PanelId = new PanelInstanceId("different-panel") },
                "different",
                new TerminalLaunchRequest("/different")),
            context,
            CancellationToken.None);
        Assert.Equal(HostErrorCode.IdempotencyKeyReused, reused.Error().Code);
        Assert.Equal(1, harness.Factory.CreateCount);
    }
}
