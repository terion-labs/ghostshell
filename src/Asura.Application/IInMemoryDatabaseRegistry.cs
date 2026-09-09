namespace Asura.Application;

/// <summary>
/// Serves a downloaded database image to the database viewer from memory. A
/// remote database previewed here must never be written to disk to satisfy an
/// engine that opens paths; registering its bytes yields a connection string
/// the engine layer resolves back to the image, and unregistering releases it.
/// </summary>
public interface IInMemoryDatabaseRegistry
{
    /// <summary>The connection string that serves this image, read-only.</summary>
    string Register(byte[] database);

    /// <summary>
    /// Releases a registration made by <see cref="Register"/>. Safe against
    /// queries still in flight: the image survives until the last of them
    /// closes.
    /// </summary>
    void Unregister(string connectionString);
}
