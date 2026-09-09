namespace Asura.AccessibilityAcceptance.Tests;

public sealed class AcceptancePrompterTests
{
    [Fact]
    public void Prompt_requires_every_assertion_before_check_passes()
    {
        var check = AcceptanceCatalog.All[0];
        var input = new StringReader(
            "PASS\nPASS\nPASS\nConcrete synthetic host evidence.\n");

        var observation = new AcceptancePrompter(input, TextWriter.Null).Prompt(
            check,
            TargetPlatform.MacOS);

        Assert.Equal(AcceptanceStatus.Pass, observation.Result);
        Assert.All(
            observation.Assertions,
            assertion => Assert.Equal(AcceptanceStatus.Pass, assertion.Result));
        Assert.Equal("operator-observed", observation.ObservationMode);
    }

    [Fact]
    public void One_failed_assertion_fails_the_check()
    {
        var check = AcceptanceCatalog.All[0];
        var input = new StringReader(
            "PASS\nFAIL\nPASS\nThe session remained locked during testing.\n");

        var observation = new AcceptancePrompter(input, TextWriter.Null).Prompt(
            check,
            TargetPlatform.Windows);

        Assert.Equal(AcceptanceStatus.Fail, observation.Result);
    }

    [Fact]
    public void Runner_assertions_are_preserved_without_operator_reentry()
    {
        var check = AcceptanceCatalog.All[1];
        var input = new StringReader(
            "PASS\nPASS\nConcrete synthetic reader evidence.\n");

        var observation = new AcceptancePrompter(input, TextWriter.Null).Prompt(
            check,
            TargetPlatform.LinuxX11,
            new Dictionary<string, AcceptanceStatus>(StringComparer.Ordinal)
            {
                ["expected-reader-running"] = AcceptanceStatus.Pass,
            });

        Assert.Equal(AcceptanceStatus.Pass, observation.Result);
        Assert.Equal(
            "operator-observed+runner-boundary",
            observation.ObservationMode);
    }

    [Fact]
    public void Macos_text_scale_result_comes_from_the_operator()
    {
        var check = Assert.Single(
            AcceptanceCatalog.All,
            item => string.Equals(item.Id, "scale-reflow-contrast-status", StringComparison.Ordinal));
        var input = new StringReader(
            "PASS\nPASS\nPASS\nPASS\nObserved 250 percent application reflow.\n");

        var observation = new AcceptancePrompter(input, TextWriter.Null).Prompt(
            check,
            TargetPlatform.MacOS);

        Assert.Equal(AcceptanceStatus.Pass, observation.Result);
        Assert.Equal("operator-observed", observation.ObservationMode);
        Assert.Equal(
            AcceptanceStatus.Pass,
            Assert.Single(
                observation.Assertions,
                assertion => string.Equals(assertion.Id, "high-text-scale-exercised", StringComparison.Ordinal)).Result);
    }

    [Fact]
    public void Ended_input_blocks_the_complete_remaining_matrix()
    {
        var check = AcceptanceCatalog.All[5];
        var observation = new AcceptancePrompter(
            new StringReader("PASS\n"),
            TextWriter.Null).Prompt(check, TargetPlatform.MacOS);

        Assert.Equal(AcceptanceStatus.Blocked, observation.Result);
        Assert.Equal(check.Assertions.Count, observation.Assertions.Count);
        Assert.Equal(AcceptanceStatus.Pass, observation.Assertions[0].Result);
        Assert.All(
            observation.Assertions.Skip(1),
            assertion => Assert.Equal(AcceptanceStatus.Blocked, assertion.Result));
    }

    [Fact]
    public void Ended_input_at_required_note_cannot_leave_a_passing_check()
    {
        var check = AcceptanceCatalog.All[0];
        var observation = new AcceptancePrompter(
            new StringReader("PASS\nPASS\nPASS\n"),
            TextWriter.Null).Prompt(check, TargetPlatform.MacOS);

        Assert.Equal(AcceptanceStatus.Blocked, observation.Result);
        Assert.All(
            observation.Assertions,
            assertion => Assert.Equal(AcceptanceStatus.Blocked, assertion.Result));
        Assert.Equal(
            "operator-observed+runner-boundary",
            observation.ObservationMode);
    }
}
