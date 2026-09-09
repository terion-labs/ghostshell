using System.Text.Json;
using System.Text.Json.Nodes;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class StartupCommandDeliveryFailurePolicyPersistenceTests
{
    [Theory]
    [InlineData(StartupCommandDeliveryFailurePolicy.RetryWhileLive)]
    [InlineData(StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure)]
    public async Task Both_policies_round_trip_through_the_definition_repository(
        StartupCommandDeliveryFailurePolicy policy)
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var screen = Screen(layout, policy);
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);

        var layoutSave = await layouts.SaveAsync(layout, null, CancellationToken.None);
        var screenSave = await screens.SaveAsync(screen, null, CancellationToken.None);
        await temporary.ReopenAsync();
        var restored = await new SqliteDefinitionRepository<ScreenDefinition>(
                temporary.Database,
                TimeProvider.System)
            .GetAsync(screen.Key, CancellationToken.None);

        Assert.True(layoutSave.IsSuccess, layoutSave.Error?.Message);
        Assert.True(screenSave.IsSuccess, screenSave.Error?.Message);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(
            policy,
            Assert.Single(restored.Value!.Value.Panels).Startup.DeliveryFailurePolicy);
    }

    [Fact]
    public async Task Stop_policy_on_workspace_only_tab_round_trips_through_the_definition_repository()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var workspace = Workspace(
            layout,
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var workspaces = new SqliteDefinitionRepository<WorkspaceDefinition>(
            temporary.Database,
            TimeProvider.System);

        var layoutSave = await layouts.SaveAsync(layout, null, CancellationToken.None);
        var workspaceSave = await workspaces.SaveAsync(
            workspace,
            null,
            CancellationToken.None);
        await temporary.ReopenAsync();
        var restored = await new SqliteDefinitionRepository<WorkspaceDefinition>(
                temporary.Database,
                TimeProvider.System)
            .GetAsync(workspace.Key, CancellationToken.None);

        Assert.True(layoutSave.IsSuccess, layoutSave.Error?.Message);
        Assert.True(workspaceSave.IsSuccess, workspaceSave.Error?.Message);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            Assert.Single(
                Assert.IsType<WorkspaceEntry.Tab>(
                    Assert.Single(restored.Value!.Value.Entries))
                .Panels)
                .Startup
                .DeliveryFailurePolicy);
    }

    [Fact]
    public async Task Legacy_bundle_without_policy_imports_with_retry_while_live_default()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var screen = Screen(
            layout,
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var screenDocument = DurableDefinitionFixtures.Document(screen);
        var screenPayload = JsonNode.Parse(screenDocument.PayloadJson)!.AsObject();
        var startup = screenPayload["panels"]!.AsArray()[0]!["startup"]!.AsObject();
        Assert.True(startup.Remove("deliveryFailurePolicy"));
        screenDocument = screenDocument with { PayloadJson = screenPayload.ToJsonString() };
        var bundle = new PortableDefinitionBundle(
            PortableDefinitionBundle.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            [
                DurableDefinitionFixtures.Document(layout),
                screenDocument,
            ]);
        var bundles = new SqliteDefinitionBundleStore(
            temporary.Database,
            TimeProvider.System);

        var preflight = await bundles.PreflightImportAsync(
            bundle,
            DefinitionImportMode.ReplaceExisting,
            CancellationToken.None);
        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.True(preflight.Value!.CanCommit);
        var committed = await bundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None,
            preflight.Value.AcknowledgeExecutionReview());
        var restored = await new SqliteDefinitionRepository<ScreenDefinition>(
                temporary.Database,
                TimeProvider.System)
            .GetAsync(screen.Key, CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.RetryWhileLive,
            Assert.Single(restored.Value!.Value.Panels).Startup.DeliveryFailurePolicy);
    }

    [Fact]
    public async Task Stop_policy_survives_portable_bundle_export_and_import()
    {
        await using var source = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var screen = Screen(
            layout,
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            source.Database,
            TimeProvider.System);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            source.Database,
            TimeProvider.System);
        Assert.True(
            (await layouts.SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
        Assert.True(
            (await screens.SaveAsync(screen, null, CancellationToken.None)).IsSuccess);
        var sourceBundles = new SqliteDefinitionBundleStore(
            source.Database,
            TimeProvider.System);
        var exported = await sourceBundles.ExportAsync(CancellationToken.None);
        Assert.True(exported.IsSuccess, exported.Error?.Message);

        await using var destination = TemporaryDatabase.Create();
        var destinationBundles = new SqliteDefinitionBundleStore(
            destination.Database,
            TimeProvider.System);
        var preflight = await destinationBundles.PreflightImportAsync(
            exported.Value!,
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);
        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.True(preflight.Value!.CanCommit);
        var committed = await destinationBundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None,
            preflight.Value.AcknowledgeExecutionReview());
        var restored = await new SqliteDefinitionRepository<ScreenDefinition>(
                destination.Database,
                TimeProvider.System)
            .GetAsync(screen.Key, CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            Assert.Single(restored.Value!.Value.Panels).Startup.DeliveryFailurePolicy);
    }

    [Fact]
    public async Task Workspace_only_tab_stop_policy_survives_portable_bundle_export_and_import()
    {
        await using var source = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var workspace = Workspace(
            layout,
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            source.Database,
            TimeProvider.System);
        var workspaces = new SqliteDefinitionRepository<WorkspaceDefinition>(
            source.Database,
            TimeProvider.System);
        Assert.True(
            (await layouts.SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
        Assert.True(
            (await workspaces.SaveAsync(workspace, null, CancellationToken.None)).IsSuccess);
        var sourceBundles = new SqliteDefinitionBundleStore(
            source.Database,
            TimeProvider.System);
        var exported = await sourceBundles.ExportAsync(CancellationToken.None);
        Assert.True(exported.IsSuccess, exported.Error?.Message);

        await using var destination = TemporaryDatabase.Create();
        var destinationBundles = new SqliteDefinitionBundleStore(
            destination.Database,
            TimeProvider.System);
        var preflight = await destinationBundles.PreflightImportAsync(
            exported.Value!,
            DefinitionImportMode.FailOnConflict,
            CancellationToken.None);
        Assert.True(preflight.IsSuccess, preflight.Error?.Message);
        Assert.True(preflight.Value!.CanCommit);
        var committed = await destinationBundles.CommitImportAsync(
            preflight.Value,
            CancellationToken.None,
            preflight.Value.AcknowledgeExecutionReview());
        var restored = await new SqliteDefinitionRepository<WorkspaceDefinition>(
                destination.Database,
                TimeProvider.System)
            .GetAsync(workspace.Key, CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            Assert.Single(
                Assert.IsType<WorkspaceEntry.Tab>(
                    Assert.Single(restored.Value!.Value.Entries))
                .Panels)
                .Startup
                .DeliveryFailurePolicy);
    }

    [Fact]
    public void Strict_definition_json_rejects_unknown_policy_string()
    {
        AssertStrictPayloadRejected(JsonValue.Create("FuturePolicy"));
    }

    [Fact]
    public void Strict_definition_json_rejects_policy_integer()
    {
        AssertStrictPayloadRejected(JsonValue.Create(999));
    }

    private static ScreenDefinition Screen(
        LayoutDefinition layout,
        StartupCommandDeliveryFailurePolicy policy) =>
        new(
            new ScreenId($"screen-{policy}"),
            ScreenDefinition.CurrentSchemaVersion,
            $"Screen {policy}",
            description: null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    Title: null,
                    ConnectionId: null,
                    new PanelStartupBehavior(
                        "/work",
                        ["git status"],
                        policy)),
            ]);

    private static WorkspaceDefinition Workspace(
        LayoutDefinition layout,
        StartupCommandDeliveryFailurePolicy policy) =>
        new(
            new WorkspaceId($"workspace-{policy}"),
            WorkspaceDefinition.CurrentSchemaVersion,
            $"Workspace {policy}",
            description: null,
            accent: null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("workspace-tab"),
                    "Workspace tab",
                    layout.Id,
                    [
                        new ScreenPanelDefinition(
                            new ScreenPanelId("workspace-terminal"),
                            new LayoutSlotId("main"),
                            ScreenPanelKind.Terminal,
                            Title: null,
                            ConnectionId: null,
                            new PanelStartupBehavior(
                                "/work",
                                ["git status"],
                                policy)),
                    ]),
            ]);

    private static void AssertStrictPayloadRejected(JsonNode? invalidPolicy)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(
            PanelStartupBehavior.None,
            DefinitionJson.Options))!.AsObject();
        payload["deliveryFailurePolicy"] = invalidPolicy;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PanelStartupBehavior>(
                payload.ToJsonString(),
                DefinitionJson.Options));
    }
}
