namespace Asura.Infrastructure.Tests;

internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private TemporaryDatabase(string directoryPath, string databasePath)
    {
        DirectoryPath = directoryPath;
        DatabasePath = databasePath;
        Database = CreateDatabase(databasePath);
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public AsuraDatabase Database { get; private set; }

    public static TemporaryDatabase Create()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "asura-infrastructure-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new TemporaryDatabase(directory, Path.Combine(directory, "test.db"));
    }

    public async ValueTask ReopenAsync()
    {
        await Database.DisposeAsync();
        Database = CreateDatabase(DatabasePath);
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static AsuraDatabase CreateDatabase(string databasePath) =>
        new(new SqliteStorageOptions(databasePath), TimeProvider.System);
}
