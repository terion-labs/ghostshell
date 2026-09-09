namespace Asura.Databases.Tests;

public sealed class DuckDbTableDefinitionTests
{
    [Fact]
    public void Finds_generated_columns_in_canonical_table_sql()
    {
        const string sql = """
            CREATE TABLE viewer_rows(
                id BIGINT DEFAULT(nextval('seq')) PRIMARY KEY,
                title VARCHAR DEFAULT('GENERATED ALWAYS AS is text'),
                computed_label VARCHAR GENERATED ALWAYS AS(concat(title, ',', id)),
                "odd""name" BIGINT GENERATED ALWAYS AS((id + 1))
            );
            """;

        var generated = DuckDbTableDefinition.FindGeneratedColumns(sql);

        Assert.Equal(
            ["computed_label", "odd\"name"],
            generated.Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CREATE VIEW example AS SELECT 1")]
    public void Missing_table_definition_has_no_generated_columns(string? sql)
    {
        Assert.Empty(DuckDbTableDefinition.FindGeneratedColumns(sql));
    }
}
