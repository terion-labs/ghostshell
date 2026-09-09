namespace Asura.TerminalAcceptance.Tests;

public sealed class AcceptanceCatalogTests
{
    [Fact]
    public void Catalog_covers_the_named_host_terminal_matrix_without_a_skip_state()
    {
        Assert.Equal(
            [
                "named-interactive-host",
                "packaged-real-pty-backend",
                "interactive-tui",
                "unicode-cell-fidelity",
                "ime-composition",
                "resize-grid",
                "mouse-reporting",
                "clipboard-safety",
                "alternate-screen",
                "quick-terminal",
                "sleep-wake",
                "pty-lifecycle",
            ],
            AcceptanceCatalog.All.Select(check => check.Id), StringComparer.Ordinal);
        Assert.Equal(
            AcceptanceCatalog.All.Count,
            AcceptanceCatalog.All.Select(check => check.Title).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(
            Enum.GetNames<AcceptanceStatus>(),
            name => string.Equals(name, "Skip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_check_has_platform_specific_operator_instructions()
    {
        foreach (var check in AcceptanceCatalog.All)
        {
            Assert.NotEmpty(check.InstructionsFor(TargetPlatform.Windows));
            Assert.NotEmpty(check.InstructionsFor(TargetPlatform.LinuxX11));
            Assert.NotEqual(
                check.InstructionsFor(TargetPlatform.Windows),
                check.InstructionsFor(TargetPlatform.LinuxX11), StringComparer.Ordinal);
        }
    }

    [Theory]
    [InlineData("unicode-cell-fidelity", "combining")]
    [InlineData("ime-composition", "preedit")]
    [InlineData("resize-grid", "PTY")]
    [InlineData("mouse-reporting", "wheel")]
    [InlineData("clipboard-safety", "OSC 52")]
    [InlineData("alternate-screen", "scrollback")]
    [InlineData("sleep-wake", "sleep")]
    [InlineData("pty-lifecycle", "process")]
    public void Critical_checks_name_the_behavior_that_must_be_observed(
        string checkId,
        string expectedText)
    {
        var check = Assert.Single(AcceptanceCatalog.All, check => string.Equals(check.Id, checkId, StringComparison.Ordinal));

        Assert.Contains(expectedText, check.CommonInstructions, StringComparison.OrdinalIgnoreCase);
    }
}
