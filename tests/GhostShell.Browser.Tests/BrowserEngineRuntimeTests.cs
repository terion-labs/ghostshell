using Exclr8Cef;
using GhostShell.SecurityCampaign.Tests;

namespace GhostShell.Browser.Tests;

public sealed class BrowserEngineRuntimeTests
{
    [Fact]
    public void RuntimeDisablesEveryUnusedOnDeviceModelStartupPath()
    {
        Assert.Equal(
            "OptimizationGuideOnDeviceModel,LogOnDeviceMetricsOnStartup",
            BrowserEngineRuntime.DisabledChromiumFeatures);
    }

    [Fact]
    public void RuntimeRoutesChromeAuthenticationThroughCefCallbacks()
    {
        Assert.Equal(
            "disable-chrome-login-prompt",
            BrowserEngineRuntime.DisableChromeLoginPromptSwitch);
    }

    [Fact]
    public void ExactPinnedRuntimeVersionIsAccepted()
    {
        BrowserEngineRuntime.ValidateVersions(
            new CefVersions(
                "0.8.0-ghostshell.7",
                "150.0.9",
                "150.0.7871.46"));
    }

    [Theory]
    [InlineData("149.0.0", "150.0.7871.46")]
    [InlineData("150.0.9", "151.0.0.0")]
    public void RuntimeVersionMismatchFailsClosed(
        string cefVersion,
        string chromiumVersion)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BrowserEngineRuntime.ValidateVersions(
                new CefVersions(
                    "0.8.0-ghostshell.7",
                    cefVersion,
                    chromiumVersion)));

        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public void UnpatchedShimVersionFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BrowserEngineRuntime.ValidateVersions(
                new CefVersions(
                    "0.8.0",
                    "150.0.9",
                    "150.0.7871.46")));
    }

    [Fact]
    public void RuntimeOptionsNormalizePaths()
    {
        var options = new BrowserEngineRuntimeOptions(
            Path.Combine("relative", "profile"),
            Path.Combine("relative", "logs", "cef.log"),
            "1.0.0");

        Assert.True(Path.IsPathFullyQualified(options.ProfileDirectory));
        Assert.True(Path.IsPathFullyQualified(options.LogFilePath));
        Assert.Equal("1.0.0", options.ProductVersion);
    }

    [Fact]
    public void RuntimeSettingsProvideAParentForDurableRequestContexts()
    {
        var options = new BrowserEngineRuntimeOptions(
            Path.Combine("relative", "profile"),
            Path.Combine("relative", "logs", "cef.log"),
            "1.0.0");

        var settings = BrowserEngineRuntime.CreateSettings(options);

        Assert.Null(settings.CachePath);
        Assert.Equal(options.ProfileDirectory, settings.RootCachePath);
        Assert.True(settings.PersistSessionCookies);
        Assert.Equal(Cef.CefLogSeverity.Disable, settings.LogSeverity);
    }

    [Fact(DisplayName = "secrecy.cef-console-adapter managed CEF callback redacts shared canaries")]
    [Trait("SecurityCampaignCase", "secrecy.cef-console-adapter")]
    public void ConsoleCallbackDropsPageControlledCanaries()
    {
        var diagnostics = new List<string>();
        var message = new ConsoleMessageEventArgs(
            Cef.CefLogSeverity.Warning,
            SecurityCampaignCanaries.Joined,
            $"https://example.test/{SecurityCampaignCanaries.Joined}",
            42);

        CefConsoleMessagePolicy.Handle(message, diagnostics.Add);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(
            "[ghostshell:browser-console] code=browser.console.warning line=42",
            diagnostic);
        Assert.All(
            SecurityCampaignCanaries.Values,
            canary => Assert.DoesNotContain(canary, diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void MacRuntimeUsesMockSafeStorageForTheAppEncryptedRuntimeTree()
    {
        Assert.Equal(
            "use-mock-keychain",
            BrowserEngineRuntime.GetMacOsSafeStorageSwitch(isMacOs: true));
        Assert.Null(BrowserEngineRuntime.GetMacOsSafeStorageSwitch(isMacOs: false));
    }

    [Fact]
    public void StartupPreservesRecoveredGlobalStateAndRecoveryContexts()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "Default");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Cookies"), "existing-cookie-db");
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        File.WriteAllText(Path.Combine(root, "runtime", "Cache"), "cache");
        Directory.CreateDirectory(Path.Combine(root, "profiles", "global", "local"));
        File.WriteAllText(
            Path.Combine(root, "profiles", "global", "local", "History"),
            "history");
        File.WriteAllText(Path.Combine(root, "Local State"), "state");
        Directory.CreateDirectory(Path.Combine(root, "contexts", "orphan"));
        File.WriteAllText(
            Path.Combine(root, "contexts", "orphan", ".ghostshell-profile"),
            "recovery");
        try
        {
            BrowserEngineRuntime.PrepareProfileLayout(root);

            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(legacy));
            Assert.True(Directory.Exists(Path.Combine(root, "runtime")));
            Assert.True(Directory.Exists(Path.Combine(root, "profiles")));
            Assert.True(File.Exists(Path.Combine(root, "Local State")));
            Assert.True(File.Exists(
                Path.Combine(root, "contexts", "orphan", ".ghostshell-profile")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void StartupNeverFollowsAChildLinkInTheRecoveredRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "must-survive");
        File.WriteAllText(outsideFile, "outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        try
        {
            BrowserEngineRuntime.PrepareProfileLayout(root);

            Assert.True(File.Exists(outsideFile));
            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(Path.Combine(root, "linked")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public void LegacyCleanupFailsClosedWhenTheRootIsAFileSystemLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-cef-layout-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "must-survive");
        File.WriteAllText(outsideFile, "outside");
        Directory.CreateSymbolicLink(root, outside);
        try
        {
            Assert.Throws<IOException>(() =>
                BrowserEngineRuntime.PrepareProfileLayout(root));

            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }
}
