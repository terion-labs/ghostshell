using Asura.Application;

namespace Asura.Application.Tests;

public sealed class DatabaseMermaidErDiagramTests
{
    [Fact]
    public void Diagram_includes_isolated_tables_columns_keys_and_relationships()
    {
        var authors = Table(
            "authors",
            [Column("id", 1, primaryKey: true, nullable: false)]);
        var articles = Table(
            "articles",
            [
                Column("id", 1, primaryKey: true, nullable: false),
                Column("author_id", 2, nullable: false),
                Column("subtitle", 3, nullable: true),
            ],
            [
                new DatabaseForeignKeySchema(
                    "fk_articles_author",
                    authors.Object.Id,
                    [new DatabaseForeignKeyColumn("author_id", "id", 1)]),
            ]);
        var audit = Table("audit_log", [Column("message", 1)]);

        var result = DatabaseMermaidErDiagram.Create(
            new DatabaseSchemaGraph([articles, audit, authors]));
        var source = DatabaseMermaidErDiagram.CreateSource(
            new DatabaseSchemaGraph([articles, audit, authors]));

        Assert.StartsWith("```mermaid\nerDiagram\n    direction LR\n", result, StringComparison.Ordinal);
        Assert.StartsWith("erDiagram\n    direction LR\n", source, StringComparison.Ordinal);
        Assert.DoesNotContain("```", source, StringComparison.Ordinal);
        Assert.Contains("T1[\"public.articles\"]", result, StringComparison.Ordinal);
        Assert.Contains("INTEGER id PK \"not null\"", result, StringComparison.Ordinal);
        Assert.Contains("INTEGER author_id FK \"not null\"", result, StringComparison.Ordinal);
        Assert.Contains("T2[\"public.audit_log\"]", result, StringComparison.Ordinal);
        Assert.Contains("T3[\"public.authors\"]", result, StringComparison.Ordinal);
        Assert.Contains("T3 ||--o{ T1 : \"fk_articles_author\"", result, StringComparison.Ordinal);
        Assert.EndsWith("```\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagram_uses_optional_parent_cardinality_for_nullable_foreign_keys()
    {
        var parent = Table("parent", [Column("id", 1, primaryKey: true, nullable: false)]);
        var child = Table(
            "child",
            [Column("parent_id", 1, nullable: true)],
            [
                new DatabaseForeignKeySchema(
                    "optional parent",
                    parent.Object.Id,
                    [new DatabaseForeignKeyColumn("parent_id", "id", 1)]),
            ]);

        var result = DatabaseMermaidErDiagram.Create(new DatabaseSchemaGraph([parent, child]));

        Assert.Contains("T2 o|--o{ T1 : \"optional parent\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagram_sanitizes_mermaid_tokens_without_losing_exact_labels()
    {
        var table = new DatabaseSchemaTable(
            new DatabaseTableDescriptor("order details\"", DatabaseTableKind.Table, Schema: "odd schema"),
            [
                new DatabaseColumnSchema(
                    "1st value",
                    1,
                    "numeric(12, 2)",
                    DatabaseValueKind.Decimal),
                new DatabaseColumnSchema(
                    "1st-value",
                    2,
                    "text[]",
                    DatabaseValueKind.Collection),
            ],
            []);

        var result = DatabaseMermaidErDiagram.Create(new DatabaseSchemaGraph([table]));

        Assert.Contains("T1[\"odd schema.order details'\"]", result, StringComparison.Ordinal);
        Assert.Contains("numeric_12__2 _1st_value \"1st value\"", result, StringComparison.Ordinal);
        Assert.Contains("text _1st_value_2 \"1st-value\"", result, StringComparison.Ordinal);
    }

    private static DatabaseSchemaTable Table(
        string name,
        IReadOnlyList<DatabaseColumnSchema> columns,
        IReadOnlyList<DatabaseForeignKeySchema>? foreignKeys = null) =>
        new(
            new DatabaseTableDescriptor(name, DatabaseTableKind.Table, Schema: "public"),
            columns,
            foreignKeys ?? []);

    private static DatabaseColumnSchema Column(
        string name,
        int ordinal,
        bool primaryKey = false,
        bool? nullable = null) =>
        new(
            name,
            ordinal,
            "INTEGER",
            DatabaseValueKind.SignedInteger,
            IsNullable: nullable,
            IsPrimaryKey: primaryKey,
            PrimaryKeyOrdinal: primaryKey ? 1 : null);
}
