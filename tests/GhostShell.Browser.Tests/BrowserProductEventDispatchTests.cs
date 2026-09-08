using GhostShell.Application;

namespace GhostShell.Browser.Tests;

public sealed class BrowserProductEventDispatchTests
{
    [Fact]
    public void Repeated_page_notifications_schedule_one_drain_with_the_latest_state()
    {
        Queue<Action> work = [];
        List<BrowserProductEvent> delivered = [];
        using var dispatch = new BrowserProductEventDispatch(work.Enqueue, delivered.Add);
        for (var index = 0; index < 10_000; index++)
        {
            dispatch.Publish(new BrowserProductEvent.JavaScriptDialogBlocked(BrowserJavaScriptDialogKind.Alert, index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            dispatch.Publish(new BrowserProductEvent.FindUpdated(index, 1, index == 9_999));
        }

        Assert.Single(work);
        work.Dequeue()();
        Assert.Empty(work);
        Assert.Equal(2, delivered.Count);
        Assert.Equal("9999", Assert.IsType<BrowserProductEvent.JavaScriptDialogBlocked>(delivered[0]).Message);
        Assert.True(Assert.IsType<BrowserProductEvent.FindUpdated>(delivered[1]).IsFinal);
    }

    [Fact]
    public void Download_terminals_and_security_notices_survive_progress_floods()
    {
        Queue<Action> work = [];
        List<BrowserProductEvent> delivered = [];
        using var dispatch = new BrowserProductEventDispatch(work.Enqueue, delivered.Add);
        dispatch.Publish(new BrowserProductEvent.DownloadRequested(1, "one", 10));
        dispatch.Publish(new BrowserProductEvent.DownloadRequested(2, "two", 10));
        for (var index = 0; index < 10_000; index++)
        {
            dispatch.Publish(new BrowserProductEvent.DownloadProgressed(1, "one", index, null, null));
            dispatch.Publish(new BrowserProductEvent.DownloadProgressed(2, "two", index, null, null));
        }
        dispatch.Publish(new BrowserProductEvent.DownloadCompleted(1, "one"));
        dispatch.Publish(new BrowserProductEvent.DownloadCancelled(2));
        dispatch.Publish(new BrowserProductEvent.DownloadProgressed(1, "one", 0, null, null));
        dispatch.Publish(new BrowserProductEvent.PermissionDenied("https://example.test", BrowserPermissionKind.Camera));
        dispatch.Publish(new BrowserProductEvent.CertificateRejected(BrowserAddress.Blank, BrowserCertificateErrorKind.UntrustedAuthority, "", ""));

        Assert.Single(work);
        work.Dequeue()();
        Assert.Equal(6, delivered.Count);
        Assert.Equal(2, delivered.OfType<BrowserProductEvent.DownloadRequested>().Count());
        Assert.Single(delivered.OfType<BrowserProductEvent.DownloadCompleted>());
        Assert.Single(delivered.OfType<BrowserProductEvent.DownloadCancelled>());
        Assert.Single(delivered.OfType<BrowserProductEvent.PermissionDenied>());
        Assert.Single(delivered.OfType<BrowserProductEvent.CertificateRejected>());
        Assert.Empty(delivered.OfType<BrowserProductEvent.DownloadProgressed>());
    }

    [Fact]
    public void Distinct_downloads_are_not_dropped_and_drain_yields_between_batches()
    {
        Queue<Action> work = [];
        List<BrowserProductEvent> delivered = [];
        using var dispatch = new BrowserProductEventDispatch(work.Enqueue, delivered.Add);
        for (var index = 0; index < 100; index++)
        {
            dispatch.Publish(new BrowserProductEvent.DownloadCompleted(index, "fixture"));
        }
        Assert.Single(work);
        work.Dequeue()();
        Assert.Equal(64, delivered.Count);
        Assert.Single(work);
        work.Dequeue()();
        Assert.Equal(100, delivered.Count);
        Assert.Empty(work);
    }

    [Fact]
    public void Reentrant_notifications_remain_single_pending_and_disposal_discards_work()
    {
        Queue<Action> work = [];
        List<BrowserProductEvent> delivered = [];
        BrowserProductEventDispatch? dispatch = null;
        dispatch = new(work.Enqueue, item =>
        {
            delivered.Add(item);
            dispatch!.Publish(new BrowserProductEvent.FindUpdated(1, 1, true));
            throw new InvalidOperationException("fixture subscriber failure");
        });
        dispatch.Publish(new BrowserProductEvent.DownloadCancelled(1));
        work.Dequeue()();
        Assert.Single(delivered);
        Assert.Single(work);
        dispatch.Dispose();
        work.Dequeue()();
        dispatch.Publish(new BrowserProductEvent.DownloadCancelled(2));
        Assert.Single(delivered);
        Assert.Empty(work);
    }
}
