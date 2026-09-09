using Asura.Core;
using Asura.Infrastructure;

namespace Asura.Infrastructure.Tests;

/// <summary>
/// A database profile's inline tunnel is a whole embedded connection profile
/// with polymorphic endpoint and authentication payloads. This proves the
/// real store gives it back intact — an inline tunnel that deserializes to
/// null would silently connect straight to the database.
/// </summary>
public sealed class DatabaseConnectionProfilePersistenceTests
{
    [Fact]
    public async Task An_inline_tunnel_survives_the_definition_store()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<DatabaseConnectionProfile>(
            temporary.Database,
            TimeProvider.System);

        var profileId = DatabaseConnectionProfileId.New();
        var tunnelId = DatabaseConnectionProfile.InlineTunnelId(profileId);
        var profile = new DatabaseConnectionProfile(
            profileId,
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "Analytics",
            "postgres",
            "Host=warehouse.internal;Database=events;Username=reader",
            inlineTunnel: new ConnectionProfile(
                tunnelId,
                ConnectionProfile.CurrentSchemaVersion,
                "Analytics tunnel",
                new ConnectionEndpoint.Ssh("bastion.internal", 2222, "ops"),
                new ConnectionAuthentication.Password(new SecretRef("tunnel-password")),
                ConnectionStartup.Default,
                ConnectionKeepAlive.Disabled,
                SshHostKeyPolicy.AcceptNew));

        var saved = await repository.SaveAsync(profile, null, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);

        var loaded = await repository.GetAsync(profile.Key, CancellationToken.None);
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        var inline = loaded.Value!.Value.InlineTunnel;
        Assert.NotNull(inline);
        Assert.Equal(tunnelId, inline!.Id);
        var ssh = Assert.IsType<ConnectionEndpoint.Ssh>(inline.Endpoint);
        Assert.Equal("bastion.internal", ssh.Host);
        Assert.Equal(2222, ssh.Port);
        Assert.Equal("ops", ssh.Username);
        var password = Assert.IsType<ConnectionAuthentication.Password>(inline.Authentication);
        Assert.Equal(new SecretRef("tunnel-password"), password.PasswordSecret);
    }

    /// <summary>
    /// Profiles saved before the inline tunnel existed load with none — the
    /// added property must stay optional in the stored JSON.
    /// </summary>
    [Fact]
    public async Task A_profile_without_an_inline_tunnel_still_loads()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<DatabaseConnectionProfile>(
            temporary.Database,
            TimeProvider.System);
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "Plain",
            "sqlite",
            "Data Source=/data/app.db");

        Assert.True((await repository.SaveAsync(profile, null, CancellationToken.None)).IsSuccess);
        var loaded = await repository.GetAsync(profile.Key, CancellationToken.None);

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Null(loaded.Value!.Value.InlineTunnel);
        Assert.False(loaded.Value.Value.HasTunnel);
    }
}
