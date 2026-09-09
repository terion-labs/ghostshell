using Asura.Application;
using Asura.Core;

namespace Asura.Browser.Tests;

public sealed class BrowserPanelAutomationTests
{
    [Fact]
    public async Task SessionTranslatesLogicalBindingAndProjectsFreshInputEpoch()
    {
        var address = new BrowserAddress(new Uri("https://example.test/page"));
        await using var session = new BrowserPanelSession(
            new SessionId("browser"),
            address,
            TimeProvider.System,
            BrowserCapabilityProfile.FullAutomationCandidate);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.SetAutomationViewport();
        var logicalBinding = BrowserAutomationBinding.FromState(session.State);

        var result = await session.DispatchMouseWithinOriginAsync(
            new BrowserMouseRequest(
                session.Id,
                logicalBinding,
                BrowserMouseAction.Click,
                10,
                10,
                BrowserMouseButton.Left,
                clickCount: 1),
            BrowserNavigationOrigin.FromAddress(session.State.Address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, renderer.MouseCount);
        Assert.Equal(renderer.State.DocumentRevision,
            renderer.LastAutomationBinding!.Document.DocumentRevision);
        Assert.Equal(logicalBinding, result.Value!.SourceBinding);
        Assert.Equal(logicalBinding.InputEpoch + 1, session.State.InputEpoch);
    }

    [Fact]
    public async Task SessionRejectsStaleViewportBeforeRendererDispatch()
    {
        var address = new BrowserAddress(new Uri("https://example.test/page"));
        await using var session = new BrowserPanelSession(
            new SessionId("browser"),
            address,
            TimeProvider.System,
            BrowserCapabilityProfile.FullAutomationCandidate);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.SetAutomationViewport();
        var stale = BrowserAutomationBinding.FromState(session.State);
        renderer.SetAutomationViewport(width: 799);

        var result = await session.EvaluateWithinOriginAsync(
            new BrowserEvaluateRequest(session.Id, stale, "1 + 1"),
            BrowserNavigationOrigin.FromAddress(session.State.Address),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.NavigationStateChanged, result.Error?.Code);
        Assert.Equal(0, renderer.EvaluateCount);
    }
}
