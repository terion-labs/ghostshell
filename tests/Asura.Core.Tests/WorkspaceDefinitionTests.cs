namespace Asura.Core.Tests;

public sealed class WorkspaceDefinitionTests
{
    [Fact]
    public void Moving_an_entry_changes_order_without_changing_identity()
    {
        var isolationMount = new WorkspaceIsolationMountDefinition(
            Path.Combine(Path.GetTempPath(), "asura-move"),
            "/workspace",
            IsReadOnly: true);
        var workspace = CreateIsolatedWorkspace(
            [isolationMount],
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("connection-entry"),
                new ConnectionId("production")),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("screen-entry"),
                new ScreenId("deploy")),
            CreateTabEntry("notes-entry"));

        var reordered = workspace.MoveEntry(new WorkspaceEntryId("screen-entry"), 0);

        Assert.Equal(
            ["screen-entry", "connection-entry", "notes-entry"],
            reordered.Entries.Select(entry => entry.Id.Value), StringComparer.Ordinal);
        Assert.Equal(workspace.Id, reordered.Id);
        Assert.Equal(workspace.Icon, reordered.Icon);
        Assert.True(reordered.IsIsolated);
        Assert.Equal([isolationMount], reordered.IsolationMounts);
    }

    [Fact]
    public void Validator_rejects_a_relative_isolation_mount_host_path()
    {
        var workspace = CreateIsolatedWorkspace(
            [new("relative/project", "/workspace", IsReadOnly: true)]);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidEntry
                && issue.Message.Contains("absolute host path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/Users/alice/project")]
    [InlineData("C:\\Users\\alice\\project")]
    [InlineData("C:/Users/alice/project")]
    [InlineData("\\\\server\\share\\project")]
    public void Validator_accepts_portable_absolute_isolation_mount_host_paths(string hostPath)
    {
        var workspace = CreateIsolatedWorkspace(
            [new(hostPath, "/workspace", IsReadOnly: true)]);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.DoesNotContain(
            result.Issues,
            issue => issue.Message.Contains("absolute host path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("C:relative")]
    [InlineData("\\root-relative")]
    [InlineData("\\\\server")]
    [InlineData("\\\\server\\")]
    public void Validator_rejects_non_absolute_cross_platform_host_paths(string hostPath)
    {
        var workspace = CreateIsolatedWorkspace(
            [new(hostPath, "/workspace", IsReadOnly: true)]);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Message.Contains("absolute host path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("/")]
    [InlineData("/workspace/../secrets")]
    [InlineData("/workspace/./nested")]
    [InlineData("/workspace\0secrets")]
    [InlineData("/proc")]
    [InlineData("//proc")]
    [InlineData("/bin")]
    [InlineData("/bin/sh")]
    [InlineData("/sbin")]
    [InlineData("/usr")]
    [InlineData("/usr/bin")]
    [InlineData("/lib")]
    [InlineData("/etc")]
    [InlineData("/var")]
    [InlineData("/run/guest")]
    [InlineData("/root")]
    [InlineData("/root/project")]
    public void Validator_rejects_invalid_isolation_mount_guest_paths(string guestPath)
    {
        var workspace = CreateIsolatedWorkspace(
            [new(AbsoluteHostPath("guest-path"), guestPath, IsReadOnly: true)]);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidEntry
                && issue.Message.Contains("absolute guest path", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_duplicate_isolation_mount_guest_paths()
    {
        var workspace = CreateIsolatedWorkspace(
        [
            new(AbsoluteHostPath("first"), "/workspace", IsReadOnly: true),
            new(AbsoluteHostPath("second"), "/workspace", IsReadOnly: false),
        ]);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.DuplicateId
                && issue.Message.Contains("/workspace", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_guest_mount_duplicates_after_path_normalization()
    {
        var workspace = CreateIsolatedWorkspace(
        [
            new(AbsoluteHostPath("first"), "/workspace", IsReadOnly: true),
            new(AbsoluteHostPath("second"), "//workspace/", IsReadOnly: false),
        ]);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.DuplicateId
                && issue.Message.Contains("/workspace", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_more_than_the_supported_isolation_mount_count()
    {
        var mounts = Enumerable
            .Range(0, WorkspaceDefinition.MaximumIsolationMountCount + 1)
            .Select(index => new WorkspaceIsolationMountDefinition(
                AbsoluteHostPath($"mount-{index}"),
                $"/mount-{index}",
                IsReadOnly: true))
            .ToArray();
        var workspace = CreateIsolatedWorkspace(mounts);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidEntry
                && issue.Message.Contains(
                    WorkspaceDefinition.MaximumIsolationMountCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_duplicate_entry_ids()
    {
        var workspace = CreateWorkspace(
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("duplicate"),
                new ConnectionId("production")),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("duplicate"),
                new ScreenId("deploy")));

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(result.Issues, issue => issue.Code == DefinitionValidationCode.DuplicateId);
    }

    [Fact]
    public void Workspace_models_connection_screen_and_workspace_only_tab_entries()
    {
        var workspace = CreateWorkspace(
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("connection"),
                new ConnectionId("production")),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("screen"),
                new ScreenId("deploy")),
            CreateTabEntry("tab"));

        var result = WorkspaceValidator.Validate(workspace);

        Assert.True(result.IsValid);
        Assert.IsType<WorkspaceEntry.ConnectionReference>(workspace.Entries[0]);
        Assert.IsType<WorkspaceEntry.ScreenReference>(workspace.Entries[1]);
        Assert.IsType<WorkspaceEntry.Tab>(workspace.Entries[2]);
    }

    [Fact]
    public void Validator_rejects_non_semantic_icon_identifiers()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            "#FF8400",
            [],
            icon: "Not an icon!");

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidEntry
                && issue.Message.Contains("icon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_run_local_yolo_as_a_saved_policy_override()
    {
        var yoloPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("unsafe-policy"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Unsafe policy",
            null,
            null,
            [],
            agentPolicyOverride: yoloPolicy);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidAgentPolicy
                && issue.Message.Contains("YOLO", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_non_default_delivery_failure_policy_on_non_terminal_tab_panel()
    {
        var browser = new ScreenPanelDefinition(
            new ScreenPanelId("browser"),
            new LayoutSlotId("main"),
            ScreenPanelKind.Browser,
            "Browser",
            null,
            new PanelStartupBehavior(
                "https://example.test",
                deliveryFailurePolicy:
                    StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure));
        var workspace = CreateWorkspace(
            new WorkspaceEntry.Tab(
                new WorkspaceEntryId("browser-tab"),
                "Browser",
                new LayoutId("single"),
                [browser]));

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidPanel
                && string.Equals(issue.Target, browser.Id.Value
, StringComparison.Ordinal) && issue.Message.Contains(
                    "delivery failure policy",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static WorkspaceDefinition CreateWorkspace(params WorkspaceEntry[] entries) =>
        CreateWorkspace(isIsolated: false, entries);

    private static WorkspaceDefinition CreateWorkspace(
        bool isIsolated,
        params WorkspaceEntry[] entries) =>
        new(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            "#FF8400",
            entries,
            isIsolated: isIsolated);

    private static WorkspaceDefinition CreateIsolatedWorkspace(
        IReadOnlyList<WorkspaceIsolationMountDefinition> isolationMounts,
        params WorkspaceEntry[] entries) =>
        new(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            "#FF8400",
            entries,
            isIsolated: true,
            isolationMounts: isolationMounts);

    private static string AbsoluteHostPath(string leaf) =>
        Path.Combine(Path.GetTempPath(), "asura", leaf);

    private static WorkspaceEntry.Tab CreateTabEntry(string id) =>
        new(
            new WorkspaceEntryId(id),
            "Scratch",
            new LayoutId("single"),
            [
                new(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    "Terminal",
                    null,
                    PanelStartupBehavior.None),
            ]);

    [Fact]
    public void A_workspace_saved_before_colours_existed_still_deserializes()
    {
        // Definitions persist as JSON, so an older payload simply lacks the
        // property. Built by serializing a current definition and removing
        // the colour, so the fixture cannot drift from the real shape.
        var current = new WorkspaceDefinition(
            new WorkspaceId("workspace-old"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Older",
            null,
            accent: null,
            [],
            icon: "workspace");
        var older = System.Text.Json.JsonSerializer.Serialize(current)
            .Replace(",\"Color\":null", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Color", older, StringComparison.Ordinal);

        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceDefinition>(older);

        Assert.NotNull(restored);
        Assert.Null(restored!.Color);
        Assert.Null(restored.Accent);
        Assert.Equal("workspace", restored.Icon);
    }

    [Fact]
    public void Colour_and_accent_are_independent()
    {
        // The tile colour identifies the workspace; the accent retints the
        // shell. One must never imply the other, or unsetting the accent
        // would silently repaint the rail.
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("workspace-appearance"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Production",
            null,
            accent: null,
            [],
            icon: "rocket",
            color: "  #B4543A  ");

        Assert.Equal("#B4543A", workspace.Color);
        Assert.Null(workspace.Accent);

        var round = System.Text.Json.JsonSerializer.Deserialize<WorkspaceDefinition>(
            System.Text.Json.JsonSerializer.Serialize(workspace));
        Assert.Equal("#B4543A", round!.Color);
        Assert.Null(round.Accent);
    }
}
