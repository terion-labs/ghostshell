namespace Asura.Architecture.Tests;

public sealed class CefPresentationShutdownContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Desktop_tears_down_presentation_before_finalization_and_cef_shutdown()
    {
        var program = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "Asura.Desktop", "Program.cs"));

        var exitRegistration = RequiredIndexOf(
            program,
            "lifetime.Exit += (_, _) =>");
        var lifetimeStart = RequiredIndexOf(program, "lifetime.Start(args)");
        var mainThreadFallback = RequiredIndexOf(
            program,
            "TeardownPresentationOrReport(mainWindowViewModel);",
            lifetimeStart);
        var finalization = RequiredIndexOf(
            program,
            "FinalizeAsync(services).GetAwaiter().GetResult();",
            mainThreadFallback);
        var finallyBlock = RequiredIndexOf(
            program,
            "instanceCoordinator.StopAcceptingActivations();",
            finalization);
        var failureFallback = RequiredIndexOf(
            program,
            "TeardownPresentationOrReport(mainWindowViewModel);",
            finallyBlock);
        var quiescenceFallback = RequiredIndexOf(
            program,
            "QuiescePresentationOrReport(services);",
            failureFallback);
        var cefShutdown = RequiredIndexOf(
            program,
            "BrowserEngineRuntime.Shutdown(",
            quiescenceFallback);
        var engineSeal = RequiredIndexOf(program,
            "BrowserEngineRuntime.SealStateAfterShutdownAsync(", cefShutdown);
        var serviceDisposal = RequiredIndexOf(program,
            "services.DisposeAsync()", engineSeal);

        Assert.True(exitRegistration < lifetimeStart);
        Assert.True(lifetimeStart < mainThreadFallback);
        Assert.True(mainThreadFallback < finalization);
        Assert.True(finalization < failureFallback);
        Assert.True(failureFallback < quiescenceFallback);
        Assert.True(quiescenceFallback < cefShutdown);
        Assert.True(cefShutdown < engineSeal);
        Assert.True(engineSeal < serviceDisposal);
    }

    [Fact]
    public void Desktop_does_not_attribute_unrelated_shutdown_failures_to_Chromium()
    {
        var program = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "Asura.Desktop", "Program.cs"));

        var initialization = RequiredIndexOf(
            program,
            "BrowserEngineRuntime.Initialize(");
        var chromiumFailure = RequiredIndexOf(
            program,
            "desktop.cef-initialize.failed",
            initialization);
        var lifetime = RequiredIndexOf(program, "lifetime.Start(args)", chromiumFailure);
        var desktopFailure = RequiredIndexOf(
            program,
            "desktop.runtime.failed",
            lifetime);

        Assert.True(initialization < chromiumFailure);
        Assert.True(chromiumFailure < lifetime);
        Assert.True(lifetime < desktopFailure);
    }

    [Fact]
    public void Desktop_finalization_drains_all_application_windows_before_disposal()
    {
        var program = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "Asura.Desktop", "Program.cs"));
        var application = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "Asura.App", "App.axaml.cs"));

        Assert.Contains(
            "await application.QuiesceForShutdownAsync(cancellationToken)",
            program,
            StringComparison.Ordinal);
        var exitHandler = RequiredIndexOf(application, "private void OnDesktopExit(");
        var exitGuard = RequiredIndexOf(
            application,
            "_desktopExitStarted = true;",
            exitHandler);
        var asyncQuiescence = RequiredIndexOf(
            application,
            "public async Task QuiesceForShutdownAsync(",
            exitGuard);
        var additionalDisposal = RequiredIndexOf(
            application,
            "viewModel.Dispose();",
            asyncQuiescence);
        var closedHandler = RequiredIndexOf(
            application,
            "private void OnMainWindowClosed(",
            additionalDisposal);
        var closedGuard = RequiredIndexOf(
            application,
            "if (!_desktopExitStarted",
            closedHandler);

        Assert.True(exitGuard < asyncQuiescence);
        Assert.True(asyncQuiescence < additionalDisposal);
        Assert.True(additionalDisposal < closedGuard);
    }

    private static int RequiredIndexOf(
        string source,
        string value,
        int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected Program.cs to contain '{value}'.");
        return index;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Asura repository root.");
    }
}
