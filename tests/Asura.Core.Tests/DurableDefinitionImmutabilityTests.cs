namespace Asura.Core.Tests;

public sealed class DurableDefinitionImmutabilityTests
{
    [Fact]
    public void Connection_collections_cannot_be_changed_through_their_read_only_interfaces()
    {
        var startup = new ConnectionStartup(
            "/srv/app",
            [new("REGION", new ConnectionEnvironmentValue.PlainText("west"))]);
        var connection = new ConnectionProfile(
            new ConnectionId("production"),
            ConnectionProfile.CurrentSchemaVersion,
            "Production",
            new ConnectionEndpoint.Ssh("prod.example"),
            new ConnectionAuthentication.SshAgent(),
            startup,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict,
            ["production"]);

        AssertCannotReplace(connection.Tags, "changed");
        AssertCannotReplace(
            connection.Startup.Environment,
            new ConnectionEnvironmentVariable("REGION", new ConnectionEnvironmentValue.PlainText("east")));
    }

    [Fact]
    public void Layout_screen_and_workspace_collections_cannot_be_changed_after_construction()
    {
        var slot = new LayoutSlotDefinition(
            new LayoutSlotId("main"),
            new LayoutGridBounds(0, 0, 1, 1),
            new LayoutMinimumSize(120, 80));
        var layout = new LayoutDefinition(
            new LayoutId("single"),
            LayoutDefinition.CurrentSchemaVersion,
            "Single",
            new LayoutGrid(1, 1),
            [slot]);
        var panel = new ScreenPanelDefinition(
            new ScreenPanelId("terminal"),
            slot.Id,
            ScreenPanelKind.Terminal,
            "Terminal",
            null,
            new PanelStartupBehavior("/work", ["git status"]));
        var screen = new ScreenDefinition(
            new ScreenId("shell"),
            ScreenDefinition.CurrentSchemaVersion,
            "Shell",
            null,
            layout.Id,
            [panel],
            ["daily"]);
        var tab = new WorkspaceEntry.Tab(
            new WorkspaceEntryId("scratch"),
            "Scratch",
            layout.Id,
            [panel]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("project"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Project",
            null,
            null,
            [tab]);

        AssertCannotReplace(layout.Slots, slot with { Id = new LayoutSlotId("replacement") });
        AssertCannotReplace(screen.Panels, panel with { Id = new ScreenPanelId("replacement") });
        AssertCannotReplace(screen.Tags, "changed");
        AssertCannotReplace(panel.Startup.Commands, "changed");
        AssertCannotReplace(workspace.Entries, new WorkspaceEntry.ScreenReference(
            new WorkspaceEntryId("replacement"),
            screen.Id));
        AssertCannotReplace(tab.Panels, panel with { Id = new ScreenPanelId("replacement") });
    }

    [Fact]
    public void Terminal_and_keymap_collections_cannot_be_changed_after_construction()
    {
        var sequence = KeySequence.Of(new KeyStroke("K", KeyModifiers.Control));
        var binding = new CommandBinding(
            new CommandId("terminal.clear"),
            sequence,
            CommandContext.Terminal,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["scope"] = "screen" });
        var keymap = new KeymapProfile(
            new KeymapProfileId("custom"),
            "Custom",
            KeymapLayer.Terminal,
            [binding]);

        AssertCannotReplace(TerminalPalette.AsuraDark.AnsiColors, RgbColor.Parse("#123456"));
        AssertCannotReplace(
            keymap.Bindings,
            new CommandBinding(binding.CommandId, binding.Sequence, CommandContext.Global, binding.Arguments));
        AssertCannotReplace(sequence.Strokes, new KeyStroke("L", KeyModifiers.Control));
        AssertCannotReplace(
            new DefinitionValidationResult(
                [new(DefinitionValidationCode.Required, "Required")]).Issues,
            new DefinitionValidationIssue(DefinitionValidationCode.InvalidEntry, "Changed"));

        var arguments = Assert.IsAssignableFrom<IDictionary<string, string>>(binding.Arguments);
        Assert.True(arguments.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => arguments["scope"] = "workspace");
        Assert.Equal("screen", binding.Arguments["scope"]);
    }

    private static void AssertCannotReplace<T>(IReadOnlyList<T> values, T replacement)
    {
        var original = values[0];
        var mutableView = Assert.IsAssignableFrom<IList<T>>(values);

        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = replacement);
        Assert.Equal(original, values[0]);
    }
}
