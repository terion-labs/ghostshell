using Asura.Application;

namespace Asura.Infrastructure;

internal sealed class UnavailableSqlLanguageSession : ISqlLanguageSession
{
    public static UnavailableSqlLanguageSession Instance { get; } = new();

    private UnavailableSqlLanguageSession()
    {
    }

    public bool IsAvailable => false;

    public string? UnavailableReason => "The SQL intelligence worker is not installed.";

    public Task<SqlCompletionResult> CompleteAsync(
        string sql,
        int cursorOffset,
        SqlCompletionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(cursorOffset);
        if (cursorOffset > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorOffset));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SqlCompletionResult(cursorOffset, 0, []));
    }

    public Task<IReadOnlyList<SqlDiagnostic>> DiagnoseAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);
    }

    public Task UpdateCatalogAsync(
        SqlCatalogSnapshot catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
