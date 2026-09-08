using GhostShell.Files;

namespace GhostShell.ConnectionBackend;

/// <summary>Only opaque operation IDs can address backend scratch; a live lease excludes cleanup.</summary>
internal sealed class DatabaseWorkspaceScratch : IDisposable
{
    private readonly FileStream _lease;
    private DatabaseWorkspaceScratch(string directory, FileStream lease) { DirectoryPath = directory; _lease = lease; }
    public string DirectoryPath { get; }
    public string ContentDirectoryPath => Path.Combine(DirectoryPath, "content");
    private static string Root => Path.Combine(Path.GetTempPath(), "ghostshell-database-operations");

    public static void Prepare(string operationId)
    {
        var directory = Resolve(operationId);
        CreateDirectory(Root);
        if (Directory.Exists(directory)) { throw new IOException("The database operation already exists."); }
        CreateDirectory(directory);
        CreateDirectory(Path.Combine(directory, "content"));
        using var lease = CreateFile(Path.Combine(directory, "lease"));
    }

    public static DatabaseWorkspaceScratch Acquire(string operationId)
    {
        var directory = Resolve(operationId);
        ValidateDirectory(directory);
        var lease = OpenLease(directory);
        try
        {
            if (File.Exists(Path.Combine(directory, "closing")))
            {
                throw new IOException("The database operation is being cleaned up.");
            }
            return new(directory, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public static async Task CleanupAsync(string operationId, CancellationToken token)
    {
        var directory = Resolve(operationId);
        if (!Directory.Exists(directory)) { return; }
        ValidateDirectory(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        FileStream lease;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                var leasePath = Path.Combine(directory, "lease");
                lease = File.Exists(leasePath) ? OpenLease(directory) : CreateFile(leasePath);
                break;
            }
            catch (IOException) when (Directory.Exists(directory))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
        }
        using (lease)
        {
            // A delayed child must not enter after cleanup releases the lease.
            var closing = Path.Combine(directory, "closing");
            if (!File.Exists(closing)) { using var marker = CreateFile(closing); }
        }
        Directory.Delete(directory, recursive: true);
    }

    private static string Resolve(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out var id)
            || !string.Equals(operationId, id.ToString("N"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The database operation identifier is invalid.");
        }
        return Path.Combine(Root, operationId);
    }

    private static void ValidateDirectory(string directory)
    {
        PrivateContentPathGuard.ValidatePrivateDirectory(Root);
        PrivateContentPathGuard.ValidatePrivateDirectory(directory);
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        PrivateContentPathGuard.ValidatePrivateDirectory(path);
    }

    private static FileStream OpenLease(string directory)
    {
        var path = Path.Combine(directory, "lease");
        PrivateContentPathGuard.ValidatePrivateFile(path);
        return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static FileStream CreateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        return new FileStream(path, options);
    }

    public void Dispose() => _lease.Dispose();
}
