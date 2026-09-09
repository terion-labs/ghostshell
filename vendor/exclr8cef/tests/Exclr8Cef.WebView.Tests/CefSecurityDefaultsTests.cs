namespace Exclr8Cef.WebView.Tests;

public sealed class CefSecurityDefaultsTests
{
    [Fact]
    public void JavascriptBridgeIsDisabledByDefault()
    {
        var settings = new Cef.CefSettings();

        Assert.False(settings.EnableJavascriptBridge);
    }

    [Fact]
    public void MainArgumentsGainProgramName()
    {
        string[] normalized = Cef.NormalizeCommandLineArguments(
            ["--type=renderer", "--field-trial-handle=123"],
            ["/opt/asura/Asura", "host-argument"]);

        Assert.Equal(
            ["/opt/asura/Asura", "--type=renderer", "--field-trial-handle=123"],
            normalized);
    }

    [Fact]
    public void FullProcessArgumentsAreNotPrefixedTwice()
    {
        string[] fullArguments = ["/opt/asura/Asura", "--type=renderer"];

        string[] normalized = Cef.NormalizeCommandLineArguments(
            fullArguments,
            ["/opt/asura/Asura"]);

        Assert.Equal(fullArguments, normalized);
        Assert.NotSame(fullArguments, normalized);
    }

    [Fact]
    public void NullArgumentsUseFullProcessArguments()
    {
        string[] processArguments = ["/opt/asura/Asura", "--host-option"];

        string[] normalized = Cef.NormalizeCommandLineArguments(null, processArguments);

        Assert.Equal(processArguments, normalized);
        Assert.NotSame(processArguments, normalized);
    }

    [Fact]
    public void EmptyMainArgumentsStillContainProgramName()
    {
        string[] normalized = Cef.NormalizeCommandLineArguments(
            [],
            ["/opt/asura/Asura"]);

        Assert.Equal(["/opt/asura/Asura"], normalized);
    }

    [Fact]
    public void BeforeBrowseCarriesMetadataAndCanCancel()
    {
        var browser = new CefBrowser(42);
        BeforeBrowseEventArgs? observed = null;
        browser.BeforeBrowse += (_, args) =>
        {
            observed = args;
            args.Cancel = true;
        };

        var args = new BeforeBrowseEventArgs(
            "https://untrusted.example/redirected",
            userGesture: true,
            isRedirect: true);
        bool canceled = browser.RaiseBeforeBrowse(args);

        Assert.Same(args, observed);
        Assert.Equal("https://untrusted.example/redirected", args.Url);
        Assert.True(args.UserGesture);
        Assert.True(args.IsRedirect);
        Assert.True(args.Cancel);
        Assert.True(canceled);
    }

    [Fact]
    public void BeforeBrowseFailsClosedWhenHandlerThrows()
    {
        var browser = new CefBrowser(42);
        browser.BeforeBrowse += (_, _) => throw new InvalidOperationException("policy failed");

        bool canceled = browser.RaiseBeforeBrowse(
            new BeforeBrowseEventArgs("https://example.test", false, false));

        Assert.True(canceled);
    }
}
