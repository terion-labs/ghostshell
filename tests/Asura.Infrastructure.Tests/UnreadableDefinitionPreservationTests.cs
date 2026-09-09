using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class UnreadableDefinitionPreservationTests
{
    private static async Task WriteRawAsync(
        TemporaryDatabase temporary,
        DefinitionKind kind,
        string id,
        string name,
        int schemaVersion,
        string payloadJson)
    {
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO definitions (kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc)
            VALUES ($kind, $id, $schema, 1, $name, $payload, $now, $now);
            """;
        insert.Parameters.AddWithValue("$kind", kind.Value);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$schema", schemaVersion);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$payload", payloadJson);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UnixEpoch.ToString("O"));
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(TemporaryDatabase temporary, DefinitionKind kind)
    {
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM definitions WHERE kind = $kind;";
        count.Parameters.AddWithValue("$kind", kind.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Unsupported_schema_fails_the_list_without_deleting_any_row()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await repository.SaveAsync(
            DurableDefinitionFixtures.Layout(id: "keeper", name: "Keeper"),
            null,
            CancellationToken.None)).IsSuccess);
        await WriteRawAsync(
            temporary,
            DefinitionKind.Layout,
            "future-layout",
            "Future layout",
            schemaVersion: 999,
            """{"schemaVersion":999}""");

        var listed = await repository.ListAsync(CancellationToken.None);

        Assert.False(listed.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.UnsupportedSchema, listed.Error?.Code);
        Assert.Equal(2, await CountAsync(temporary, DefinitionKind.Layout));
    }

    [Fact]
    public async Task Corrupt_payload_fails_the_list_without_deleting_the_row()
    {
        await using var temporary = TemporaryDatabase.Create();
        await WriteRawAsync(
            temporary,
            DefinitionKind.Theme,
            ThemePreference.Default.Id.Value,
            "Automatic",
            ThemePreference.CurrentSchemaVersion,
            """{"not":"a theme"}""");
        var repository = new SqliteDefinitionRepository<ThemePreference>(
            temporary.Database,
            TimeProvider.System);

        var listed = await repository.ListAsync(CancellationToken.None);

        Assert.False(listed.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, listed.Error?.Code);
        Assert.Equal(1, await CountAsync(temporary, DefinitionKind.Theme));
    }

    [Fact]
    public async Task Unsupported_row_in_another_kind_does_not_block_a_valid_list()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var keeper = DurableDefinitionFixtures.Layout();
        Assert.True((await layouts.SaveAsync(
            keeper,
            null,
            CancellationToken.None)).IsSuccess);
        await WriteRawAsync(
            temporary,
            DefinitionKind.Theme,
            ThemePreference.Default.Id.Value,
            "Automatic",
            schemaVersion: 999,
            """{"schemaVersion":999}""");

        var listed = await layouts.ListAsync(CancellationToken.None);

        Assert.True(listed.IsSuccess, listed.Error?.Message);
        Assert.Equal(keeper.Id, Assert.Single(listed.Value!).Value.Id);
        Assert.Equal(1, await CountAsync(temporary, DefinitionKind.Theme));
        Assert.Equal(1, await CountAsync(temporary, DefinitionKind.Layout));
    }
}
