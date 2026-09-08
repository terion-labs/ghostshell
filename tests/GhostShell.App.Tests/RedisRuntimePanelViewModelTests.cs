using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class RedisRuntimePanelViewModelTests
{
    [Fact]
    public async Task InitialRedisBindingIsAcceptedOnlyAfterConnectAndLostWhenPasswordChanges()
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        using var panel = new RedisRuntimePanelViewModel(PanelInstanceId.New(), "Redis",
            new RedisPanelFixtures.StubFactory(new RedisPanelFixtures.StubSession(null)),
            new RedisPanelFixtures.StubCatalog(), "localhost:6379", recovery: new DatabaseRecoveryState(vault));
        var source = new ScreenPanelDefinition(ScreenPanelId.New(), new LayoutSlotId("redis"),
            ScreenPanelKind.DatabaseViewer, "Redis", null, new PanelStartupBehavior("redis:localhost:6379", ["PING"]));
        panel.SourceDefinition = source;
        Assert.False(panel.CanPreserveInitialSourceConnection);
        panel.StartInitialization();
        await panel.Initialization;
        Assert.True(panel.CanPreserveInitialSourceConnection);
        Assert.Equal(source.Startup.Commands,
            WorkspaceAutoSaveCoordinatorTests.CaptureDatabasePanel(panel, source).Startup.Commands);
        panel.SetSessionPassword("replacement-fixture");
        Assert.False(panel.CanPreserveInitialSourceConnection);
        Assert.Empty(WorkspaceAutoSaveCoordinatorTests.CaptureDatabasePanel(panel, source).Startup.Commands);
    }

    [Fact]
    public async Task AdHocRedisRecoveryIsDeferredAndRetainsSessionPasswordInVaultOnly()
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var recovery = new DatabaseRecoveryState(vault);
        using var panel = new RedisRuntimePanelViewModel(PanelInstanceId.New(), "Redis",
            new RedisPanelFixtures.StubFactory(new RedisPanelFixtures.StubSession(null)),
            new RedisPanelFixtures.StubCatalog(), "localhost:6379", sessionPassword: "fixture-password", recovery: recovery);
        Assert.False(panel.IsConnected);
        Assert.Null(panel.RecoveryTarget);
        panel.StartInitialization();
        await panel.Initialization;

        Assert.True(panel.IsConnected);
        Assert.NotNull(DatabaseRecoveryToken.TryParse(panel.RecoveryTarget));
        Assert.Equal("fixture-password", (await recovery.RestoreAsync(CancellationToken.None))!.SessionPassword);
        Assert.DoesNotContain("fixture-password", panel.RecoveryTarget!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restored_saved_connection_waits_for_connect_before_resolving_credential()
    {
        var secret = SecretRef.New();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "redis",
            RedisDatabase.DriverId,
            "localhost:6379",
            secret);
        var resolveCount = 0;
        using var panel = new RedisRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Redis",
            new RedisPanelFixtures.StubFactory(
                new RedisPanelFixtures.StubSession(timeToLive: null)),
            new RedisPanelFixtures.StubCatalog(),
            savedConnection: profile,
            passwordResolver: (reference, _) =>
            {
                Assert.Equal(profile, reference);
                resolveCount++;
                return Task.FromResult<string?>("vaulted");
            },
            deferStoredCredentialAccess: true);

        await panel.Initialization;

        Assert.Equal(0, resolveCount);
        Assert.False(panel.IsConnected);

        await panel.ConnectAsync();

        Assert.Equal(1, resolveCount);
        Assert.True(panel.IsConnected);
    }
}
