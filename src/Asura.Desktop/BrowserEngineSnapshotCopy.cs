using Asura.Files;
using Asura.Infrastructure;

namespace Asura.Desktop;

/// <summary>
/// Copies the stopped macOS engine using a separate platform process. Opening and
/// closing an FPS database descriptor in the browser process would release its
/// retained POSIX locks, even without flock. The child never owns those locks.
/// This is not a live SQLite backup and must follow successful CEF shutdown.
/// </summary>
internal sealed class BrowserEngineSnapshotCopy(IConnectionCommandRunner runner)
{
    public async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The engine snapshot copier is macOS-specific.");
        }

        PrivateContentPathGuard.ValidatePrivateDirectory(source);
        PrivateContentPathGuard.ValidatePrivateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException("The engine snapshot destination must be empty.");
        }

        var archiveBudget = MeasureTree(source, cancellationToken);
        // Reserve both an ordinary copy and an uncompressed archive. cp may use
        // an APFS clone, but correctness and disk admission do not depend on it.
        _ = new DiskWriteHeadroom(destination, checked(archiveBudget * 2));
        var result = await runner.RunAsync(new ConnectionProbeCommand(
            "/bin/cp", ["-R", "-P", "-X", Path.TrimEndingDirectorySeparator(source) + "/.", destination],
            TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Outcome != ConnectionProbeOutcome.Exited || result.ExitCode != 0)
        {
            // cp's stderr contains private paths. Do not include it in errors.
            throw new IOException("The stopped browser engine snapshot could not be completed.");
        }

        PrivateContentPathGuard.ValidatePrivateDirectory(destination);
        _ = new DiskWriteHeadroom(destination, archiveBudget);
    }

    private static long MeasureTree(string root, CancellationToken cancellationToken)
    {
        long budget = 0;
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(root));
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("Browser engine snapshot source contains a filesystem link.");
                }

                budget = checked(budget + 4096);
                if (entry is DirectoryInfo child)
                {
                    directories.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    budget = checked(budget + file.Length);
                }
            }
        }

        return budget;
    }
}
