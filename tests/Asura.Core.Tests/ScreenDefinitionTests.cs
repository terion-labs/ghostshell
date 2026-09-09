namespace Asura.Core.Tests;

public sealed class ScreenDefinitionTests
{
    [Fact]
    public void Screen_maps_every_layout_slot_exactly_once()
    {
        var layout = CreateLayout();
        var screen = new ScreenDefinition(
            new ScreenId("deploy"),
            ScreenDefinition.CurrentSchemaVersion,
            "Deploy",
            null,
            layout.Id,
            [
                Panel("terminal", "left", ScreenPanelKind.Terminal),
                Panel("logs", "right", ScreenPanelKind.FileViewer),
            ]);

        var result = ScreenValidator.Validate(screen, layout);

        Assert.True(result.IsValid);
        Assert.Equal(["left", "right"], screen.Panels.Select(panel => panel.SlotId.Value), StringComparer.Ordinal);
    }

    [Fact]
    public void Screen_rejects_duplicate_unknown_and_unmapped_slots()
    {
        var layout = CreateLayout();
        var screen = new ScreenDefinition(
            new ScreenId("broken"),
            ScreenDefinition.CurrentSchemaVersion,
            "Broken",
            null,
            layout.Id,
            [
                Panel("one", "left", ScreenPanelKind.Terminal),
                Panel("two", "left", ScreenPanelKind.Terminal),
                Panel("three", "removed-slot", ScreenPanelKind.Browser),
            ]);

        var result = ScreenValidator.Validate(screen, layout);

        Assert.Contains(result.Issues, issue => issue.Code == DefinitionValidationCode.DuplicateId);
        Assert.Contains(result.Issues, issue => issue.Code == DefinitionValidationCode.UnknownSlot);
        Assert.Contains(result.Issues, issue =>
            issue.Code == DefinitionValidationCode.MissingSlot
            && string.Equals(issue.Target, "right", StringComparison.Ordinal));
    }

    [Fact]
    public void Screen_rejects_run_local_yolo_as_a_saved_policy_override()
    {
        var layout = CreateLayout();
        var yoloPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var screen = new ScreenDefinition(
            new ScreenId("unsafe-policy"),
            ScreenDefinition.CurrentSchemaVersion,
            "Unsafe policy",
            null,
            layout.Id,
            [
                Panel("terminal", "left", ScreenPanelKind.Terminal),
                Panel("logs", "right", ScreenPanelKind.FileViewer),
            ],
            agentPolicyOverride: yoloPolicy);

        var result = ScreenValidator.Validate(screen, layout);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidAgentPolicy
                && issue.Message.Contains("YOLO", StringComparison.Ordinal));
    }

    [Fact]
    public void Screen_rejects_non_default_delivery_failure_policy_on_non_terminal_panel()
    {
        var layout = CreateLayout();
        var browser = Panel("browser", "right", ScreenPanelKind.Browser) with
        {
            Startup = new PanelStartupBehavior(
                "https://example.test",
                deliveryFailurePolicy:
                    StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure),
        };
        var screen = new ScreenDefinition(
            new ScreenId("invalid-browser-policy"),
            ScreenDefinition.CurrentSchemaVersion,
            "Invalid browser policy",
            null,
            layout.Id,
            [
                Panel("terminal", "left", ScreenPanelKind.Terminal),
                browser,
            ]);

        var result = ScreenValidator.Validate(screen, layout);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidPanel
                && string.Equals(issue.Target, browser.Id.Value
, StringComparison.Ordinal) && issue.Message.Contains(
                    "delivery failure policy",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static LayoutDefinition CreateLayout() =>
        new(
            new LayoutId("two-columns"),
            LayoutDefinition.CurrentSchemaVersion,
            "Two columns",
            new LayoutGrid(2, 1),
            [
                new(new LayoutSlotId("left"), new(0, 0, 1, 1), new(100, 80)),
                new(new LayoutSlotId("right"), new(1, 0, 1, 1), new(100, 80)),
            ]);

    private static ScreenPanelDefinition Panel(string id, string slot, ScreenPanelKind kind) =>
        new(
            new ScreenPanelId(id),
            new LayoutSlotId(slot),
            kind,
            null,
            null,
            PanelStartupBehavior.None);
}
