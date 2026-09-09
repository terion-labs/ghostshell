using Asura.Application;

namespace Asura.Browser.Tests;

public sealed class CefNavigationStateTests
{
    [Fact]
    public void OnlyAnObservedRedirectMakesItsPreviousAbortNonTerminal()
    {
        var navigation = Navigation();
        navigation.AdmitLeg("https://example.test/start", isRedirect: false);
        navigation.AdmitLeg("https://example.test/final", isRedirect: true);

        Assert.True(navigation.ShouldAwaitCompletionAfterAbort(
            "https://example.test/start"));
        Assert.False(navigation.ShouldAwaitCompletionAfterAbort(
            "https://example.test/start"));
        Assert.False(navigation.ShouldAwaitCompletionAfterAbort(
            "https://example.test/final"));
    }

    [Fact]
    public void AnUnknownAbortIsTerminalEvenWhileANavigationIsActive()
    {
        var navigation = Navigation();
        navigation.AdmitLeg("https://example.test/current", isRedirect: false);

        Assert.False(navigation.ShouldAwaitCompletionAfterAbort(
            "https://example.test/cancelled"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void StopAndRejectionKeepRedirectAbortsTerminal(
        bool wasStopped,
        bool wasRejected)
    {
        var navigation = Navigation();
        navigation.AdmitLeg("https://example.test/start", isRedirect: false);
        navigation.AdmitLeg("https://example.test/final", isRedirect: true);
        navigation.StopRequested = wasStopped;
        navigation.WasRejected = wasRejected;

        Assert.False(navigation.ShouldAwaitCompletionAfterAbort(
            "https://example.test/start"));
    }

    [Fact]
    public void AStoppedBootstrapNavigationCannotDispatchItsQueuedTarget()
    {
        var navigation = Navigation();

        navigation.StopRequested = true;

        Assert.False(navigation.MayDispatchQueuedNavigation);
    }

    [Fact]
    public void ALocalPermitAppearsOnlyForAnAdmittedProvisionalDocument()
    {
        var localPage = BrowserAddress.ForLocalFile(LocalPath("report.html"));
        var policy = CefLocalDocumentAccessPolicy.None;

        Assert.Null(policy.PermittedPage);

        var admitted = policy.Admit(localPage);

        Assert.Same(localPage, admitted.PermittedPage);
        Assert.Null(admitted.RollBack().PermittedPage);
    }

    [Fact]
    public void AnAdmittedRemoteTransitionClearsThenRestoresACommittedLocalPermitOnFailure()
    {
        var localPage = BrowserAddress.ForLocalFile(LocalPath("report.html"));
        var remotePage = Address("https://example.test/page");
        var committedLocal = CefLocalDocumentAccessPolicy.None
            .Admit(localPage)
            .Complete(isSuccess: true, localPage);

        var provisionalRemote = committedLocal.Admit(remotePage);

        Assert.Null(provisionalRemote.PermittedPage);
        Assert.Same(
            localPage,
            provisionalRemote.RollBack().PermittedPage);
    }

    [Fact]
    public void ASuccessfulRemoteTransitionDoesNotRetainTheOldLocalPermit()
    {
        var localPage = BrowserAddress.ForLocalFile(LocalPath("report.html"));
        var remotePage = Address("https://example.test/page");
        var committedLocal = CefLocalDocumentAccessPolicy.None
            .Admit(localPage)
            .Complete(isSuccess: true, localPage);

        var committedRemote = committedLocal
            .Admit(remotePage)
            .Complete(isSuccess: true, remotePage);

        Assert.Null(committedRemote.PermittedPage);
        Assert.Null(committedRemote.CommittedPage);
    }

    private static CefBrowserView.ActiveNativeNavigation Navigation() =>
        new(
            generation: 1,
            pendingAddress: Address("https://example.test/start"));

    private static BrowserAddress Address(string value) =>
        BrowserAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException("The test address is invalid.");

    private static string LocalPath(string name) =>
        OperatingSystem.IsWindows()
            ? $@"C:\previews\{name}"
            : $"/previews/{name}";
}
