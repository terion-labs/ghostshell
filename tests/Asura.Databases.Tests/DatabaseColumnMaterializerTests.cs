using System.Data.Common;
using Asura.Application;
using Asura.Databases;
using Microsoft.Data.Sqlite;

namespace Asura.Databases.Tests;

public sealed class DatabaseColumnMaterializerTests
{
    [Fact]
    public void Maps_optional_dbcolumn_metadata_into_the_application_descriptor()
    {
        var column = new StubDbColumn();

        var descriptor = DatabaseColumnMaterializer.DescribeColumn(column, fallbackOrdinal: 0);

        Assert.Equal("display_id", descriptor.Name);
        Assert.Equal("BIGINT", descriptor.DataTypeName);
        Assert.Equal(DatabaseValueKind.SignedInteger, descriptor.ValueKind);
        Assert.Equal(typeof(long).FullName, descriptor.ClrTypeName);
        Assert.False(descriptor.IsNullable);
        Assert.True(descriptor.IsKey);
        Assert.True(descriptor.IsIdentity);
        Assert.True(descriptor.IsReadOnly);
        Assert.Equal("id", descriptor.BaseColumnName);
        Assert.Equal(
            new DatabaseObjectId("warehouse", "public", "people"),
            descriptor.BaseObject);
    }

    [Fact]
    public void Does_not_invent_a_base_object_when_the_provider_omits_the_base_table()
    {
        var column = new StubDbColumn(includeBaseTable: false);

        var descriptor = DatabaseColumnMaterializer.DescribeColumn(column, fallbackOrdinal: 0);

        Assert.Null(descriptor.BaseObject);
    }

    [Fact]
    public void Hidden_keyinfo_columns_do_not_claim_editable_query_provenance()
    {
        var column = new StubDbColumn(isHidden: true);

        var descriptor = DatabaseColumnMaterializer.DescribeColumn(column, fallbackOrdinal: 0);

        Assert.True(descriptor.IsHidden);
        Assert.Null(descriptor.BaseColumnName);
        Assert.Null(descriptor.BaseObject);
    }

    [Fact]
    public void Aliased_columns_do_not_claim_editable_query_provenance()
    {
        var column = new StubDbColumn(isAliased: true);

        var descriptor = DatabaseColumnMaterializer.DescribeColumn(column, fallbackOrdinal: 0);

        Assert.Null(descriptor.BaseColumnName);
        Assert.Null(descriptor.BaseObject);
    }

    [Fact]
    public void Describes_and_materializes_a_real_sqlite_reader()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE sample(id INTEGER PRIMARY KEY, name TEXT, payload BLOB);
                INSERT INTO sample VALUES (1, '', X'010203');
                """;
            setup.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, payload FROM sample";
        using var reader = command.ExecuteReader();
        var columns = DatabaseValueMaterializer.DescribeColumns(reader);

        Assert.Equal(["id", "name", "payload"], columns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal(
            [
                DatabaseValueKind.SignedInteger,
                DatabaseValueKind.Text,
                DatabaseValueKind.Binary,
            ],
            columns.Select(column => column.ValueKind));
        Assert.All(columns, column =>
        {
            Assert.Equal(new DatabaseObjectId("main", null, "sample"), column.BaseObject);
            Assert.Equal(column.Name, column.BaseColumnName);
        });

        Assert.True(reader.Read());
        var values = columns
            .Select((column, ordinal) => DatabaseValueMaterializer.Materialize(
                reader,
                ordinal,
                column))
            .ToArray();
        Assert.Equal(1L, values[0].RawValue);
        Assert.Equal(string.Empty, values[1].RawValue);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(values[2].RawValue));
        Assert.Equal("0x010203", values[2].DisplayText);
    }

    private sealed class StubDbColumn : DbColumn
    {
        public StubDbColumn(
            bool includeBaseTable = true,
            bool isHidden = false,
            bool isAliased = false)
        {
            ColumnName = "display_id";
            ColumnOrdinal = 2;
            BaseColumnName = "id";
            BaseCatalogName = "warehouse";
            BaseSchemaName = "public";
            BaseTableName = includeBaseTable ? "people" : null;
            DataType = typeof(long);
            DataTypeName = "BIGINT";
            AllowDBNull = false;
            IsKey = true;
            IsAutoIncrement = true;
            IsExpression = true;
            IsHidden = isHidden;
            IsAliased = isAliased;
        }
    }
}
