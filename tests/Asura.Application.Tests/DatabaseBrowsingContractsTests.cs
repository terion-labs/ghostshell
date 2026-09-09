using Asura.Application;

namespace Asura.Application.Tests;

public sealed class DatabaseBrowsingContractsTests
{
    [Theory]
    [InlineData(null, null, "events", "events")]
    [InlineData("warehouse", "analytics", "events", "analytics.events")]
    [InlineData("warehouse", " ", "events", "events")]
    public void Object_identity_keeps_qualification_while_displaying_the_useful_name(
        string? catalog,
        string? schema,
        string name,
        string expectedDisplayName)
    {
        var id = new DatabaseObjectId(catalog, schema, name);

        Assert.Equal(catalog, id.Catalog);
        Assert.Equal(schema, id.Schema);
        Assert.Equal(name, id.Name);
        Assert.Equal(expectedDisplayName, id.DisplayName);
    }

    [Fact]
    public void Catalog_remains_part_of_identity_even_when_it_is_not_in_the_display_name()
    {
        var production = new DatabaseObjectId("production", "public", "people");
        var staging = new DatabaseObjectId("staging", "public", "people");

        Assert.Equal(production.DisplayName, staging.DisplayName);
        Assert.NotEqual(production, staging);
    }

    [Fact]
    public void Query_page_projects_legacy_text_rows_into_typed_value_slots()
    {
        var page = new DatabaseQueryPage(
            [
                new DatabaseColumnDescriptor(
                    "id",
                    "INTEGER",
                    DatabaseValueKind.SignedInteger),
                new DatabaseColumnDescriptor("nickname", "TEXT", DatabaseValueKind.Text),
            ],
            [["42", null]],
            Truncated: false,
            RowsAffected: 0,
            TimeSpan.Zero);

        var row = Assert.Single(page.ValueRows);
        Assert.Equal("42", row[0].RawValue);
        Assert.Equal(DatabaseValueKind.SignedInteger, row[0].Kind);
        Assert.Equal("42", row[0].DisplayText);
        Assert.False(row[0].IsNull);
        Assert.Null(row[1].RawValue);
        Assert.Equal(DatabaseValueKind.Text, row[1].Kind);
        Assert.Equal("NULL", row[1].DisplayText);
        Assert.True(row[1].IsNull);
    }

    [Fact]
    public void Query_page_prefers_provider_typed_rows_over_the_text_fallback()
    {
        IReadOnlyList<IReadOnlyList<DatabaseValue>> typedRows =
        [
            [
                new DatabaseValue(42L, DatabaseValueKind.SignedInteger, "42"),
                new DatabaseValue(null, DatabaseValueKind.Text, "NULL"),
            ],
        ];
        var page = new DatabaseQueryPage(
            [
                new DatabaseColumnDescriptor("id", "INTEGER"),
                new DatabaseColumnDescriptor("nickname", "TEXT"),
            ],
            [["legacy", "legacy"]],
            Truncated: false,
            RowsAffected: 0,
            TimeSpan.Zero,
            typedRows);

        Assert.Same(typedRows, page.ValueRows);
        Assert.Equal(42L, page.ValueRows[0][0].RawValue);
        Assert.True(page.ValueRows[0][1].IsNull);
    }

    [Fact]
    public void Column_edits_keep_default_null_and_value_as_distinct_states()
    {
        var useDefault = new DatabaseColumnEdit("created_at", DatabaseEditValueState.Default);
        var setNull = new DatabaseColumnEdit("nickname", DatabaseEditValueState.Null);
        var setValue = new DatabaseColumnEdit(
            "nickname",
            DatabaseEditValueState.Value,
            string.Empty);

        Assert.Equal(DatabaseEditValueState.Default, useDefault.State);
        Assert.Null(useDefault.Value);
        Assert.Equal(DatabaseEditValueState.Null, setNull.State);
        Assert.Null(setNull.Value);
        Assert.Equal(DatabaseEditValueState.Value, setValue.State);
        Assert.Equal(string.Empty, setValue.Value);
    }

    [Fact]
    public void Table_query_preserves_typed_filters_sorting_and_page_bounds()
    {
        IReadOnlyList<object?> includedNames = ["Ada", "Grace"];
        IReadOnlyList<object?> excludedIds = [7L, 11L];
        var query = new DatabaseTableQuery(
            [
                new DatabaseFilterCondition(
                    "name",
                    DatabaseFilterOperator.NotContains,
                    "Ada"),
                new DatabaseFilterCondition(
                    "name",
                    DatabaseFilterOperator.In,
                    includedNames),
                new DatabaseFilterCondition(
                    "id",
                    DatabaseFilterOperator.NotIn,
                    excludedIds),
                new DatabaseFilterCondition("deleted_at", DatabaseFilterOperator.IsNull),
            ],
            [new DatabaseSort("created_at", Descending: true)],
            Offset: 100,
            Limit: 25);

        Assert.Equal(100, query.Offset);
        Assert.Equal(25, query.Limit);
        Assert.Collection(
            query.Filters,
            filter =>
            {
                Assert.Equal("name", filter.ColumnName);
                Assert.Equal(DatabaseFilterOperator.NotContains, filter.Operator);
                Assert.Equal("Ada", filter.Value);
            },
            filter =>
            {
                Assert.Equal("name", filter.ColumnName);
                Assert.Equal(DatabaseFilterOperator.In, filter.Operator);
                Assert.Same(includedNames, filter.Value);
            },
            filter =>
            {
                Assert.Equal("id", filter.ColumnName);
                Assert.Equal(DatabaseFilterOperator.NotIn, filter.Operator);
                Assert.Same(excludedIds, filter.Value);
            },
            filter =>
            {
                Assert.Equal("deleted_at", filter.ColumnName);
                Assert.Equal(DatabaseFilterOperator.IsNull, filter.Operator);
                Assert.Null(filter.Value);
            });
        var sort = Assert.Single(query.Sorts);
        Assert.Equal("created_at", sort.ColumnName);
        Assert.True(sort.Descending);
    }

    [Fact]
    public void First_page_has_no_implicit_filters_or_sorting()
    {
        var query = DatabaseTableQuery.FirstPage(200);

        Assert.Empty(query.Filters);
        Assert.Empty(query.Sorts);
        Assert.Equal(0, query.Offset);
        Assert.Equal(200, query.Limit);
    }

    [Fact]
    public void Table_page_keeps_the_exact_filtered_total_separate_from_the_materialized_page()
    {
        var result = new DatabaseQueryPage(
            [new DatabaseColumnDescriptor("id", "INTEGER")],
            [["1"], ["2"]],
            Truncated: true,
            RowsAffected: 0,
            TimeSpan.Zero);
        var page = new DatabaseTablePage(
            result,
            Offset: 50,
            Limit: 2,
            HasMore: true,
            TotalRows: 987);

        Assert.Equal(2, page.Result.ValueRows.Count);
        Assert.Equal(50, page.Offset);
        Assert.Equal(2, page.Limit);
        Assert.True(page.HasMore);
        Assert.Equal(987, page.TotalRows);
    }

    [Fact]
    public void Mutation_result_reports_the_total_without_hiding_conflicts()
    {
        var result = new DatabaseMutationResult(
            Inserted: 2,
            Updated: 3,
            Deleted: 4,
            HasConflict: true,
            Message: "One row changed after it was loaded.");

        Assert.Equal(9, result.TotalAffected);
        Assert.True(result.HasConflict);
        Assert.Equal("One row changed after it was loaded.", result.Message);
    }
}
