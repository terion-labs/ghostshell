using GhostShell.Desktop;
using GhostShell.Infrastructure;

namespace GhostShell.Architecture.Tests;

public sealed class BrowserEngineSnapshotCopyTests
{
    [Fact]
    public async Task Platform_copy_preserves_complete_tree_and_database_sidecars()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var root = Directory.CreateTempSubdirectory("ghostshell-engine-copy-");
        try
        {
            var source = PrivateDirectory(root.FullName, "source");
            var destination = PrivateDirectory(root.FullName, "destination");
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            var files = new[] { "first_party_sets.db", "first_party_sets.db-journal", "Local State", "nested/content" };
            foreach (var file in files)
            {
                await File.WriteAllTextAsync(Path.Combine(source, file), "synthetic:" + file);
            }
            await new BrowserEngineSnapshotCopy(new ProcessConnectionCommandRunner()).CopyAsync(source, destination, CancellationToken.None);
            foreach (var file in files)
            {
                Assert.Equal("synthetic:" + file, await File.ReadAllTextAsync(Path.Combine(destination, file)));
                Assert.True(File.Exists(Path.Combine(source, file)));
            }
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Linked_source_is_rejected_before_copy_and_stderr_is_never_exposed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var root = Directory.CreateTempSubdirectory("ghostshell-engine-copy-");
        try
        {
            var source = PrivateDirectory(root.FullName, "source");
            var destination = PrivateDirectory(root.FullName, "destination");
            var runner = new FailedRunner();
            File.CreateSymbolicLink(Path.Combine(source, "link"), "/etc/hosts");
            await Assert.ThrowsAsync<IOException>(() => new BrowserEngineSnapshotCopy(runner).CopyAsync(source, destination, CancellationToken.None));
            Assert.Equal(0, runner.Count);
            File.Delete(Path.Combine(source, "link"));
            var failure = await Assert.ThrowsAsync<IOException>(() => new BrowserEngineSnapshotCopy(runner).CopyAsync(source, destination, CancellationToken.None));
            Assert.Equal(1, runner.Count);
            Assert.DoesNotContain("private-path-sentinel", failure.ToString(), StringComparison.Ordinal);
        }
        finally { root.Delete(recursive: true); }
    }

    private static string PrivateDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private sealed class FailedRunner : IConnectionCommandRunner
    {
        public int Count { get; private set; }
        public ValueTask<ConnectionProbeResult> RunAsync(ConnectionProbeCommand command, CancellationToken cancellationToken)
        {
            Count++;
            Assert.Equal("/bin/cp", command.Executable);
            Assert.Equal(["-R", "-P", "-X"], command.Arguments.Take(3), StringComparer.Ordinal);
            return ValueTask.FromResult(new ConnectionProbeResult(ConnectionProbeOutcome.Exited, 1, "private-path-sentinel"));
        }
    }
}
