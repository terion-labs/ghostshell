namespace Asura.Core.Tests;

public sealed class BuiltInKeymapsTests
{
    [Fact]
    public void Tmux_application_map_has_safe_prefix_options_and_required_actions()
    {
        var profile = BuiltInKeymaps.TmuxApplication;

        Assert.Equal(new KeyStroke("B", KeyModifiers.Control), profile.Prefix?.Stroke);
        Assert.Equal(FailedSequenceBehavior.DiscardAndShowHint, profile.Prefix?.FailedSequenceBehavior);
        Assert.True(profile.Prefix?.Repeatable);
        Assert.Contains(profile.Bindings, binding => binding.CommandId == BuiltInCommands.NewTab);
        Assert.Contains(profile.Bindings, binding => binding.CommandId == BuiltInCommands.SplitPanel);
        Assert.Contains(profile.Bindings, binding => binding.CommandId == BuiltInCommands.SendPrefix);
        Assert.Equal(
            KeySequence.Of(
                new KeyStroke("B", KeyModifiers.Control),
                new KeyStroke("ARROWLEFT", KeyModifiers.Shift)),
            Assert.Single(
                profile.Bindings,
                binding => binding.CommandId == BuiltInCommands.MoveTabLeft).Sequence);
        Assert.Equal(
            KeySequence.Of(
                new KeyStroke("B", KeyModifiers.Control),
                new KeyStroke("ARROWRIGHT", KeyModifiers.Shift)),
            Assert.Single(
                profile.Bindings,
                binding => binding.CommandId == BuiltInCommands.MoveTabRight).Sequence);
        Assert.Equal(10, profile.Bindings.Count(binding => binding.CommandId == BuiltInCommands.SelectTab));
        Assert.Equal(
            new KeyStroke("ARROWLEFT", KeyModifiers.Meta | KeyModifiers.Alt),
            Assert.Single(
                profile.Bindings,
                binding => binding.CommandId == BuiltInCommands.PreviousTab
                    && binding.Sequence.Count == 1).Sequence[0]);
        Assert.Equal(
            new KeyStroke("ARROWRIGHT", KeyModifiers.Meta | KeyModifiers.Alt),
            Assert.Single(
                profile.Bindings,
                binding => binding.CommandId == BuiltInCommands.NextTab
                    && binding.Sequence.Count == 1).Sequence[0]);

        var workspaceBindings = profile.Bindings
            .Where(binding => binding.CommandId == BuiltInCommands.SelectWorkspace)
            .ToArray();
        Assert.Equal(9, workspaceBindings.Length);
        for (var position = 0; position < workspaceBindings.Length; position++)
        {
            var binding = workspaceBindings[position];
            Assert.Equal(
                new KeyStroke((position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), KeyModifiers.Meta),
                binding.Sequence[0]);
            Assert.Equal(position.ToString(System.Globalization.CultureInfo.InvariantCulture), binding.Arguments["position"]);
        }
    }

    [Fact]
    public void Built_in_maps_have_no_blocking_conflicts()
    {
        foreach (var profile in BuiltInKeymaps.All)
        {
            var issues = KeymapConflictValidator.Validate(profile, BuiltInCommands.Registry);

            Assert.DoesNotContain(issues, issue => issue.Severity == KeymapIssueSeverity.Error);
        }
    }

    [Fact]
    public void Mac_terminal_clear_scrollback_does_not_shadow_the_command_palette()
    {
        var binding = Assert.Single(
            BuiltInKeymaps.MacOsTerminal.Bindings,
            item => item.CommandId == BuiltInCommands.ClearScrollback);

        Assert.Equal(
            new KeyStroke("K", KeyModifiers.Meta | KeyModifiers.Shift),
            binding.Sequence[0]);
        Assert.DoesNotContain(
            BuiltInKeymaps.MacOsTerminal.Bindings,
            item => item.Sequence.Count == 1
                && item.Sequence[0] == new KeyStroke("K", KeyModifiers.Meta));
    }

    [Theory]
    [InlineData(HostOperatingSystem.MacOS, "macOS Native")]
    [InlineData(HostOperatingSystem.Windows, "Windows Native")]
    [InlineData(HostOperatingSystem.Linux, "Linux Native")]
    public void Host_selects_its_native_terminal_preset(HostOperatingSystem host, string expectedName)
    {
        Assert.Equal(expectedName, BuiltInKeymaps.TerminalFor(host).Name);
    }

    [Fact]
    public void Cloned_preset_keeps_bindings_and_records_its_base()
    {
        var cloneId = new KeymapProfileId("my-macos-map");

        var clone = BuiltInKeymaps.MacOsTerminal.CloneAs(cloneId, "My macOS map");

        Assert.Equal(cloneId, clone.Id);
        Assert.Equal(BuiltInKeymaps.MacOsTerminalId, clone.BasedOn);
        Assert.Equal(BuiltInKeymaps.MacOsTerminal.Bindings, clone.Bindings);
    }

    [Fact]
    public void VisibleOnlyTerminalSelectionIsNamedHonestly()
    {
        Assert.True(BuiltInCommands.Registry.TryGet(BuiltInCommands.SelectAll, out var command));
        Assert.Equal("Select visible terminal content", command?.Title);
    }
}
