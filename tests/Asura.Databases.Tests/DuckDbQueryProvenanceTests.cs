using DuckDB.NET.Data;

namespace Asura.Databases.Tests;

public sealed class DuckDbQueryProvenanceTests
{
    [Fact]
    public async Task Accepts_only_one_plain_star_from_one_base_table()
    {
        var source = DuckDbQueryProvenance.TryReadSingleStarSource(
            await SerializeAsync(
                "SELECT * FROM \"memory\".\"main\".\"viewer_rows\" "
                + "WHERE score > 10 ORDER BY id DESC LIMIT 50 OFFSET 2;"));

        Assert.NotNull(source);
        Assert.Equal("memory", source.Catalog);
        Assert.Equal("main", source.Schema);
        Assert.Equal("viewer_rows", source.Name);
    }

    [Theory]
    [InlineData("SELECT id, title FROM viewer_rows")]
    [InlineData("SELECT *, score + 1 AS computed FROM viewer_rows")]
    [InlineData("SELECT * EXCLUDE (payload) FROM viewer_rows")]
    [InlineData("SELECT * FROM viewer_rows AS rows")]
    [InlineData("SELECT * FROM viewer_rows JOIN other_rows USING (id)")]
    [InlineData("SELECT * FROM viewer_rows UNION ALL SELECT * FROM other_rows")]
    [InlineData("WITH rows AS (SELECT * FROM viewer_rows) SELECT * FROM rows")]
    [InlineData("SELECT DISTINCT * FROM viewer_rows")]
    [InlineData("SELECT * FROM viewer_rows; SELECT * FROM other_rows")]
    public async Task Rejects_ambiguous_or_transformed_query_shapes(string sql)
    {
        var source = DuckDbQueryProvenance.TryReadSingleStarSource(
            await SerializeAsync(sql));

        Assert.Null(source);
    }

    private static async Task<string> SerializeAsync(string sql)
    {
        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT json_serialize_sql(CAST($asura_sql AS VARCHAR));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "asura_sql";
        parameter.Value = sql;
        command.Parameters.Add(parameter);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
}
