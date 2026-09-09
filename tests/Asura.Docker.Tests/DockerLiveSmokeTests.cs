using Asura.Application;
using Asura.Core;
using Asura.Docker;
using Asura.Infrastructure;

namespace Asura.Docker.Tests;

public sealed class DockerLiveSmokeTests
{
    [Fact]
    public async Task GovernedLifecycleControlsOneExactDisposableLocalContainer()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "ASURA_RUN_DOCKER_LIFECYCLE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var image = Environment.GetEnvironmentVariable(
                "ASURA_DOCKER_LIFECYCLE_IMAGE")
            ?? throw new InvalidOperationException(
                "ASURA_DOCKER_LIFECYCLE_IMAGE is required and must name an already-present image with a long-running default command.");
        var containerName = $"asura-lifecycle-{Guid.NewGuid():N}";
        var executor = new ConnectionCommandExecutor(
            new LocalRuntime(),
            new PathConnectionExecutableLocator());
        var client = new DockerEngineClient(executor, TimeProvider.System);
        string? containerId = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var present = await ExecuteDockerAsync(
                executor,
                ["image", "inspect", image],
                timeout.Token);
            Assert.Equal(ConnectionCommandOutcome.Exited, present.Outcome);
            Assert.Equal(0, present.ExitCode);

            var created = await ExecuteDockerAsync(
                executor,
                [
                    "container",
                    "create",
                    "--pull=never",
                    "--name",
                    containerName,
                    "--label",
                    "com.asura.integration-test=governed-lifecycle",
                    image,
                ],
                timeout.Token);
            Assert.Equal(ConnectionCommandOutcome.Exited, created.Outcome);
            Assert.Equal(0, created.ExitCode);
            containerId = created.StandardOutput.Trim();
            Assert.Equal(64, containerId.Length);
            Assert.All(containerId, character => Assert.True(char.IsAsciiHexDigit(character)));

            var factory = new DockerPanelSessionFactory(client, TimeProvider.System);
            await using var session = await factory.CreateAsync(
                new SessionId($"docker-live-{Guid.NewGuid():N}"),
                new DockerSessionTarget(BuiltInConnections.Local, 1),
                timeout.Token);
            Assert.True(session.Capabilities.Contains(
                SessionCapabilities.DockerContainerStart));

            await AssertExactStateAsync(client, containerId, containerName, "created", timeout.Token);
            await ApplyAsync(
                session,
                containerName,
                DockerContainerAction.Start,
                "created",
                timeout.Token);
            await AssertExactStateAsync(client, containerId, containerName, "running", timeout.Token);

            await ApplyAsync(
                session,
                containerName,
                DockerContainerAction.Restart,
                "running",
                timeout.Token);
            await AssertExactStateAsync(client, containerId, containerName, "running", timeout.Token);

            await ApplyAsync(
                session,
                containerName,
                DockerContainerAction.Pause,
                "running",
                timeout.Token);
            await AssertExactStateAsync(client, containerId, containerName, "paused", timeout.Token);

            await ApplyAsync(
                session,
                containerName,
                DockerContainerAction.Resume,
                "paused",
                timeout.Token);
            await AssertExactStateAsync(client, containerId, containerName, "running", timeout.Token);

            await ApplyAsync(
                session,
                containerName,
                DockerContainerAction.Stop,
                "running",
                timeout.Token);
            await AssertExactStateAsync(client, containerId, containerName, "exited", timeout.Token);

            await ApplyAsync(
                session,
                containerName,
                DockerContainerAction.Remove,
                "exited",
                timeout.Token);
            var afterRemove = await ReadSnapshotAsync(client, timeout.Token);
            Assert.DoesNotContain(afterRemove.Containers, container =>
                string.Equals(container.Id, containerId, StringComparison.Ordinal)
                || string.Equals(container.Name, containerName, StringComparison.Ordinal));
            containerId = null;
        }
        finally
        {
            // This test owns only its cryptographically random name/returned ID.
            // Force and anonymous-volume removal are cleanup, never agent input.
            _ = await ExecuteDockerAsync(
                executor,
                [
                    "container",
                    "rm",
                    "--force",
                    "--volumes",
                    containerId ?? containerName,
                ],
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task RemoteSnapshotAndVolumeUsageCanBeReadThroughTheProductionCommandRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ASURA_RUN_DOCKER_SSH_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var host = Environment.GetEnvironmentVariable("ASURA_DOCKER_SSH_HOST")
            ?? throw new InvalidOperationException("ASURA_DOCKER_SSH_HOST is required.");
        var username = Environment.GetEnvironmentVariable("ASURA_DOCKER_SSH_USERNAME")
            ?? throw new InvalidOperationException("ASURA_DOCKER_SSH_USERNAME is required.");
        var profile = new ConnectionProfile(
            new ConnectionId("docker-ssh-smoke"),
            ConnectionProfile.CurrentSchemaVersion,
            "Docker SSH smoke",
            new ConnectionEndpoint.Ssh(host, username: username),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var executor = new ConnectionCommandExecutor(
            new SshRuntime(profile),
            new PathConnectionExecutableLocator());
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var snapshotResult = await client.ReadSnapshotAsync(profile, CancellationToken.None);
        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(
            snapshotResult).Value;
        Assert.NotEmpty(snapshot.Containers);
        Assert.NotEmpty(snapshot.Images);
        Assert.NotEmpty(snapshot.Volumes);
        Assert.NotEmpty(snapshot.Networks);

        var usageResult = await client.ReadVolumeUsageAsync(profile, CancellationToken.None);
        var usage = Assert.IsType<DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success>(
            usageResult).Value;
        Assert.NotEmpty(usage);
        Assert.Contains(usage, item => item.SizeBytes is > 0);
    }

    private static async ValueTask ApplyAsync(
        IDockerPanelSession session,
        string containerName,
        DockerContainerAction action,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var snapshot = Assert.IsType<DockerResult<DockerPanelSnapshot>.Success>(
            await session.ReadStateAsync(100, cancellationToken)).Value;
        var container = Assert.Single(snapshot.Containers, item =>
            string.Equals(item.Resource.DisplayName, containerName, StringComparison.Ordinal));
        Assert.Equal(expectedState, container.State, ignoreCase: true);
        var revision = Assert.IsType<DockerContainerRevision>(container.ControlRevision);

        var result = await session.ControlContainerAsync(
            new DockerContainerControlRequest(
                container.Resource.Reference,
                session.State.EngineGeneration,
                revision,
                action,
                expectedState),
            cancellationToken);

        Assert.Equal(DockerContainerControlOutcome.Applied, result.Outcome);
        Assert.Equal("docker_container_control_applied", result.StableCode);
        Assert.False(result.Retryable);
    }

    private static async ValueTask AssertExactStateAsync(
        DockerEngineClient client,
        string containerId,
        string containerName,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var snapshot = await ReadSnapshotAsync(client, cancellationToken);
            var matches = snapshot.Containers.Where(container =>
                string.Equals(container.Id, containerId, StringComparison.Ordinal)
                || string.Equals(container.Name, containerName, StringComparison.Ordinal)).ToArray();
            var container = Assert.Single(matches);
            Assert.Equal(containerId, container.Id);
            Assert.Equal(containerName, container.Name);
            if (string.Equals(container.State, expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                $"Container remained in state '{container.State}' instead of '{expectedState}'.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static async ValueTask<DockerEngineSnapshot> ReadSnapshotAsync(
        DockerEngineClient client,
        CancellationToken cancellationToken) =>
        Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(
            await client.ReadSnapshotAsync(
                BuiltInConnections.Local,
                cancellationToken)).Value;

    private static ValueTask<ConnectionCommandResult> ExecuteDockerAsync(
        IConnectionCommandExecutor executor,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        executor.ExecuteAsync(
            new ConnectionCommand(
                BuiltInConnections.Local,
                "docker",
                arguments,
                TimeSpan.FromSeconds(30),
                64 * 1_024),
            cancellationToken);

    [Fact]
    public async Task VolumeUsageCanBeReadFromTheProductionCommandRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ASURA_RUN_DOCKER_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var executor = new ConnectionCommandExecutor(
            new LocalRuntime(),
            new PathConnectionExecutableLocator());
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadVolumeUsageAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var usage = Assert.IsType<DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success>(
            result).Value;
        Assert.NotEmpty(usage);
        Assert.All(usage, item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
        Assert.Contains(usage, item => item.SizeBytes is > 0);
    }

    [Fact]
    public async Task SelectedContainerRootCanBeBrowsedThroughTheProductionCommandRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ASURA_RUN_DOCKER_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var containerName = Environment.GetEnvironmentVariable(
            "ASURA_DOCKER_SMOKE_CONTAINER") ?? "dosvit-grafana-1";
        var executor = new ConnectionCommandExecutor(
            new LocalRuntime(),
            new PathConnectionExecutableLocator());
        var client = new DockerEngineClient(executor, TimeProvider.System);
        var snapshotResult = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);
        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(
            snapshotResult).Value;
        var container = Assert.Single(snapshot.Containers, item => string.Equals(item.Name, containerName, StringComparison.Ordinal));

        var result = await client.ListFilesAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(
                DockerResourceKind.Container,
                container.Id,
                container.Name),
            "/",
            CancellationToken.None);

        var listing = Assert.IsType<DockerResult<DockerFileListing>.Success>(result).Value;
        Assert.Equal("/", listing.Path);
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, ".dockerenv", StringComparison.Ordinal));
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, "etc", StringComparison.Ordinal) && entry.Kind == DockerFileKind.Directory);
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, "usr", StringComparison.Ordinal) && entry.Kind == DockerFileKind.Directory);
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, "var", StringComparison.Ordinal) && entry.Kind == DockerFileKind.Directory);

        var etcResult = await client.ListFilesAsync(
            BuiltInConnections.Local,
            listing.Resource,
            "/etc",
            CancellationToken.None);
        var etc = Assert.IsType<DockerResult<DockerFileListing>.Success>(etcResult).Value;
        Assert.Contains(etc.Entries, entry => string.Equals(entry.Name, "passwd", StringComparison.Ordinal));

        var statResult = await client.StatFileAsync(
            BuiltInConnections.Local,
            listing.Resource,
            "/etc/passwd",
            CancellationToken.None);
        var stat = Assert.IsType<DockerResult<DockerFileEntry>.Success>(statResult).Value;
        Assert.Equal("passwd", stat.Name);
        Assert.Equal(DockerFileKind.File, stat.Kind);
        Assert.True(stat.Size > 0);

        var previewResult = await client.ReadFileAsync(
            BuiltInConnections.Local,
            listing.Resource,
            "/etc/passwd",
            256 * 1024,
            CancellationToken.None);
        var preview = Assert.IsType<DockerResult<DockerFileContent>.Success>(
            previewResult).Value;
        Assert.False(preview.Content.IsEmpty);
        Assert.Contains("root:", System.Text.Encoding.UTF8.GetString(preview.Content.Span));
    }

    [Fact]
    public async Task SelectedContainerLogsAcceptOneTimestampCursorThroughProductionRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ASURA_RUN_DOCKER_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var containerName = Environment.GetEnvironmentVariable(
            "ASURA_DOCKER_SMOKE_CONTAINER") ?? "dosvit-grafana-1";
        var client = new DockerEngineClient(
            new ConnectionCommandExecutor(
                new LocalRuntime(),
                new PathConnectionExecutableLocator()),
            TimeProvider.System);
        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(
            await client.ReadSnapshotAsync(
                BuiltInConnections.Local,
                CancellationToken.None)).Value;
        var container = Assert.Single(snapshot.Containers, item => string.Equals(item.Name, containerName, StringComparison.Ordinal));

        var result = await client.ReadContainerLogsAsync(
            BuiltInConnections.Local,
            new DockerContainerLogRequest(
                container.Id,
                Limit: 30,
                SinceTimestamp: DateTimeOffset.UtcNow
                    .AddHours(-1)
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            CancellationToken.None);

        var page = Assert.IsType<DockerResult<DockerContainerLogPage>.Success>(result).Value;
        Assert.InRange(page.Lines.Count, 0, 30);
    }

    private sealed class LocalRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(null),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SshRuntime(ConnectionProfile profile) : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile requestedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(profile.Id, requestedProfile.Id);
            var endpoint = Assert.IsType<ConnectionEndpoint.Ssh>(profile.Endpoint);
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Ssh,
                    new TerminalLaunchRequest(
                        null,
                        "ssh",
                        [
                            "-p", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "-o", "StrictHostKeyChecking=yes",
                            "-o", "AddKeysToAgent=yes",
                            "-tt",
                            "-l", endpoint.Username!,
                            "--", endpoint.Host,
                        ]),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.Strict,
                    ConnectionReconnectMode.BoundedBackoff)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile requestedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
