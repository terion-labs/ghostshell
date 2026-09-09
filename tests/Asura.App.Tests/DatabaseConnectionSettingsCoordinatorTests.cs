using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class DatabaseConnectionSettingsCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Draft_create_keeps_identity_and_does_not_become_an_edit(bool alreadyExists)
    {
        var id = DatabaseConnectionProfileId.New();
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        var proxy = (CatalogProxy)(object)catalog;
        if (alreadyExists)
        {
            proxy.Snapshot = DefinitionCatalogSnapshot.Empty with
            {
                DatabaseConnections = [new StoredDefinition<DatabaseConnectionProfile>(
                    new DatabaseConnectionProfile(id, 1, "Existing", "postgres", "Host=existing"),
                    1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)],
            };
        }
        var database = DispatchProxy.Create<IDatabaseConnectionCatalog, DatabaseCatalogProxy>();
        var vault = DispatchProxy.Create<ISecretVault, VaultProxy>();
        var coordinator = new DatabaseConnectionSettingsCoordinator(catalog, database, vault, _ => { }, _ => { });
        var result = await coordinator.SaveDatabaseConnectionAsync(null, "Draft", "postgres", new DatabaseConnectionDetails(Host: "db"),
            false, null, draftId: id);
        if (alreadyExists)
        {
            Assert.Null(result);
            Assert.Null(proxy.SavedProfile);
        }
        else
        {
            Assert.Equal(id, result!.Id);
            Assert.Null(proxy.ExpectedRevision);
        }
    }

    [Fact]
    public async Task Missing_edit_identity_does_not_silently_create_a_new_profile()
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        var database = DispatchProxy.Create<IDatabaseConnectionCatalog, DatabaseCatalogProxy>();
        var vault = DispatchProxy.Create<ISecretVault, VaultProxy>();
        var coordinator = new DatabaseConnectionSettingsCoordinator(catalog, database, vault, _ => { }, _ => { });
        var result = await coordinator.SaveDatabaseConnectionAsync(DatabaseConnectionProfileId.New(), "Missing", "postgres",
            new DatabaseConnectionDetails(Host: "db"), false, null);
        Assert.Null(result);
        Assert.Null(((CatalogProxy)(object)catalog).SavedProfile);
    }

    [Fact]
    public async Task Save_strips_the_password_and_forwards_a_null_create_revision()
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        var catalogProxy = (CatalogProxy)(object)catalog;
        var database = DispatchProxy.Create<IDatabaseConnectionCatalog, DatabaseCatalogProxy>();
        var databaseProxy = (DatabaseCatalogProxy)(object)database;
        var vault = DispatchProxy.Create<ISecretVault, VaultProxy>();
        var errors = new List<string>();
        var coordinator = new DatabaseConnectionSettingsCoordinator(
            catalog,
            database,
            vault,
            errors.Add,
            errors.Add);

        var saved = await coordinator.SaveDatabaseConnectionAsync(
            existingId: null,
            "  Production  ",
            "postgres",
            new DatabaseConnectionDetails(
                Host: "db.internal",
                Database: "app",
                Password: "session-only"),
            storePassword: false,
            tunnelConnectionId: null,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal("Production", catalogProxy.SavedProfile?.Name);
        Assert.Null(catalogProxy.ExpectedRevision);
        Assert.Null(databaseProxy.BuiltDetails?.Password);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Missing_database_catalog_reports_an_error_before_persistence()
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        var catalogProxy = (CatalogProxy)(object)catalog;
        var vault = DispatchProxy.Create<ISecretVault, VaultProxy>();
        var errors = new List<string>();
        var coordinator = new DatabaseConnectionSettingsCoordinator(
            catalog,
            databaseConnectionCatalog: null,
            vault,
            errors.Add,
            errors.Add);

        var saved = await coordinator.SaveDatabaseConnectionAsync(
            existingId: null,
            "Production",
            "postgres",
            new DatabaseConnectionDetails(),
            storePassword: false,
            tunnelConnectionId: null,
            cancellationToken: CancellationToken.None);

        Assert.Null(saved);
        Assert.Null(catalogProxy.SavedProfile);
        Assert.Contains(errors, error => error.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    public class CatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot Snapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public DatabaseConnectionProfile? SavedProfile { get; private set; }

        public long? ExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                nameof(IDefinitionCatalog.SaveDatabaseConnectionAsync) => Save(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object Save(object?[] args)
        {
            SavedProfile = (DatabaseConnectionProfile)args[0]!;
            ExpectedRevision = (long?)args[1];
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>>.Success(
                    new(
                        SavedProfile,
                        (ExpectedRevision ?? 0) + 1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));
        }
    }

    public class DatabaseCatalogProxy : DispatchProxy
    {
        public DatabaseConnectionDetails? BuiltDetails { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Drivers" => Array.Empty<DatabaseDriverDescriptor>(),
                nameof(IDatabaseConnectionCatalog.BuildConnectionString) => Build(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object Build(object?[] args)
        {
            BuiltDetails = (DatabaseConnectionDetails)args[1]!;
            return "Host=db.internal;Database=app";
        }
    }

    public class VaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
