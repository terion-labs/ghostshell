namespace Asura.Infrastructure;

internal sealed class ProfileDatabaseLock : IDisposable
{
    private readonly FileStream _stream;

    private ProfileDatabaseLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ProfileDatabaseLock Acquire(string databasePath)
    {
        var lockPath = $"{databasePath}.lock";
        var stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.None);
        return new ProfileDatabaseLock(stream);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
