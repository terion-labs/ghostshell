using Asura.Application;
using Asura.Infrastructure;

var catalog = new SqlCatalogSnapshot(
    "sqlite",
    DefaultCatalog: null,
    DefaultSchema: "main",
    [
        new SqlCatalogObject(
            new DatabaseObjectId(null, "main", "people"),
            DatabaseTableKind.Table,
            [
                new SqlCatalogColumn(
                    "id",
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    IsNullable: false),
                new SqlCatalogColumn(
                    "name",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: true),
            ]),
    ]);

ISqlLanguageService service = new CalciteSqlLanguageService();
await using var session = await service.OpenSessionAsync(catalog, CancellationToken.None);
var completion = await session.CompleteAsync(
    "SELECT p. FROM people p",
    9,
    CancellationToken.None);
var diagnostics = await session.DiagnoseAsync(
    "SELECT id FROM people",
    CancellationToken.None);
var invalidDiagnostics = await session.DiagnoseAsync(
    "SELECT missing FROM people",
    CancellationToken.None);

// Missing is a supported installation state. When a real worker path is
// supplied, the same Native-AOT executable exercises the framed process and
// source-generated JSON path rather than a test substitute.
return !service.IsAvailable
    ? completion.Items.Count == 0
        && diagnostics.Count == 0
        && invalidDiagnostics.Count == 0
        ? 0
        : 1
    : session.IsAvailable
        && completion.Items.Any(item => item.Label is "id" or "name")
        && diagnostics.Count == 0
        && invalidDiagnostics.Any(item =>
            item.Severity == SqlDiagnosticSeverity.Error)
        ? 0
        : 1;
