using Asura.Application;

namespace Asura.Application.Tests;

public sealed class DatabaseQueryProvenanceResolverTests
{
    private static readonly DatabaseTableDescriptor PeopleTable = new(
        "people",
        DatabaseTableKind.Table,
        "warehouse",
        "public");

    private static readonly DatabaseObjectDetails PeopleDetails = new(
        PeopleTable,
        [
            new DatabaseColumnSchema(
                "id",
                0,
                "BIGINT",
                DatabaseValueKind.SignedInteger,
                IsNullable: false,
                IsPrimaryKey: true,
                PrimaryKeyOrdinal: 1),
            new DatabaseColumnSchema(
                "name",
                1,
                "TEXT",
                DatabaseValueKind.Text,
                IsNullable: true),
        ],
        [],
        CanEdit: true);

    [Fact]
    public void Resolves_a_unique_exact_full_table_projection()
    {
        var page = Page(
            Column("name", PeopleTable.Id),
            Column("id", PeopleTable.Id));

        var provenance = DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [PeopleTable],
            PeopleDetails);

        Assert.NotNull(provenance);
        Assert.Equal(PeopleTable, provenance.Table);
        Assert.Same(PeopleDetails, provenance.Details);
    }

    [Fact]
    public void Rejects_an_unqualified_provider_identity_for_a_qualified_target()
    {
        var reportedObject = new DatabaseObjectId(null, null, "people");
        var page = Page(Column("id", reportedObject), Column("name", reportedObject));

        var result = DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [PeopleTable],
            PeopleDetails);

        Assert.Null(result);
    }

    [Fact]
    public void Resolves_an_exact_unqualified_identity_after_provider_normalization()
    {
        var sqliteTable = new DatabaseTableDescriptor("people", DatabaseTableKind.Table);
        var sqliteDetails = PeopleDetails with { Object = sqliteTable };
        var reportedObject = new DatabaseObjectId(null, null, "people");
        var page = Page(Column("id", reportedObject), Column("name", reportedObject));

        var provenance = DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [sqliteTable],
            sqliteDetails);

        Assert.NotNull(provenance);
        Assert.Equal(sqliteTable, provenance.Table);
    }

    [Fact]
    public void Rejects_a_provider_qualifier_missing_from_the_catalog_identity()
    {
        var sqliteTable = new DatabaseTableDescriptor("people", DatabaseTableKind.Table);
        var sqliteDetails = PeopleDetails with { Object = sqliteTable };
        var attachedObject = new DatabaseObjectId("attached", null, "people");
        var page = Page(Column("id", attachedObject), Column("name", attachedObject));

        var provenance = DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [sqliteTable],
            sqliteDetails);

        Assert.Null(provenance);
    }

    [Fact]
    public void Resolves_exact_base_objects_when_the_provider_omits_base_column_names()
    {
        var page = Page(
            new DatabaseColumnDescriptor(
                "id",
                "BIGINT",
                BaseObject: PeopleTable.Id),
            new DatabaseColumnDescriptor(
                "name",
                "TEXT",
                BaseObject: PeopleTable.Id));

        var provenance = Resolve(page);

        Assert.NotNull(provenance);
        Assert.Equal(PeopleTable, provenance.Table);
    }

    [Fact]
    public void Rejects_aliased_partial_computed_and_mixed_object_results()
    {
        var otherObject = new DatabaseObjectId("warehouse", "public", "other_people");

        Assert.Null(Resolve(Page(
            Column("person_id", PeopleTable.Id, baseColumnName: "id"),
            Column("name", PeopleTable.Id))));
        Assert.Null(Resolve(Page(Column("id", PeopleTable.Id))));
        Assert.Null(Resolve(Page(
            Column("id", PeopleTable.Id),
            new DatabaseColumnDescriptor("computed", "TEXT"))));
        Assert.Null(Resolve(Page(
            Column("id", PeopleTable.Id),
            Column("name", otherObject))));
    }

    [Fact]
    public void Rejects_duplicate_output_columns_and_duplicate_catalog_metadata()
    {
        var duplicateProjection = Page(
            Column("id", PeopleTable.Id),
            Column("id", PeopleTable.Id));
        var duplicateDetails = PeopleDetails with
        {
            Columns = [PeopleDetails.Columns[0], PeopleDetails.Columns[0]],
        };

        Assert.Null(Resolve(duplicateProjection));
        Assert.Null(DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            Page(Column("id", PeopleTable.Id), Column("name", PeopleTable.Id)),
            [PeopleTable],
            duplicateDetails));
    }

    [Fact]
    public void Rejects_keyless_tables_views_and_mismatched_details()
    {
        var page = Page(Column("id", PeopleTable.Id), Column("name", PeopleTable.Id));
        var keyless = PeopleDetails with
        {
            Columns = [.. PeopleDetails.Columns
                .Select(column => column with
                {
                    IsPrimaryKey = false,
                    PrimaryKeyOrdinal = null,
                })],
        };
        var view = PeopleTable with { Kind = DatabaseTableKind.View };
        var otherDetails = PeopleDetails with
        {
            Object = PeopleTable with { Schema = "audit" },
        };

        Assert.Null(DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [PeopleTable],
            keyless));
        Assert.Null(DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [view],
            PeopleDetails with { Object = view }));
        Assert.Null(DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [PeopleTable],
            otherDetails));
    }

    private static DatabaseQueryTableProvenance? Resolve(DatabaseQueryPage page) =>
        DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
            page,
            [PeopleTable],
            PeopleDetails);

    private static DatabaseColumnDescriptor Column(
        string name,
        DatabaseObjectId source,
        string? baseColumnName = null) => new(
            name,
            "TEXT",
            BaseColumnName: baseColumnName ?? name,
            BaseObject: source);

    private static DatabaseQueryPage Page(params DatabaseColumnDescriptor[] columns) => new(
        columns,
        [],
        Truncated: false,
        RowsAffected: 0,
        TimeSpan.Zero);
}
