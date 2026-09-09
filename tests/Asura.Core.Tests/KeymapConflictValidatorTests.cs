namespace Asura.Core.Tests;

public sealed class KeymapConflictValidatorTests
{
    private static readonly CommandId FirstCommand = new("test.first");
    private static readonly CommandId SecondCommand = new("test.second");

    [Fact]
    public void Exact_binding_in_the_same_context_is_an_error()
    {
        var sequence = KeySequence.Of(new KeyStroke("K", KeyModifiers.Control));
        var profile = CreateProfile(
            new CommandBinding(FirstCommand, sequence, CommandContext.Global),
            new CommandBinding(SecondCommand, sequence, CommandContext.Global));

        var issue = Assert.Single(KeymapConflictValidator.Validate(profile, CreateRegistry()));

        Assert.Equal(KeymapIssueKind.ExactBinding, issue.Kind);
        Assert.Equal(KeymapIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void A_complete_sequence_cannot_also_be_a_prefix_in_an_overlapping_context()
    {
        var firstStroke = new KeyStroke("K", KeyModifiers.Control);
        var profile = CreateProfile(
            new CommandBinding(FirstCommand, KeySequence.Of(firstStroke), CommandContext.Global),
            new CommandBinding(SecondCommand, KeySequence.Of(firstStroke, new KeyStroke("C")), CommandContext.Global));

        var issue = Assert.Single(KeymapConflictValidator.Validate(profile, CreateRegistry()));

        Assert.Equal(KeymapIssueKind.PrefixCollision, issue.Kind);
        Assert.Equal(KeymapIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Same_sequence_in_overlapping_equal_priority_contexts_is_an_error()
    {
        var sequence = KeySequence.Of(new KeyStroke("F"));
        var profile = CreateProfile(
            new CommandBinding(FirstCommand, sequence, CommandContext.Terminal),
            new CommandBinding(SecondCommand, sequence, CommandContext.Terminal | CommandContext.TextEditing));

        var issue = Assert.Single(KeymapConflictValidator.Validate(profile, CreateRegistry()));

        Assert.Equal(KeymapIssueKind.OverlappingContexts, issue.Kind);
        Assert.Equal(KeymapIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Higher_priority_context_shadowing_is_reported_as_a_warning()
    {
        var sequence = KeySequence.Of(new KeyStroke("F"));
        var profile = CreateProfile(
            new CommandBinding(FirstCommand, sequence, CommandContext.Global),
            new CommandBinding(SecondCommand, sequence, CommandContext.Terminal));

        var issue = Assert.Single(KeymapConflictValidator.Validate(profile, CreateRegistry()));

        Assert.Equal(KeymapIssueKind.ShadowedBinding, issue.Kind);
        Assert.Equal(KeymapIssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Mutually_exclusive_terminal_and_browser_contexts_do_not_conflict()
    {
        var sequence = KeySequence.Of(new KeyStroke("F"));
        var profile = CreateProfile(
            new CommandBinding(FirstCommand, sequence, CommandContext.Terminal),
            new CommandBinding(SecondCommand, sequence, CommandContext.Browser));

        Assert.Empty(KeymapConflictValidator.Validate(profile, CreateRegistry()));
    }

    [Fact]
    public void Unknown_command_binding_is_preserved_and_warned_about()
    {
        var unknownId = new CommandId("future.command");
        var binding = new CommandBinding(
            unknownId,
            KeySequence.Of(new KeyStroke("U", KeyModifiers.Control)),
            CommandContext.Global);
        var profile = CreateProfile(binding);

        var issue = Assert.Single(KeymapConflictValidator.Validate(profile, CreateRegistry()));

        Assert.Equal(KeymapIssueKind.UnknownCommand, issue.Kind);
        Assert.Equal(KeymapIssueSeverity.Warning, issue.Severity);
        Assert.Equal(unknownId, Assert.Single(profile.Bindings).CommandId);
    }

    [Fact]
    public void Terminal_bindings_must_be_single_stroke()
    {
        var profile = new KeymapProfile(
            new KeymapProfileId("terminal.sequence"),
            "Terminal sequence",
            KeymapLayer.Terminal,
            [
                new CommandBinding(
                    FirstCommand,
                    KeySequence.Of(
                        new KeyStroke("B", KeyModifiers.Control),
                        new KeyStroke("C")),
                    CommandContext.Terminal),
            ]);

        var issue = Assert.Single(KeymapConflictValidator.Validate(profile, CreateRegistry()));

        Assert.Equal(KeymapIssueKind.TerminalSequence, issue.Kind);
        Assert.Equal(KeymapIssueSeverity.Error, issue.Severity);
        Assert.Contains("exactly one", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_terminal_key_is_a_blocking_compatibility_issue()
    {
        var profile = new KeymapProfile(
            new KeymapProfileId("terminal.unsupported-key"),
            "Unsupported terminal key",
            KeymapLayer.Terminal,
            [
                new CommandBinding(
                    FirstCommand,
                    KeySequence.Of(new KeyStroke("MediaNextTrack")),
                    CommandContext.Terminal),
            ]);

        var issue = Assert.Single(KeymapConflictValidator.Validate(profile, CreateRegistry()));

        Assert.Equal(KeymapIssueKind.UnsupportedTerminalKey, issue.Kind);
        Assert.Equal(KeymapIssueSeverity.Error, issue.Severity);
        Assert.Contains("every desktop renderer", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static KeymapProfile CreateProfile(params CommandBinding[] bindings) => new(
        new KeymapProfileId("test"),
        "Test",
        KeymapLayer.Application,
        bindings);

    private static CommandRegistry CreateRegistry() => new(
    [
        new CommandDefinition(FirstCommand, "First", "Tests", CommandContext.All),
        new CommandDefinition(SecondCommand, "Second", "Tests", CommandContext.All),
    ]);
}
