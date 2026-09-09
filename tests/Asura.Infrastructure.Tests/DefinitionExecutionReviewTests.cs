using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class DefinitionExecutionReviewTests
{
    [Fact]
    public async Task Export_preserves_saved_profile_addresses_without_reading_credentials()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        Assert.True((await new SqliteDefinitionRepository<LayoutDefinition>(temporary.Database, TimeProvider.System)
            .SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
        var profile = new DatabaseConnectionProfile(new("profile"), 1, "Database", "postgres", "Host=fixture.invalid");
        Assert.True((await new SqliteDefinitionRepository<DatabaseConnectionProfile>(temporary.Database, TimeProvider.System)
            .SaveAsync(profile, null, CancellationToken.None)).IsSuccess);
        var screen = new ScreenDefinition(new("saved"), 1, "Saved", null, layout.Id,
            [new(new("db"), new("main"), ScreenPanelKind.DatabaseViewer, "Database", null, new("saved:profile"))]);
        Assert.True((await new SqliteDefinitionRepository<ScreenDefinition>(temporary.Database, TimeProvider.System)
            .SaveAsync(screen, null, CancellationToken.None)).IsSuccess);
        var result = await new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System).ExportAsync(CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.ReconnectRequiredDatabasePanelCount);
        Assert.Contains("saved:profile", Assert.Single(result.Value.Definitions,
            document => document.Kind == ScreenDefinition.Kind).PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task Imported_existing_screen_is_frozen_without_reusing_its_confidential_target(bool editAfterReview, bool hasScreenPolicy, bool collideWithCopy)
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        Assert.True((await new SqliteDefinitionRepository<LayoutDefinition>(temporary.Database, TimeProvider.System)
            .SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
        var token = DatabaseRecoveryToken.Create().Serialize();
        if (hasScreenPolicy)
        {
            Assert.True((await new SqliteDefinitionRepository<AiProviderProfile>(temporary.Database, TimeProvider.System)
                .SaveAsync(DurableDefinitionFixtures.AiProvider("Anthropic", "Test provider", 0), null, CancellationToken.None)).IsSuccess);
        }
        var panel = new ScreenPanelDefinition(new("private-db"), new("main"), ScreenPanelKind.DatabaseViewer,
            "Database", null, new PanelStartupBehavior(token));
        var screen = new ScreenDefinition(new("local-screen"), 1, "Local screen", null, layout.Id, [panel],
            agentPolicyOverride: hasScreenPolicy ? AgentPolicy.Default : null);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(temporary.Database, TimeProvider.System);
        var savedScreen = await screens.SaveAsync(screen, null, CancellationToken.None);
        Assert.True(savedScreen.IsSuccess, savedScreen.Error?.Message);
        var reference = new WorkspaceEntry.ScreenReference(new("entry"), screen.Id, "My local screen");
        var workspace = new WorkspaceDefinition(new("imported"), 1, "Imported", null, null, [reference]);
        var store = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var reviewed = (await store.PreflightImportAsync(new(1, DateTimeOffset.UtcNow,
            [DurableDefinitionFixtures.Document(workspace)]), DefinitionImportMode.ReplaceExisting, CancellationToken.None)).Value!;
        Assert.True(reviewed.CanCommit);
        Assert.Contains(reviewed.ExecutionReview, item => item.Details.Contains("detached from future updates", StringComparison.Ordinal));
        Assert.All(reviewed.Bundle.Definitions, document => Assert.DoesNotContain(token, document.PayloadJson, StringComparison.Ordinal));
        if (editAfterReview)
        {
            var changed = new ScreenDefinition(screen.Id, 1, "Changed screen", null, layout.Id, [panel]);
            Assert.True((await screens.SaveAsync(changed, 1, CancellationToken.None)).IsSuccess);
        }
        if (collideWithCopy)
        {
            var document = Assert.Single(reviewed.Bundle.Definitions, document => document.Kind == ScreenDefinition.Kind);
            var collision = new ScreenDefinition(new(document.Id), 1, "Concurrent unrelated screen", null, layout.Id, [panel]);
            Assert.True((await screens.SaveAsync(collision, null, CancellationToken.None)).IsSuccess);
        }
        var committed = await store.CommitImportAsync(reviewed, CancellationToken.None, reviewed.AcknowledgeExecutionReview());
        if (editAfterReview || collideWithCopy)
        {
            Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, committed.Error?.Code);
            Assert.DoesNotContain((await store.ExportAsync(CancellationToken.None)).Value!.Definitions,
                document => document.Kind == WorkspaceDefinition.Kind && string.Equals(document.Id, workspace.Id.Value, StringComparison.Ordinal));
            return;
        }
        Assert.True(committed.IsSuccess, committed.Error?.Message);
        var restored = (await new SqliteDefinitionRepository<WorkspaceDefinition>(temporary.Database, TimeProvider.System)
            .GetAsync(workspace.Key, CancellationToken.None)).Value!.Value;
        if (hasScreenPolicy)
        {
            var copiedReference = Assert.IsType<WorkspaceEntry.ScreenReference>(Assert.Single(restored.Entries));
            Assert.Equal(reference.Id, copiedReference.Id);
            Assert.Equal(reference.Alias, copiedReference.Alias);
            Assert.NotEqual(reference.ScreenId, copiedReference.ScreenId);
            var copy = (await screens.GetAsync(new(ScreenDefinition.Kind, copiedReference.ScreenId.Value), CancellationToken.None)).Value!.Value;
            Assert.Equivalent(screen.AgentPolicyOverride, copy.AgentPolicyOverride);
            Assert.Equal(layout.Id, copy.LayoutId);
            Assert.Equal(panel.Id, Assert.Single(copy.Panels).Id);
            Assert.Equal(DatabaseRecoveryToken.ReconnectTarget, copy.Panels[0].Startup.Location);
        }
        else
        {
            var tab = Assert.IsType<WorkspaceEntry.Tab>(Assert.Single(restored.Entries));
            Assert.Equal(reference.Id, tab.Id);
            Assert.Equal(reference.Alias, tab.Name);
            Assert.Equal(layout.Id, tab.LayoutId);
            Assert.Equal(panel.Id, Assert.Single(tab.Panels).Id);
            Assert.Equal(DatabaseRecoveryToken.ReconnectTarget, tab.Panels[0].Startup.Location);
        }
        Assert.Equal(token, (await screens.GetAsync(screen.Key, CancellationToken.None)).Value!.Value.Panels[0].Startup.Location);
    }

    [Fact]
    public async Task Imported_reference_without_confidential_target_stays_linked()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        Assert.True((await new SqliteDefinitionRepository<LayoutDefinition>(temporary.Database, TimeProvider.System)
            .SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
        var screen = DurableDefinitionFixtures.Screen(layoutId: layout.Id.Value);
        Assert.True((await new SqliteDefinitionRepository<ScreenDefinition>(temporary.Database, TimeProvider.System)
            .SaveAsync(screen, null, CancellationToken.None)).IsSuccess);
        var reference = new WorkspaceEntry.ScreenReference(new("entry"), screen.Id);
        var workspace = new WorkspaceDefinition(new("imported"), 1, "Imported", null, null, [reference]);
        var store = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var reviewed = (await store.PreflightImportAsync(new(1, DateTimeOffset.UtcNow,
            [DurableDefinitionFixtures.Document(workspace)]), DefinitionImportMode.ReplaceExisting, CancellationToken.None)).Value!;
        Assert.Single(reviewed.Bundle.Definitions);
        Assert.True((await store.CommitImportAsync(reviewed, CancellationToken.None, reviewed.AcknowledgeExecutionReview())).IsSuccess);
        var restored = (await new SqliteDefinitionRepository<WorkspaceDefinition>(temporary.Database, TimeProvider.System)
            .GetAsync(workspace.Key, CancellationToken.None)).Value!.Value;
        Assert.Equal(reference, Assert.Single(restored.Entries));
    }

    [Fact]
    public async Task ImportedLocalDatabaseTokenBecomesReconnectWithoutLosingOtherPanels()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var layout = DurableDefinitionFixtures.Layout();
        var token = DatabaseRecoveryToken.Create().Serialize();
        var panel = new ScreenPanelDefinition(new("private-db"), new("main"), ScreenPanelKind.DatabaseViewer,
            "Database", null, new PanelStartupBehavior(token));
        var screen = new ScreenDefinition(new("local-database"), 1, "Local database", null, layout.Id, [panel]);
        var bundle = new PortableDefinitionBundle(1, DateTimeOffset.UtcNow,
            [DurableDefinitionFixtures.Document(layout), DurableDefinitionFixtures.Document(screen)]);
        var preflight = (await store.PreflightImportAsync(bundle, DefinitionImportMode.ReplaceExisting, CancellationToken.None)).Value!;
        Assert.Contains(preflight.Issues, issue => issue.Code == DefinitionImportIssueCode.ImportedDatabaseRecoveryDetached);
        Assert.Contains(preflight.ExecutionReview, item => item.Details.Contains("not imported", StringComparison.Ordinal));
        Assert.True((await store.CommitImportAsync(preflight, CancellationToken.None, preflight.AcknowledgeExecutionReview())).IsSuccess);
        var restored = (await new SqliteDefinitionRepository<ScreenDefinition>(temporary.Database, TimeProvider.System)
            .GetAsync(screen.Key, CancellationToken.None)).Value!.Value;
        Assert.Equal(layout.Id, restored.LayoutId);
        Assert.Equal(panel.Id, Assert.Single(restored.Panels).Id);
        Assert.Equal(DatabaseRecoveryToken.ReconnectTarget, restored.Panels[0].Startup.Location);
        var exported = (await store.ExportAsync(CancellationToken.None)).Value!;
        Assert.All(exported.Definitions, document => Assert.DoesNotContain(token, document.PayloadJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Executable_import_requires_exact_review_and_preserves_all_approved_values()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var layout = DurableDefinitionFixtures.Layout();
        var screen = new ScreenDefinition(new("commands"), ScreenDefinition.CurrentSchemaVersion, "Commands", null,
            layout.Id, [new(new("panel"), new("main"), ScreenPanelKind.Terminal, "Task", null,
                new PanelStartupBehavior(commands: ["echo reviewed", "printf second"]))]);
        var workspace = new WorkspaceDefinition(new("isolated"), WorkspaceDefinition.CurrentSchemaVersion,
            "Isolated", null, null, [], isIsolated: true,
            isolationMounts: [new("/tmp/review-host", "/mnt/work", false)],
            isolationImageReference: "registry.example/tools:reviewed");
        var documents = new[] { DurableDefinitionFixtures.Document(layout), DurableDefinitionFixtures.Document(screen),
            DurableDefinitionFixtures.Document(workspace) };
        var bundle = new PortableDefinitionBundle(1, DateTimeOffset.UtcNow, documents);
        var reviewed = (await store.PreflightImportAsync(bundle, DefinitionImportMode.ReplaceExisting, CancellationToken.None)).Value!;
        Assert.True(reviewed.CanCommit);
        Assert.Contains(reviewed.ExecutionReview, item => item.Details.Contains("printf second", StringComparison.Ordinal));
        Assert.Contains(reviewed.ExecutionReview, item => item.Details.Contains("WRITABLE", StringComparison.Ordinal)
            && item.Details.Contains("registry.example/tools:reviewed", StringComparison.Ordinal));
        var approval = reviewed.AcknowledgeExecutionReview();
        var denied = await store.CommitImportAsync(reviewed, CancellationToken.None);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, denied.Error?.Code);
        var copied = reviewed with { };
        Assert.False(approval.AppliesTo(copied));
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition,
            (await store.CommitImportAsync(copied, CancellationToken.None, approval)).Error?.Code);
        var forged = new DefinitionImportPreflight(reviewed.Bundle, reviewed.Mode, []);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition,
            (await store.CommitImportAsync(forged, CancellationToken.None, forged.AcknowledgeExecutionReview())).Error?.Code);
        Assert.Empty((await store.ExportAsync(CancellationToken.None)).Value!.Definitions);

        // Changing the caller's original list cannot change the already-reviewed plan.
        documents[1] = documents[1] with { PayloadJson = "{}" };
        var committed = await store.CommitImportAsync(reviewed, CancellationToken.None, approval);
        Assert.True(committed.IsSuccess, committed.Error?.Message);
        var restoredScreen = await new SqliteDefinitionRepository<ScreenDefinition>(temporary.Database, TimeProvider.System)
            .GetAsync(screen.Key, CancellationToken.None);
        Assert.Equal(screen.Panels[0].Startup.Commands, restoredScreen.Value!.Value.Panels[0].Startup.Commands);
        var restoredWorkspace = await new SqliteDefinitionRepository<WorkspaceDefinition>(temporary.Database, TimeProvider.System)
            .GetAsync(workspace.Key, CancellationToken.None);
        Assert.Equal(workspace.IsolationMounts, restoredWorkspace.Value!.Value.IsolationMounts);
        Assert.Equal(workspace.IsolationImageReference, restoredWorkspace.Value.Value.IsolationImageReference);
    }

    [Fact]
    public async Task Changed_saved_ssh_destination_invalidates_database_import_approval_without_writes()
    {
        await using var temporary = TemporaryDatabase.Create();
        var routes = new SqliteDefinitionRepository<ConnectionProfile>(temporary.Database, TimeProvider.System);
        var route = Route("approved.example");
        Assert.True((await routes.SaveAsync(route, null, CancellationToken.None)).IsSuccess);
        var database = new DatabaseConnectionProfile(new("database"), 1, "Database", "sqlserver",
            "Server=database.internal;Integrated Security=true", tunnelConnectionId: route.Id);
        var store = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var bundle = new PortableDefinitionBundle(1, DateTimeOffset.UtcNow, [DurableDefinitionFixtures.Document(database)]);
        var reviewed = (await store.PreflightImportAsync(bundle, DefinitionImportMode.ReplaceExisting, CancellationToken.None)).Value!;
        Assert.True(reviewed.CanCommit);
        Assert.Contains(reviewed.ExecutionReview, item => item.Details.Contains("approved.example", StringComparison.Ordinal)
            && item.Details.Contains("Integrated Security=true", StringComparison.Ordinal));
        var approval = reviewed.AcknowledgeExecutionReview();
        Assert.True((await routes.SaveAsync(Route("changed.example"), 1, CancellationToken.None)).IsSuccess);
        var denied = await store.CommitImportAsync(reviewed, CancellationToken.None, approval);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, denied.Error?.Code);
        Assert.DoesNotContain((await store.ExportAsync(CancellationToken.None)).Value!.Definitions, item => item.Id == database.Id.Value);
        var fresh = (await store.PreflightImportAsync(bundle, DefinitionImportMode.ReplaceExisting, CancellationToken.None)).Value!;
        Assert.Contains(fresh.ExecutionReview, item => item.Details.Contains("changed.example", StringComparison.Ordinal));
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition,
            (await store.CommitImportAsync(fresh, CancellationToken.None, approval)).Error?.Code);
        Assert.True((await store.CommitImportAsync(fresh, CancellationToken.None, fresh.AcknowledgeExecutionReview())).IsSuccess);
    }

    [Fact]
    public async Task Raw_database_panel_and_existing_referenced_connection_are_reviewed()
    {
        await using var temporary = TemporaryDatabase.Create();
        var route = Route("approved.example");
        Assert.True((await new SqliteDefinitionRepository<ConnectionProfile>(temporary.Database, TimeProvider.System)
            .SaveAsync(route, null, CancellationToken.None)).IsSuccess);
        var layout = DurableDefinitionFixtures.Layout();
        var screen = new ScreenDefinition(new("database-screen"), 1, "Database screen", null, layout.Id,
            [new(new("database-panel"), new("main"), ScreenPanelKind.DatabaseViewer, null, null,
                new PanelStartupBehavior(location: "sqlserver:Server=raw.example;Integrated Security=true"))]);
        var workspace = new WorkspaceDefinition(new("workspace"), 1, "Workspace", null, null,
            [new WorkspaceEntry.ConnectionReference(new("route-entry"), route.Id)]);
        var store = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var reviewed = (await store.PreflightImportAsync(new(1, DateTimeOffset.UtcNow,
            [DurableDefinitionFixtures.Document(layout), DurableDefinitionFixtures.Document(screen), DurableDefinitionFixtures.Document(workspace)]),
            DefinitionImportMode.ReplaceExisting, CancellationToken.None)).Value!;
        Assert.True(reviewed.CanCommit);
        Assert.Contains(reviewed.ExecutionReview, item => item.Details.Contains("raw.example", StringComparison.Ordinal));
        Assert.Contains(reviewed.ExecutionReview, item => item.Details.Contains("approved.example", StringComparison.Ordinal));
    }

    private static ConnectionProfile Route(string host) => new(new("route"), 1, "SSH route",
        new ConnectionEndpoint.Ssh(host, username: "operator"), new ConnectionAuthentication.SshAgent(),
        new ConnectionStartup(command: "echo route-startup"), ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
}
