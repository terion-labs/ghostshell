namespace Asura.AccessibilityAcceptance.Tests;

public sealed class AcceptanceCatalogTests
{
    [Fact]
    public void Catalog_has_fixed_unique_checks_and_assertions()
    {
        Assert.Equal(12, AcceptanceCatalog.All.Count);
        Assert.Equal(
            AcceptanceCatalog.All.Count,
            AcceptanceCatalog.All.Select(check => check.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(AcceptanceCatalog.All, check =>
        {
            Assert.NotEmpty(check.Assertions);
            Assert.Equal(
                check.Assertions.Count,
                check.Assertions.Select(assertion => assertion.Id).Distinct(StringComparer.Ordinal).Count());
        });
        Assert.True(EvidenceValidator.IsLowercaseSha256(AcceptanceCatalog.Digest));
    }

    [Fact]
    public void Macos_scale_check_names_the_production_application_setting()
    {
        var scaleCheck = Assert.Single(
            AcceptanceCatalog.All,
            check => string.Equals(check.Id, "scale-reflow-contrast-status", StringComparison.Ordinal));

        Assert.Contains("Application text size", scaleCheck.MacOSInstructions);
        Assert.Contains("200% or 250%", scaleCheck.MacOSInstructions);
    }

    [Theory]
    [InlineData("MacOS", "VoiceOver")]
    [InlineData("Windows", "Narrator")]
    [InlineData("LinuxX11", "Orca")]
    public void Platform_has_one_fixed_screen_reader(
        string platformName,
        string expectedName)
    {
        var platform = Enum.Parse<TargetPlatform>(platformName);
        var expected = Enum.Parse<ScreenReaderKind>(expectedName);
        Assert.Equal(expected, AcceptanceEvidence.ScreenReaderFor(platform));
    }

    [Fact]
    public void Assertion_and_overall_results_fail_closed()
    {
        var pass = new AssertionObservation("one", AcceptanceStatus.Pass);
        var blocked = new AssertionObservation("two", AcceptanceStatus.Blocked);
        var fail = new AssertionObservation("three", AcceptanceStatus.Fail);

        Assert.Equal(AcceptanceStatus.Pass, CheckObservation.ResolveResult([pass]));
        Assert.Equal(AcceptanceStatus.Blocked, CheckObservation.ResolveResult([pass, blocked]));
        Assert.Equal(AcceptanceStatus.Fail, CheckObservation.ResolveResult([pass, blocked, fail]));
        Assert.Equal(AcceptanceStatus.Blocked, CheckObservation.ResolveResult([]));
    }

    [Theory]
    [InlineData("Pass", true, "Pass")]
    [InlineData("Pass", false, "Fail")]
    [InlineData("Blocked", true, "Blocked")]
    [InlineData("Blocked", false, "Fail")]
    [InlineData("Fail", true, "Fail")]
    [InlineData("Fail", false, "Fail")]
    public void Runner_boundary_can_downgrade_but_never_upgrade_operator_result(
        string operatorName,
        bool boundaryPassed,
        string expectedName)
    {
        var operatorResult = Enum.Parse<AcceptanceStatus>(operatorName);
        var expected = Enum.Parse<AcceptanceStatus>(expectedName);

        Assert.Equal(
            expected,
            AcceptanceBoundary.Constrain(operatorResult, boundaryPassed));
    }

    [Theory]
    [InlineData("operator-observed", "operator-observed+runner-boundary")]
    [InlineData("operator-observed+runner-boundary", "operator-observed+runner-boundary")]
    [InlineData("runner-observed-boundary", "runner-observed-boundary")]
    public void Runner_boundary_preserves_the_source_of_the_observation(
        string current,
        string expected)
    {
        Assert.Equal(expected, AcceptanceBoundary.AddRunnerObservation(current));
    }

    [Fact]
    public void Parser_requires_complete_named_host_identity()
    {
        var options = RunOptions.Parse(
        [
            "--platform", "LinuxX11",
            "--screen-reader", "Orca",
            "--system-name", "ubuntu-a11y-lab-01",
            "--observer", "operator-02",
            "--build-label", "rc-20260723-1",
            "--package", "/opt/asura",
        ]);

        Assert.Equal(TargetPlatform.LinuxX11, options.Platform);
        Assert.Equal(ScreenReaderKind.Orca, options.ScreenReader);
        Assert.Equal("ubuntu-a11y-lab-01", options.SystemName);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Parser_rejects_ambiguous_or_weakened_runs(string[] arguments)
    {
        Assert.Throws<UsageException>(() => RunOptions.Parse(arguments));
    }

    public static TheoryData<string[]> InvalidArguments
    {
        get
        {
            var data = new TheoryData<string[]>
            {
                FullArguments("Windows", "VoiceOver"),
                FullArguments("LinuxX11", "Orca", systemName: "linux"),
                FullArguments("MacOS", "VoiceOver", observer: "/Users/alice"),
                ([.. FullArguments("MacOS", "VoiceOver"), "--observer", "duplicate"]),
                (["--unknown", "value"])
            };
            return data;
        }
    }

    private static string[] FullArguments(
        string platform,
        string reader,
        string systemName = "a11y-lab-01",
        string observer = "operator-01") =>
    [
        "--platform", platform,
        "--screen-reader", reader,
        "--system-name", systemName,
        "--observer", observer,
        "--build-label", "rc-1",
        "--package", "/opt/asura",
    ];
}
