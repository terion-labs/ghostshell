using System.Data.Common;
using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class DatabaseCredentialBoundaryTests
{
    [Theory]
    [InlineData("/tmp/literal;Mode=ReadWrite;Cache=Shared.db")]
    [InlineData("/tmp/quotes\";Password=wrong;name.db")]
    public void Sqlite_preview_preserves_literal_path_and_read_only_mode(string path)
    {
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = FileRuntimePanelViewModel.ReadOnlySqliteFileConnectionString(path),
        };
        Assert.Equal(path, builder["Data Source"]);
        Assert.Equal("ReadOnly", builder["Mode"]);
        Assert.Equal(2, builder.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reused_reference_never_borrows_another_owner_or_destination(bool reuseOwnerId)
    {
        var secret = SecretRef.New();
        var owner = new DatabaseConnectionProfile(DatabaseConnectionProfileId.New(), 1, "owner", "postgres", "Host=trusted", secret);
        var requested = new DatabaseConnectionProfile(reuseOwnerId ? owner.Id : DatabaseConnectionProfileId.New(), 1, "attacker", "postgres", "Host=untrusted", secret);
        var catalog = DispatchProxy.Create<IDefinitionCatalog, DatabaseConnectionSettingsCoordinatorTests.CatalogProxy>();
        ((DatabaseConnectionSettingsCoordinatorTests.CatalogProxy)(object)catalog).Snapshot = DefinitionCatalogSnapshot.Empty with
        {
            DatabaseConnections = [new StoredDefinition<DatabaseConnectionProfile>(owner, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
        };
        // Any vault invocation throws: rejection must happen before secret access.
        var vault = DispatchProxy.Create<ISecretVault, DatabaseConnectionSettingsCoordinatorTests.VaultProxy>();
        var coordinator = new DatabaseConnectionSettingsCoordinator(catalog, null, vault, _ => { }, _ => { });
        Assert.Null(await coordinator.ResolveDatabasePasswordAsync(requested, CancellationToken.None));
    }
}
