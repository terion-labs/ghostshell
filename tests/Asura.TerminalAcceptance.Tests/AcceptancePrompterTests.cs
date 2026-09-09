namespace Asura.TerminalAcceptance.Tests;

public sealed class AcceptancePrompterTests
{
    [Fact]
    public void End_of_input_is_blocked_instead_of_being_treated_as_a_pass()
    {
        var output = new StringWriter();
        var prompter = new AcceptancePrompter(new StringReader(string.Empty), output);

        var observation = prompter.Prompt(
            AcceptanceCatalog.All[0],
            TargetPlatform.Windows);

        Assert.Equal(AcceptanceStatus.Blocked, observation.Result);
        Assert.Equal("runner-observed-boundary", observation.ObservationMode);
        Assert.Contains("input ended", observation.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_status_and_short_note_are_reprompted()
    {
        var input = new StringReader(
            "SKIP\nPASS\nshort\nvim 9.1 redraw and clean exit succeeded\n");
        var output = new StringWriter();
        var prompter = new AcceptancePrompter(input, output);

        var observation = prompter.Prompt(
            AcceptanceCatalog.All[2],
            TargetPlatform.LinuxX11);

        Assert.Equal(AcceptanceStatus.Pass, observation.Result);
        Assert.Equal("vim 9.1 redraw and clean exit succeeded", observation.Notes);
        Assert.Contains("There is no SKIP state", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("at least 12 characters", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_note_fields_are_redacted_before_the_observation_is_returned()
    {
        var input = new StringReader(
            "FAIL\ntoken=top-secret at 192.0.2.20 while vim redraw failed\n");
        var output = new StringWriter();
        var prompter = new AcceptancePrompter(input, output);

        var observation = prompter.Prompt(
            AcceptanceCatalog.All[2],
            TargetPlatform.Windows);

        Assert.Equal(AcceptanceStatus.Fail, observation.Result);
        Assert.DoesNotContain("top-secret", observation.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.20", observation.Notes, StringComparison.Ordinal);
        Assert.True(observation.RedactionsApplied >= 2);
    }
}
