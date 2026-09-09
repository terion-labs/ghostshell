using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class ContainerRelayImageTests
{
    [Theory]
    [InlineData("docker", "arm64", "linux/arm64")]
    [InlineData("docker", "x64", "linux/amd64")]
    [InlineData("podman", "arm64", "linux/arm64")]
    [InlineData("podman", "x64", "linux/amd64")]
    public async Task Uses_direct_build_result_not_a_preexisting_or_retagged_image(string kind, string architecture, string platform)
    {
        using var fixture = new BuildFixture(kind, architecture);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await fixture.Provider.PrepareAsync(new(new WorkspaceId("service-test")), CancellationToken.None);
            var binding = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success>(result).Value;
            try
            {
                Assert.Equal(BuildFixture.BuiltId, binding.RuntimeImageReference);
                var create = fixture.Commands.Last(command => command[0] == "create");
                Assert.Equal(BuildFixture.BuiltId, create[^1]);
                var build = fixture.Commands.Last(command => command[0] == "build");
                Assert.Equal(platform, build[2]);
                Assert.DoesNotContain("--tag", build, StringComparer.Ordinal);
                Assert.Equal(kind == "docker", build.Contains("--load", StringComparer.Ordinal));
                Assert.Empty(Directory.GetFiles(fixture.BuildDirectory!));
            }
            finally { await fixture.Provider.StopAsync(binding, CancellationToken.None); }
            Assert.False(Directory.Exists(fixture.BuildDirectory));
        }
        Assert.Equal(2, fixture.Commands.Count(command => command[0] == "build"));
        Assert.DoesNotContain(fixture.Commands, command => command[0] == "image");
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("localhost/ghostshell-relay:spoofed", 0)]
    [InlineData("sha256:1234", 0)]
    [InlineData(BuildFixture.BuiltId, 1)]
    public async Task Missing_invalid_or_failed_build_never_starts_a_container(string? result, int exitCode)
    {
        using var fixture = new BuildFixture("docker", "arm64") { ImageId = result, BuildExitCode = exitCode };
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Provider.PrepareAsync(
            new(new WorkspaceId("service-test")), CancellationToken.None).AsTask());
        Assert.DoesNotContain(fixture.Commands, command => command[0] == "create");
        Assert.False(Directory.Exists(fixture.BuildDirectory));
    }

    [Fact]
    public async Task Cancelled_build_cleans_private_inputs_without_starting_a_container()
    {
        using var fixture = new BuildFixture("docker", "arm64") { CancelBuild = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.PrepareAsync(
            new(new WorkspaceId("service-test")), CancellationToken.None).AsTask());
        Assert.DoesNotContain(fixture.Commands, command => command[0] == "create");
        Assert.False(Directory.Exists(fixture.BuildDirectory));
    }

    private sealed class BuildFixture : IWorkspaceIsolationCommandRunner, IDisposable
    {
        internal const string BuiltId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly string _directory = Directory.CreateTempSubdirectory("gs-relay-image-test-").FullName;
        internal ContainerRelayIsolationProvider Provider { get; }
        internal List<IReadOnlyList<string>> Commands { get; } = [];
        internal string? BuildDirectory { get; private set; }
        internal string? ImageId { get; init; } = BuiltId;
        internal int BuildExitCode { get; init; }
        internal bool CancelBuild { get; init; }

        internal BuildFixture(string kind, string architecture)
        {
            var archive = Path.Combine(_directory, "payload.tar.gz");
            File.WriteAllText(archive, "verified payload fixture");
            Provider = new(_ => Task.FromResult(new RelayContainerEngine("/fixture/engine", [], architecture, kind)),
                (_, _) => Task.FromResult(archive), this);
        }

        public async ValueTask<WorkspaceIsolationCommandResult> RunAsync(WorkspaceProcessLaunch launch,
            ReadOnlyMemory<byte> standardInput, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(standardInput.IsEmpty);
            var arguments = launch.Arguments;
            Commands.Add(arguments);
            if (arguments[0] == "image")
            {
                // Both the old cache shortcut and a post-build tag lookup would
                // select this different, syntactically valid attacker image ID.
                return new(0, "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "");
            }
            if (arguments[0] != "build") { return new(0, "", ""); }
            BuildDirectory = arguments[^1];
            Assert.Equal(ContainerRelayIsolationProvider.ImageRecipe, await File.ReadAllTextAsync(Path.Combine(BuildDirectory, "Dockerfile"), cancellationToken));
            Assert.Equal("verified payload fixture", await File.ReadAllTextAsync(Path.Combine(BuildDirectory, "payload.tar.gz"), cancellationToken));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(BuildDirectory));
            }
            if (CancelBuild) { throw new OperationCanceledException(cancellationToken); }
            Assert.Equal("--iidfile", arguments[3]);
            Assert.Equal(BuildDirectory, Path.GetDirectoryName(arguments[4]));
            Assert.False(File.Exists(arguments[4]));
            if (ImageId is not null) { await File.WriteAllTextAsync(arguments[4], ImageId + "\n", cancellationToken); }
            return new(BuildExitCode, "untrusted console output", "fixture failure");
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
