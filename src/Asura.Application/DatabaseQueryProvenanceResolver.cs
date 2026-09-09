namespace Asura.Application;

/// <summary>
/// Recognizes only raw results that reproduce one complete catalog table without
/// aliases. Partial, computed, ambiguous, keyless, and view results deliberately
/// have no table provenance.
/// </summary>
public static class DatabaseQueryProvenanceResolver
{
    public static DatabaseQueryTableProvenance? ResolveExactTableProjection(
        DatabaseQueryPage page,
        IReadOnlyList<DatabaseTableDescriptor> catalog,
        DatabaseObjectDetails details)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(details);

        if (page.Columns.Count == 0
            || page.Columns.Count != details.Columns.Count
            || details.PrimaryKey.Count == 0)
        {
            return null;
        }

        var source = page.Columns[0].BaseObject;
        if (source is null)
        {
            return null;
        }

        DatabaseTableDescriptor? table = null;
        foreach (var candidate in catalog)
        {
            if (!MatchesReportedIdentity(candidate.Id, source))
            {
                continue;
            }

            if (table is not null)
            {
                return null;
            }

            table = candidate;
        }

        if (table is not { Kind: DatabaseTableKind.Table }
            || details.Object != table)
        {
            return null;
        }

        var metadataColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in details.Columns)
        {
            if (!metadataColumns.Add(column.Name))
            {
                return null;
            }
        }

        var projectedColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in page.Columns)
        {
            // SqlClient reports the exact BaseObject for ordinary table
            // projections but leaves BaseColumnName null. In that case the
            // output name is still a safe fallback because the checks below
            // require a unique, complete, unaliased metadata-name set from one
            // exact base object. Originless providers do not enter this path.
            var baseColumnName = column.BaseColumnName
                ?? (column.BaseObject is not null ? column.Name : null);
            if (column.BaseObject != source
                || string.IsNullOrWhiteSpace(baseColumnName)
                || !string.Equals(column.Name, baseColumnName, StringComparison.Ordinal)
                || !metadataColumns.Contains(baseColumnName)
                || !projectedColumns.Add(baseColumnName))
            {
                return null;
            }
        }

        if (!projectedColumns.SetEquals(metadataColumns)
            || details.PrimaryKey.Any(key => !projectedColumns.Contains(key.Name)))
        {
            return null;
        }

        return new DatabaseQueryTableProvenance(table, details);
    }

    private static bool MatchesReportedIdentity(
        DatabaseObjectId catalogObject,
        DatabaseObjectId reportedObject) =>
        string.Equals(catalogObject.Name, reportedObject.Name, StringComparison.Ordinal)
        && QualifierMatches(catalogObject.Schema, reportedObject.Schema)
        && QualifierMatches(catalogObject.Catalog, reportedObject.Catalog);

    private static bool QualifierMatches(string? catalogValue, string? reportedValue) =>
        string.Equals(catalogValue, reportedValue, StringComparison.Ordinal);
}
