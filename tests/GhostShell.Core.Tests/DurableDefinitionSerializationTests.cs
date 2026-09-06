using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhostShell.Core.Tests;

public sealed class DurableDefinitionSerializationTests
{
    [Fact]
    public void Layout_screen_and_workspace_definitions_round_trip_with_polymorphic_entries()
    {
        var layout = new LayoutDefinition(
            new LayoutId("single"),
            LayoutDefinition.CurrentSchemaVersion,
            "Single",
            new LayoutGrid(1, 1),
            [new(new LayoutSlotId("main"), new(0, 0, 1, 1), new(120, 80))]);
        var screen = new ScreenDefinition(
            new ScreenId("shell"),
            ScreenDefinition.CurrentSchemaVersion,
            "Shell",
            null,
            layout.Id,
            [
                new(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    null,
                    new ConnectionId("local"),
                    new PanelStartupBehavior("/work", ["git status"])),
            ]);
        var isolationMount = new WorkspaceIsolationMountDefinition(
            Path.Combine(Path.GetTempPath(), "ghostshell-project"),
            "/workspace",
            IsReadOnly: true);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("project"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Project",
            null,
            null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("connection"),
                    new ConnectionId("local")),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("screen"),
                    screen.Id),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("scratch"),
                    "Scratch",
                    layout.Id,
                    screen.Panels),
            ],
            icon: "server",
            isIsolated: true,
            isolationMounts: [isolationMount],
            isolationImageReference: "registry.example.test/team/dev:2026.09",
            networkOverride: new NetworkPolicy(
                [new NetworkConnectionId("office-proxy")],
                new NetworkConnectionId("office-proxy"),
                isEnabled: true,
                killSwitchEnabled: true));

        var restoredLayout = RoundTrip(layout);
        var restoredScreen = RoundTrip(screen);
        var restoredWorkspace = RoundTrip(workspace);

        Assert.True(LayoutValidator.Validate(restoredLayout).IsValid);
        Assert.True(ScreenValidator.Validate(restoredScreen, restoredLayout).IsValid);
        Assert.True(WorkspaceValidator.Validate(restoredWorkspace).IsValid);
        Assert.IsType<WorkspaceEntry.ConnectionReference>(restoredWorkspace.Entries[0]);
        Assert.IsType<WorkspaceEntry.ScreenReference>(restoredWorkspace.Entries[1]);
        Assert.IsType<WorkspaceEntry.Tab>(restoredWorkspace.Entries[2]);
        Assert.Equal("server", restoredWorkspace.Icon);
        Assert.True(restoredWorkspace.IsIsolated);
        Assert.Equal([isolationMount], restoredWorkspace.IsolationMounts);
        Assert.Equal(
            "registry.example.test/team/dev:2026.09",
            restoredWorkspace.IsolationImageReference);
        Assert.NotNull(restoredWorkspace.NetworkOverride);
        Assert.Equal(
            workspace.NetworkOverride!.Connections,
            restoredWorkspace.NetworkOverride.Connections);
        Assert.Equal(
            workspace.NetworkOverride.SelectedConnectionId,
            restoredWorkspace.NetworkOverride.SelectedConnectionId);
        Assert.Equal(workspace.NetworkOverride.IsEnabled, restoredWorkspace.NetworkOverride.IsEnabled);
        Assert.Equal(
            workspace.NetworkOverride.KillSwitchEnabled,
            restoredWorkspace.NetworkOverride.KillSwitchEnabled);
    }

    [Fact]
    public void Workspace_payload_without_icon_uses_the_backward_compatible_default()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("legacy"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Legacy",
            null,
            null,
            []);
        var payload = JsonNode.Parse(JsonSerializer.Serialize(workspace))!.AsObject();
        Assert.True(payload.Remove(nameof(WorkspaceDefinition.Icon)));

        var restored = JsonSerializer.Deserialize<WorkspaceDefinition>(payload.ToJsonString());

        Assert.NotNull(restored);
        Assert.Equal(WorkspaceDefinition.DefaultIcon, restored.Icon);
        Assert.True(WorkspaceValidator.Validate(restored).IsValid);
    }

    [Fact]
    public void Workspace_payload_without_isolation_uses_the_backward_compatible_default()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("legacy-isolation"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Legacy isolation",
            null,
            null,
            []);
        var payload = JsonNode.Parse(JsonSerializer.Serialize(workspace))!.AsObject();
        Assert.True(payload.Remove(nameof(WorkspaceDefinition.IsIsolated)));

        var restored = JsonSerializer.Deserialize<WorkspaceDefinition>(payload.ToJsonString());

        Assert.NotNull(restored);
        Assert.False(restored.IsIsolated);
    }

    [Fact]
    public void Workspace_payload_without_isolation_mounts_uses_an_empty_collection()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("legacy-isolation-mounts"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Legacy isolation mounts",
            null,
            null,
            [],
            isIsolated: true,
            isolationMounts:
            [
                new(
                    Path.Combine(Path.GetTempPath(), "ghostshell-legacy"),
                    "/workspace",
                    IsReadOnly: false),
            ]);
        var payload = JsonNode.Parse(JsonSerializer.Serialize(workspace))!.AsObject();
        Assert.True(payload.Remove(nameof(WorkspaceDefinition.IsolationMounts)));

        var restored = JsonSerializer.Deserialize<WorkspaceDefinition>(payload.ToJsonString());

        Assert.NotNull(restored);
        Assert.True(restored.IsIsolated);
        Assert.Empty(restored.IsolationMounts);
    }

    [Fact]
    public void Workspace_payload_without_isolation_image_uses_the_platform_default()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("legacy-isolation-image"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Legacy isolation image",
            null,
            null,
            [],
            isIsolated: true,
            isolationImageReference: "registry.example.test/dev:old");
        var payload = JsonNode.Parse(JsonSerializer.Serialize(workspace))!.AsObject();
        Assert.True(payload.Remove(nameof(WorkspaceDefinition.IsolationImageReference)));

        var restored = JsonSerializer.Deserialize<WorkspaceDefinition>(payload.ToJsonString());

        Assert.NotNull(restored);
        Assert.Null(restored.IsolationImageReference);
    }

    [Fact]
    public void Workspace_payload_without_agent_isolation_keeps_the_agent_on_the_host()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("legacy-agent-isolation"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Legacy agent isolation",
            null,
            null,
            [],
            isIsolated: true,
            runAgentInIsolation: true);
        var payload = JsonNode.Parse(JsonSerializer.Serialize(workspace))!.AsObject();
        Assert.True(payload.Remove(nameof(WorkspaceDefinition.RunAgentInIsolation)));

        var restored = JsonSerializer.Deserialize<WorkspaceDefinition>(payload.ToJsonString());

        Assert.NotNull(restored);
        Assert.False(restored.RunAgentInIsolation);
    }

    [Fact]
    public void Workspace_payload_without_network_override_inherits_application_networking()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("legacy-networking"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Legacy networking",
            null,
            null,
            [],
            networkOverride: NetworkPolicy.Direct);
        var payload = JsonNode.Parse(JsonSerializer.Serialize(workspace))!.AsObject();
        Assert.True(payload.Remove(nameof(WorkspaceDefinition.NetworkOverride)));

        var restored = JsonSerializer.Deserialize<WorkspaceDefinition>(payload.ToJsonString());

        Assert.NotNull(restored);
        Assert.Null(restored.NetworkOverride);
    }

    [Theory]
    [InlineData(StartupCommandDeliveryFailurePolicy.RetryWhileLive)]
    [InlineData(StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure)]
    public void Panel_startup_delivery_failure_policy_round_trips(
        StartupCommandDeliveryFailurePolicy policy)
    {
        var startup = new PanelStartupBehavior(
            "/work",
            ["git status"],
            policy);

        var restored = JsonSerializer.Deserialize<PanelStartupBehavior>(
            JsonSerializer.Serialize(startup));

        Assert.NotNull(restored);
        Assert.Equal(policy, restored.DeliveryFailurePolicy);
    }

    [Fact]
    public void Legacy_panel_startup_payload_without_delivery_failure_policy_uses_retry_default()
    {
        var startup = new PanelStartupBehavior("/work", ["git status"]);
        var payload = JsonNode.Parse(JsonSerializer.Serialize(startup))!.AsObject();
        Assert.True(payload.Remove(nameof(PanelStartupBehavior.DeliveryFailurePolicy)));

        var restored = JsonSerializer.Deserialize<PanelStartupBehavior>(payload.ToJsonString());

        Assert.NotNull(restored);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.RetryWhileLive,
            restored.DeliveryFailurePolicy);
    }

    private static T RoundTrip<T>(T value)
        where T : IDurableDefinition
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
    }
}
