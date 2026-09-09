namespace Asura.Databases;

/// <summary>
/// DuckDB's column catalogs expose a generated expression as a default but do
/// not expose whether it is generated. Its canonical CREATE TABLE definition
/// is therefore the authoritative source for the read-only/generated flag.
/// </summary>
internal static class DuckDbTableDefinition
{
    public static IReadOnlySet<string> FindGeneratedColumns(string? createTableSql)
    {
        if (string.IsNullOrWhiteSpace(createTableSql))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var open = createTableSql.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in SplitColumnDefinitions(createTableSql[(open + 1)..]))
        {
            if (ContainsGeneratedClause(definition)
                && ReadIdentifier(definition) is { Length: > 0 } name)
            {
                generated.Add(name);
            }
        }

        return generated;
    }

    private static IEnumerable<string> SplitColumnDefinitions(string sql)
    {
        var start = 0;
        var depth = 0;
        var singleQuoted = false;
        var doubleQuoted = false;
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (character == '\'' && !doubleQuoted)
            {
                if (singleQuoted && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                singleQuoted = !singleQuoted;
                continue;
            }

            if (character == '"' && !singleQuoted)
            {
                if (doubleQuoted && index + 1 < sql.Length && sql[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                doubleQuoted = !doubleQuoted;
                continue;
            }

            if (singleQuoted || doubleQuoted)
            {
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                if (depth == 0)
                {
                    yield return sql[start..index].Trim();
                    yield break;
                }

                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                yield return sql[start..index].Trim();
                start = index + 1;
            }
        }
    }

    private static string? ReadIdentifier(string definition)
    {
        var value = definition.TrimStart();
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] != '"')
        {
            var length = 0;
            while (length < value.Length && !char.IsWhiteSpace(value[length]))
            {
                length++;
            }

            return value[..length];
        }

        var name = new System.Text.StringBuilder();
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] != '"')
            {
                name.Append(value[index]);
                continue;
            }

            if (index + 1 < value.Length && value[index + 1] == '"')
            {
                name.Append('"');
                index++;
                continue;
            }

            return name.ToString();
        }

        return null;
    }

    private static bool ContainsGeneratedClause(string definition)
    {
        string[] expected = ["GENERATED", "ALWAYS", "AS"];
        var matched = 0;
        var singleQuoted = false;
        var doubleQuoted = false;
        for (var index = 0; index < definition.Length;)
        {
            var character = definition[index];
            if (character == '\'' && !doubleQuoted)
            {
                singleQuoted = !singleQuoted;
                index++;
                continue;
            }

            if (character == '"' && !singleQuoted)
            {
                doubleQuoted = !doubleQuoted;
                index++;
                continue;
            }

            if (singleQuoted || doubleQuoted || !char.IsLetter(character))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < definition.Length && char.IsLetter(definition[index]))
            {
                index++;
            }

            var token = definition[start..index];
            matched = token.Equals(expected[matched], StringComparison.OrdinalIgnoreCase)
                ? matched + 1
                : token.Equals(expected[0], StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (matched == expected.Length)
            {
                return true;
            }
        }

        return false;
    }
}
