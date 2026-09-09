using Asura.Application;

namespace Asura.Browser.Tests;

/// <summary>
/// A previewed local page has to reach the embedded browser, and nothing else on this
/// machine may follow it in. The parser stays shut to <c>file:</c> — these
/// cover the one narrow opening beside it.
/// </summary>
public sealed class NativeLocalPageNavigationTests
{
    private static readonly string PageDirectory =
        OperatingSystem.IsWindows() ? @"C:\previews\" : "/previews/";

    [Fact]
    public void An_ordinary_address_resolves_with_no_local_page_permitted()
    {
        Assert.True(CefBrowserView.TryResolveNavigation(
            new Uri("https://example.test/page"), null, out var address));
        Assert.Equal("https://example.test/page", address!.Value.AbsoluteUri);
    }

    [Fact]
    public void A_local_page_is_refused_when_none_was_asked_for()
    {
        Assert.False(CefBrowserView.TryResolveNavigation(
            LocalUri("report.html"), null, out var address));
        Assert.Null(address);
    }

    [Fact]
    public void The_local_page_that_was_asked_for_is_admitted()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        Assert.True(CefBrowserView.TryResolveNavigation(
            LocalUri("report.html"), permitted, out var address));
        Assert.Same(permitted, address);
    }

    [Fact]
    public void A_different_local_page_is_refused_while_one_is_permitted()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        // The page's own markup deciding to load the shell's history file is
        // exactly what admitting a whole scheme would have allowed.
        Assert.False(CefBrowserView.TryResolveNavigation(
            LocalUri("secrets.html"), permitted, out var address));
        Assert.Null(address);
    }

    [Fact]
    public void A_permitted_page_does_not_admit_its_own_directory()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        Assert.False(CefBrowserView.TryResolveNavigation(
            new Uri(new Uri(PageDirectory).AbsoluteUri), permitted, out _));
    }

    [Fact]
    public void A_local_page_may_load_an_adjacent_stylesheet()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        Assert.True(CefBrowserView.IsPermittedLocalSubresource(
            LocalUri("report.css"),
            permitted));
    }

    [Fact]
    public void A_local_page_may_not_load_a_file_from_another_directory()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));
        var outside = OperatingSystem.IsWindows()
            ? new Uri(@"C:\secrets\history.txt")
            : new Uri("file:///secrets/history.txt");

        Assert.False(CefBrowserView.IsPermittedLocalSubresource(
            outside,
            permitted));
    }

    [Fact]
    public void A_navigation_to_nowhere_is_refused()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        Assert.False(CefBrowserView.TryResolveNavigation(
            null, permitted, out var address));
        Assert.Null(address);
    }

    [Fact]
    public void An_ordinary_address_still_resolves_while_a_local_page_is_permitted()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        // A previewed page may link out to the web; that is the parser's
        // decision as always, not the local-page permit's.
        Assert.True(CefBrowserView.TryResolveNavigation(
            new Uri("https://example.test/"), permitted, out var address));
        Assert.NotSame(permitted, address);
    }

    [Theory]
    [InlineData("report.css", Exclr8Cef.Cef.ResourceType.Stylesheet)]
    [InlineData("diagram.png", Exclr8Cef.Cef.ResourceType.Image)]
    [InlineData("body.woff2", Exclr8Cef.Cef.ResourceType.Font)]
    public void Restricted_preview_allows_only_explicit_adjacent_static_assets(
        string name,
        Exclr8Cef.Cef.ResourceType resourceType)
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        Assert.True(CefBrowserView.IsPermittedRestrictedHtmlPreviewRequest(
            LocalUri(name).AbsoluteUri,
            "GET",
            resourceType,
            permitted));
    }

    [Theory]
    [InlineData("https://example.test/tracker", Exclr8Cef.Cef.ResourceType.Image, "GET")]
    [InlineData("report.js", Exclr8Cef.Cef.ResourceType.Script, "GET")]
    [InlineData("frame.html", Exclr8Cef.Cef.ResourceType.SubFrame, "GET")]
    [InlineData("vector.svg", Exclr8Cef.Cef.ResourceType.Image, "GET")]
    [InlineData("report.html", Exclr8Cef.Cef.ResourceType.MainFrame, "POST")]
    public void Restricted_preview_denies_network_active_content_and_forms(
        string address,
        Exclr8Cef.Cef.ResourceType resourceType,
        string method)
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));
        var request = Uri.TryCreate(address, UriKind.Absolute, out var absolute)
            ? absolute
            : LocalUri(address);

        Assert.False(CefBrowserView.IsPermittedRestrictedHtmlPreviewRequest(
            request.AbsoluteUri,
            method,
            resourceType,
            permitted));
    }

    [Fact]
    public void Restricted_preview_allows_only_its_exact_main_document()
    {
        var permitted = BrowserAddress.ForLocalFile(PagePath("report.html"));

        Assert.True(CefBrowserView.IsPermittedRestrictedHtmlPreviewRequest(
            LocalUri("report.html").AbsoluteUri,
            "GET",
            Exclr8Cef.Cef.ResourceType.MainFrame,
            permitted));
        Assert.False(CefBrowserView.IsPermittedRestrictedHtmlPreviewRequest(
            LocalUri("other.html").AbsoluteUri,
            "GET",
            Exclr8Cef.Cef.ResourceType.MainFrame,
            permitted));
    }

    private static string PagePath(string name) => PageDirectory + name;

    private static Uri LocalUri(string name) => new(PagePath(name));
}
