using Asura.Application;
using Asura.Infrastructure;

namespace Asura.Infrastructure.Tests;

public sealed class CalciteSqlLanguageNativeIntegrationTests
{
    private const string EnableVariable = "ASURA_RUN_SQL_LANGUAGE_NATIVE";

    [NativeSqlLanguageFact]
    public async Task NativeWorkerCompletesValidatesUpdatesAndShutsDown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var workerPath = Environment.GetEnvironmentVariable(
            CalciteSqlLanguageService.WorkerPathEnvironment);
        Assert.False(
            string.IsNullOrWhiteSpace(workerPath),
            $"Set {CalciteSqlLanguageService.WorkerPathEnvironment} to the real native worker.");

        var service = new CalciteSqlLanguageService();
        Assert.True(
            service.IsAvailable,
            $"Native SQL language worker was not found at '{workerPath}'.");

        await using var session = await service.OpenSessionAsync(
            Catalog("people", "name"),
            timeout.Token);
        Assert.True(session.IsAvailable, "The native worker did not initialize its catalog.");

        var empty = await session.CompleteAsync(
            string.Empty,
            0,
            timeout.Token);
        Assert.Contains(empty.Items, item =>
            item.Kind == SqlCompletionItemKind.Table && string.Equals(item.Label, "people", StringComparison.Ordinal));
        Assert.Contains(empty.Items, item =>
            item.Kind == SqlCompletionItemKind.Keyword && string.Equals(item.Label, "SELECT", StringComparison.Ordinal));

        const string aliasSql = "SELECT p. FROM people p";
        var aliasCompletion = await session.CompleteAsync(
            aliasSql,
            aliasSql.IndexOf("p.", StringComparison.Ordinal) + 2,
            timeout.Token);
        Assert.Contains(aliasCompletion.Items, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "id", StringComparison.Ordinal));
        Assert.Contains(aliasCompletion.Items, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "name", StringComparison.Ordinal));

        const string preferredSql = "SELECT na";
        var preferredCompletion = await session.CompleteAsync(
            preferredSql,
            preferredSql.Length,
            new SqlCompletionContext(
                new DatabaseObjectId("app", "public", "people")),
            timeout.Token);
        Assert.Contains(preferredCompletion.Items, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "name", StringComparison.Ordinal));

        const string preferredQualifierSql = "SELECT people";
        var preferredQualifierCompletion = await session.CompleteAsync(
            preferredQualifierSql,
            preferredQualifierSql.Length,
            new SqlCompletionContext(
                new DatabaseObjectId("app", "public", "people")),
            timeout.Token);
        Assert.Contains(preferredQualifierCompletion.Items, item =>
            item.Kind == SqlCompletionItemKind.Column
            && string.Equals(item.Label, "people.name"
, StringComparison.Ordinal) && string.Equals(item.InsertText, "people.name", StringComparison.Ordinal));

        var valid = await session.DiagnoseAsync(
            "SELECT p.id, p.name FROM people p",
            timeout.Token);
        Assert.Empty(valid);

        const string invalidSql = "SELECT p.asura_missing FROM people p";
        var invalid = await session.DiagnoseAsync(
            invalidSql,
            timeout.Token);
        Assert.Contains(invalid, diagnostic =>
            diagnostic.Severity == SqlDiagnosticSeverity.Error
            && diagnostic.Message.Contains(
                "asura_missing",
                StringComparison.OrdinalIgnoreCase));

        await session.UpdateCatalogAsync(
            Catalog("contacts", "email"),
            timeout.Token);
        const string updatedSql = "SELECT c. FROM contacts c";
        var updatedCompletion = await session.CompleteAsync(
            updatedSql,
            updatedSql.IndexOf("c.", StringComparison.Ordinal) + 2,
            timeout.Token);
        Assert.Contains(updatedCompletion.Items, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "email", StringComparison.Ordinal));
        Assert.Empty(await session.DiagnoseAsync(
            "SELECT c.id, c.email FROM contacts c",
            timeout.Token));
        var updatedRoot = await session.CompleteAsync(string.Empty, 0, timeout.Token);
        Assert.Contains(updatedRoot.Items, item =>
            item.Kind == SqlCompletionItemKind.Table && string.Equals(item.Label, "contacts", StringComparison.Ordinal));
        Assert.DoesNotContain(updatedRoot.Items, item =>
            item.Kind == SqlCompletionItemKind.Table && string.Equals(item.Label, "people", StringComparison.Ordinal));

        await session.DisposeAsync();
        Assert.False(session.IsAvailable);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CompleteAsync(
            string.Empty,
            0,
            CancellationToken.None));
    }

    private static SqlCatalogSnapshot Catalog(string tableName, string textColumn) => new(
        "postgres",
        "app",
        "public",
        [
            new SqlCatalogObject(
                new DatabaseObjectId("app", "public", tableName),
                DatabaseTableKind.Table,
                [
                    new SqlCatalogColumn(
                        "id",
                        "bigint",
                        DatabaseValueKind.SignedInteger,
                        false),
                    new SqlCatalogColumn(
                        textColumn,
                        "text",
                        DatabaseValueKind.Text,
                        true),
                ]),
        ]);

    private sealed class NativeSqlLanguageFactAttribute : FactAttribute
    {
        public NativeSqlLanguageFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnableVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {EnableVariable}=1 to exercise the real native Calcite worker.";
            }
        }
    }
}
