namespace Asura.Infrastructure;

public sealed record SqliteStorageOptions
{
    public SqliteStorageOptions(
        string databasePath,
        TimeSpan? busyTimeout = null,
        bool acquireProfileLock = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var timeout = busyTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeout),
                "The SQLite busy timeout must be between zero and one minute.");
        }

        DatabasePath = fullPath;
        BusyTimeout = timeout;
        AcquireProfileLock = acquireProfileLock;
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Supplies the database password at the moment a connection is built,
    /// so turning application encryption on or off changes the very next
    /// open rather than the next launch. Null, or a null answer, means the
    /// database is plain.
    /// </summary>
    public Func<string?>? PasswordProvider { get; init; }

    public TimeSpan BusyTimeout { get; }

    public bool AcquireProfileLock { get; }

    public string BackupDirectory => Path.Combine(
        Path.GetDirectoryName(DatabasePath)!,
        "backups");

    public static SqliteStorageOptions CreateDefault() =>
        new(AsuraDataPaths.CreateDefault().DatabasePath);
}
