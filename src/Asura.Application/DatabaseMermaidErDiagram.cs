using System.Text;

namespace Asura.Application;

/// <summary>
/// Serializes Asura's provider-neutral schema graph as Mermaid ER source.
/// The raw source feeds an in-app renderer; <see cref="Create"/> keeps the
/// fenced Markdown export used by Mermaid-aware documentation tools.
/// </summary>
public static class DatabaseMermaidErDiagram
{
    public static string Create(DatabaseSchemaGraph graph)
    {
        var source = CreateSource(graph);
        return $"```mermaid{Environment.NewLine}{source}```{Environment.NewLine}";
    }

    public static string CreateSource(DatabaseSchemaGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var tables = graph.Tables
            .OrderBy(table => table.Object.Id.DisplayName, StringComparer.Ordinal)
            .ThenBy(table => table.Object.Id.Catalog, StringComparer.Ordinal)
            .ToArray();
        var ids = tables
            .Select((table, index) => (Object: table.Object.Id, Entity: $"T{index + 1}"))
            .ToDictionary(pair => pair.Object, pair => pair.Entity, DatabaseObjectIdComparer.Instance);
        var builder = new StringBuilder();
        builder.AppendLine("erDiagram");
        builder.AppendLine("    direction LR");

        foreach (var table in tables)
        {
            var tableId = ids[table.Object.Id];
            builder.Append("    ").Append(tableId).Append("[\"")
                .Append(EscapeQuoted(table.Object.DisplayName)).AppendLine("\"] {");
            var foreignKeyColumns = table.ForeignKeys
                .SelectMany(key => key.Columns)
                .Select(column => column.ColumnName)
                .ToHashSet(StringComparer.Ordinal);
            var usedColumnNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in table.Columns.OrderBy(column => column.Ordinal))
            {
                var columnName = UniqueToken(column.Name, "column", usedColumnNames);
                var typeName = Token(column.DataTypeName, "other");
                var keys = new List<string>(2);
                if (column.IsPrimaryKey)
                {
                    keys.Add("PK");
                }

                if (foreignKeyColumns.Contains(column.Name))
                {
                    keys.Add("FK");
                }

                var notes = new List<string>(3);
                if (!string.Equals(columnName, column.Name, StringComparison.Ordinal))
                {
                    notes.Add(column.Name);
                }

                if (column.IsNullable == false)
                {
                    notes.Add("not null");
                }

                if (column.IsIdentity)
                {
                    notes.Add("identity");
                }
                else if (column.IsGenerated)
                {
                    notes.Add("generated");
                }

                builder.Append("        ").Append(typeName).Append(' ').Append(columnName);
                if (keys.Count > 0)
                {
                    builder.Append(' ').Append(string.Join(',', keys));
                }

                if (notes.Count > 0)
                {
                    builder.Append(" \"").Append(EscapeQuoted(string.Join(" · ", notes))).Append('"');
                }

                builder.AppendLine();
            }

            builder.AppendLine("    }");
        }

        foreach (var child in tables)
        {
            foreach (var foreignKey in child.ForeignKeys.OrderBy(key => key.Name, StringComparer.Ordinal))
            {
                if (!ids.TryGetValue(foreignKey.ReferencedObject, out var parentId)
                    || foreignKey.Columns.Count == 0)
                {
                    continue;
                }

                var required = foreignKey.Columns.All(pair => child.Columns.Any(column =>
                    string.Equals(column.Name, pair.ColumnName, StringComparison.Ordinal)
                    && column.IsNullable == false));
                builder.Append("    ").Append(parentId)
                    .Append(required ? " ||--o{ " : " o|--o{ ")
                    .Append(ids[child.Object.Id]).Append(" : \"")
                    .Append(EscapeQuoted(foreignKey.Name)).AppendLine("\"");
            }
        }

        return builder.ToString();
    }

    private static string UniqueToken(string value, string fallback, ISet<string> used)
    {
        var root = Token(value, fallback);
        var candidate = root;
        for (var suffix = 2; !used.Add(candidate); suffix++)
        {
            candidate = $"{root}_{suffix}";
        }

        return candidate;
    }

    private static string Token(string value, string fallback)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character == '_'
                ? character
                : '_');
        }

        var token = builder.ToString().Trim('_');
        if (token.Length == 0)
        {
            token = fallback;
        }

        return char.IsAsciiLetter(token[0]) ? token : $"_{token}";
    }

    private static string EscapeQuoted(string value) => value
        .Replace('"', '\'')
        .Replace('\r', ' ')
        .Replace('\n', ' ');

    private sealed class DatabaseObjectIdComparer : IEqualityComparer<DatabaseObjectId>
    {
        public static DatabaseObjectIdComparer Instance { get; } = new();

        public bool Equals(DatabaseObjectId? x, DatabaseObjectId? y) =>
            ReferenceEquals(x, y)
            || x is not null && y is not null
            && string.Equals(x.Catalog, y.Catalog, StringComparison.Ordinal)
            && string.Equals(x.Schema, y.Schema, StringComparison.Ordinal)
            && string.Equals(x.Name, y.Name, StringComparison.Ordinal);

        public int GetHashCode(DatabaseObjectId value) => HashCode.Combine(
            value.Catalog is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Catalog),
            value.Schema is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Schema),
            StringComparer.Ordinal.GetHashCode(value.Name));
    }
}
