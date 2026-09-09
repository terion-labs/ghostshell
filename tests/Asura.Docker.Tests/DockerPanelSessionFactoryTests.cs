using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.Docker.Tests;

public sealed class DockerPanelSessionFactoryTests
{
    private const string ContainerId = "sha256:container-internal-id";
    private const string ImageId = "sha256:image-internal-id";
    private const string NetworkId = "network-internal-id";

    [Fact]
    public async Task SessionUsesOpaqueResourcesAndProjectsEveryBoundedRead()
    {
        var client = new FakeDockerEngineClient();
        var factory = new DockerPanelSessionFactory(client, TimeProvider.System);
        var target = new DockerSessionTarget(BuiltInConnections.Local, 7);
        await using var session = await factory.CreateAsync(
            new SessionId("docker-session"),
            target,
            CancellationToken.None);

        Assert.Equal(PanelKind.Docker, session.Kind);
        Assert.Equal(target.Binding, session.Binding);
        Assert.True(session.Capabilities.Contains(SessionCapabilities.DockerReadState));
        Assert.True(session.Capabilities.Contains(SessionCapabilities.DockerInspect));
        Assert.True(session.Capabilities.Contains(SessionCapabilities.DockerReadLogs));
        Assert.True(session.Capabilities.Contains(SessionCapabilities.DockerFilesList));
        Assert.True(session.Capabilities.Contains(SessionCapabilities.DockerFilesStat));
        Assert.True(session.Capabilities.Contains(SessionCapabilities.DockerFilesRead));

        var state = Success(await session.ReadStateAsync(1, CancellationToken.None));
        var container = Assert.Single(state.Containers);
        var image = Assert.Single(state.Images);
        var volume = Assert.Single(state.Volumes);
        var network = Assert.Single(state.Networks);
        Assert.True(state.IsTruncated);
        AssertOpaque(container.Resource.Reference, ContainerId);
        AssertOpaque(image.Resource.Reference, ImageId);
        AssertOpaque(volume.Resource.Reference, "app-data");
        AssertOpaque(network.Resource.Reference, NetworkId);
        Assert.DoesNotContain(ContainerId, JsonSerializer.Serialize(state), StringComparison.Ordinal);

        var refreshed = Success(await session.ReadStateAsync(1, CancellationToken.None));
        Assert.Equal(
            container.Resource.Reference,
            Assert.Single(refreshed.Containers).Resource.Reference);

        var inspection = Success(await session.InspectAsync(
            container.Resource.Reference,
            CancellationToken.None));
        Assert.Equal(ContainerId, client.LastInspectedResource?.Id);
        Assert.Equal(["Name", "Config.Image"], inspection.Properties.Select(item => item.Name), StringComparer.Ordinal);
        Assert.DoesNotContain("PASSWORD=needle", JsonSerializer.Serialize(inspection), StringComparison.Ordinal);
        Assert.DoesNotContain(ContainerId, JsonSerializer.Serialize(inspection), StringComparison.Ordinal);
        Assert.True(inspection.IsTruncated);

        var logs = Success(await session.ReadLogsAsync(
            new DockerLogReadRequest(container.Resource.Reference, Limit: 10),
            CancellationToken.None));
        Assert.Equal(ContainerId, client.LastLogRequest?.ContainerId);
        Assert.Equal("ready", Assert.Single(logs.Lines).Message);

        var files = Success(await session.ListFilesAsync(
            new DockerFileListRequest(container.Resource.Reference, "/srv", MaximumEntries: 1),
            CancellationToken.None));
        Assert.Equal("first.txt", Assert.Single(files.Entries).Name);
        Assert.True(files.IsTruncated);
        Assert.Equal(ContainerId, client.LastFileResource?.Id);

        var stat = Success(await session.StatFileAsync(
            new DockerFileStatRequest(container.Resource.Reference, "/srv/first.txt"),
            CancellationToken.None));
        Assert.Equal(DockerFileKind.File, stat.Kind);

        var content = Success(await session.ReadFileAsync(
            new DockerFileReadRequest(container.Resource.Reference, "/srv/first.txt", 128),
            CancellationToken.None));
        Assert.Equal("hello", Encoding.UTF8.GetString(content.Content.Span));

        var unknown = await session.InspectAsync(
            new DockerResourceReferenceId("opaque_but_unknown"),
            CancellationToken.None);
        Assert.Equal(
            "The Docker resource reference is unknown or expired.",
            Assert.IsType<DockerResult<DockerInspectionSnapshot>.Failure>(unknown).Error.Message);
        var fileCalls = client.FileOperationCount;
        _ = Assert.IsType<DockerResult<DockerFilePage>.Failure>(
            await session.ListFilesAsync(
                new DockerFileListRequest(network.Resource.Reference, "/", 10),
                CancellationToken.None));
        Assert.Equal(fileCalls, client.FileOperationCount);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.ReadFileAsync(
                    new DockerFileReadRequest(container.Resource.Reference, "/srv/../secret", 128),
                    CancellationToken.None)
                .AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.ReadStateAsync(501, CancellationToken.None).AsTask());
        Assert.Equal(0, client.MutationOperationCount);
    }

    [Fact]
    public async Task LocalControlConsumesOneShotRevisionAndRevalidatesState()
    {
        var client = new FakeDockerEngineClient { SupportsMutation = true };
        var factory = new DockerPanelSessionFactory(client, TimeProvider.System);
        await using var session = await factory.CreateAsync(
            new SessionId("docker-control"),
            new DockerSessionTarget(BuiltInConnections.Local, 1),
            CancellationToken.None);
        Assert.True(session.Capabilities.Contains(SessionCapabilities.DockerContainerPause));
        var state = Success(await session.ReadStateAsync(10, CancellationToken.None));
        var container = Assert.Single(state.Containers, item =>
            string.Equals(item.Resource.DisplayName, "api", StringComparison.Ordinal));
        var revision = Assert.IsType<DockerContainerRevision>(container.ControlRevision);
        var request = new DockerContainerControlRequest(
            container.Resource.Reference,
            session.State.EngineGeneration,
            revision,
            DockerContainerAction.Pause,
            "running");

        var applied = await session.ControlContainerAsync(request, CancellationToken.None);
        var replay = await session.ControlContainerAsync(request, CancellationToken.None);

        Assert.Equal(DockerContainerControlOutcome.Applied, applied.Outcome);
        Assert.Equal(DockerContainerControlOutcome.NotDispatched, replay.Outcome);
        Assert.Equal("docker_container_precondition_expired", replay.StableCode);
        Assert.False(replay.Retryable);
        Assert.Equal(1, client.MutationOperationCount);
        Assert.Equal(ContainerId, client.LastMutationContainerId);
        Assert.Equal(DockerContainerAction.Pause, client.LastMutationAction);
    }

    [Fact]
    public async Task RemoteDockerSessionDoesNotAdvertiseAgentControl()
    {
        var client = new FakeDockerEngineClient { SupportsMutation = true };
        var factory = new DockerPanelSessionFactory(client, TimeProvider.System);
        var remote = new ConnectionProfile(
            new ConnectionId("remote-docker"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote Docker",
            new ConnectionEndpoint.Ssh("docker.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        await using var session = await factory.CreateAsync(
            new SessionId("docker-remote"),
            new DockerSessionTarget(remote, 1),
            CancellationToken.None);

        Assert.DoesNotContain(
            session.Capabilities.Values,
            capability => capability.StartsWith("docker.container.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderFailuresAreReducedToFixedSecretFreeErrors()
    {
        var client = new FakeDockerEngineClient();
        var factory = new DockerPanelSessionFactory(client, TimeProvider.System);
        await using var session = await factory.CreateAsync(
            new SessionId("docker-session"),
            new DockerSessionTarget(BuiltInConnections.Local, 1),
            CancellationToken.None);
        var state = Success(await session.ReadStateAsync(10, CancellationToken.None));
        client.InspectFailure = new DockerError(
            DockerErrorCode.ConnectionFailed,
            "tcp://private.internal:2376 Password=needle",
            Retryable: false);

        var result = await session.InspectAsync(
            state.Containers[0].Resource.Reference,
            CancellationToken.None);

        var failure = Assert.IsType<DockerResult<DockerInspectionSnapshot>.Failure>(result);
        Assert.Equal("The Docker connection failed.", failure.Error.Message);
        Assert.DoesNotContain("private.internal", failure.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("needle", failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoryRejectsAnUnavailableEngineWithoutProviderDetails()
    {
        var client = new FakeDockerEngineClient
        {
            SnapshotFailure = new DockerError(
                DockerErrorCode.ConnectionFailed,
                "ssh://private.internal Password=needle",
                Retryable: false),
        };
        var factory = new DockerPanelSessionFactory(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateAsync(
                    new SessionId("docker-session"),
                    new DockerSessionTarget(BuiltInConnections.Local, 1),
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("The Docker engine could not be opened.", error.Message);
        Assert.DoesNotContain("private.internal", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("needle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostileInspectAndLogOutputIsAllowlistedAndUtf8Budgeted()
    {
        var client = new FakeDockerEngineClient();
        var factory = new DockerPanelSessionFactory(client, TimeProvider.System);
        await using var session = await factory.CreateAsync(
            new SessionId("docker-hostile"),
            new DockerSessionTarget(BuiltInConnections.Local, 1),
            CancellationToken.None);
        var state = Success(await session.ReadStateAsync(10, CancellationToken.None));
        var container = Assert.Single(state.Containers, item => string.Equals(item.Resource.DisplayName, "api", StringComparison.Ordinal));
        var raw = new DockerResourceReference(
            DockerResourceKind.Container,
            ContainerId,
            "api");
        client.InspectionOverride = new DockerResourceInspection(
            raw,
            [
                new DockerInspectionProperty("Config.Image", "safe/image:latest"),
                new DockerInspectionProperty("Config.Cmd", "--password=hunter2"),
                new DockerInspectionProperty("Config.Entrypoint", "token=abcdefghijklmnop"),
            ],
            "{\"password\":\"hunter2\"}");

        var inspection = Success(await session.InspectAsync(
            container.Resource.Reference,
            CancellationToken.None));

        Assert.Equal(["Config.Image"], inspection.Properties.Select(item => item.Name), StringComparer.Ordinal);
        Assert.True(inspection.IsTruncated);
        var inspectJson = JsonSerializer.Serialize(inspection);
        Assert.DoesNotContain("hunter2", inspectJson, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", inspectJson, StringComparison.Ordinal);

        client.LogsOverride = new DockerContainerLogPage(
            [.. Enumerable.Range(0, 100)
                .Select(index => new DockerContainerLogLine(
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new string('x', 8_192)))],
            HasOlder: false,
            OldestTimestamp: null,
            NewestTimestamp: null);
        var logs = Success(await session.ReadLogsAsync(
            new DockerLogReadRequest(container.Resource.Reference, Limit: 100),
            CancellationToken.None));
        Assert.True(logs.Lines.Sum(line => Encoding.UTF8.GetByteCount(line.Message))
            <= 48 * 1_024);
        Assert.True(logs.Lines.Count < 100);
        Assert.True(logs.HasOlder);

        client.LogsOverride = new DockerContainerLogPage(
            [new DockerContainerLogLine(
                "now",
                string.Concat(Enumerable.Repeat("🙂", 2_048)))],
            HasOlder: false,
            OldestTimestamp: null,
            NewestTimestamp: null);
        var unicode = Success(await session.ReadLogsAsync(
            new DockerLogReadRequest(container.Resource.Reference, Limit: 1),
            CancellationToken.None));
        Assert.DoesNotContain('\uFFFD', Assert.Single(unicode.Lines).Message);

        client.LogsOverride = new DockerContainerLogPage(
            [new DockerContainerLogLine("now", new string(['b', 'a', 'd', '\uD800']))],
            HasOlder: false,
            OldestTimestamp: null,
            NewestTimestamp: null);
        var invalid = await session.ReadLogsAsync(
            new DockerLogReadRequest(container.Resource.Reference, Limit: 1),
            CancellationToken.None);
        Assert.IsType<DockerResult<DockerContainerLogPage>.Failure>(invalid);
    }

    private static T Success<T>(DockerResult<T> result) =>
        Assert.IsType<DockerResult<T>.Success>(result).Value;

    private static void AssertOpaque(
        DockerResourceReferenceId reference,
        string rawIdentity)
    {
        Assert.DoesNotContain(rawIdentity, reference.Value, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(reference.Value.Length, 16, 128);
    }

    private sealed class FakeDockerEngineClient : IDockerEngineClient
    {
        private static readonly DockerEngineSnapshot Snapshot = new(
            new DockerEngineSummary("28.3.0", "Linux", "amd64", "1.51"),
            [
                new DockerContainerSummary(
                    ContainerId,
                    "api",
                    "registry.example/api:latest",
                    "running",
                    "Up 5 minutes",
                    "443/tcp",
                    "5 minutes ago",
                    "1%",
                    "10MiB / 1GiB",
                    "1kB / 2kB",
                    "3kB / 4kB"),
                new DockerContainerSummary(
                    "second-container-id",
                    "worker",
                    "registry.example/worker:latest",
                    "running",
                    "Up 5 minutes",
                    string.Empty,
                    "5 minutes ago",
                    "1%",
                    "10MiB / 1GiB",
                    "1kB / 2kB",
                    "3kB / 4kB"),
            ],
            [new DockerImageSummary(ImageId, "registry.example/api", "latest", "25MB", "today")],
            [new DockerVolumeSummary("app-data", "local", "local", "/private/daemon/path")],
            [new DockerNetworkSummary(NetworkId, "app-net", "bridge", "local", "today")],
            DateTimeOffset.UnixEpoch);

        public DockerError? SnapshotFailure { get; init; }

        public bool SupportsMutation { get; init; }

        public bool SupportsContainerMutation => SupportsMutation;

        public DockerError? InspectFailure { get; set; }

        public DockerResourceInspection? InspectionOverride { get; set; }

        public DockerContainerLogPage? LogsOverride { get; set; }

        public DockerResourceReference? LastInspectedResource { get; private set; }

        public DockerContainerLogRequest? LastLogRequest { get; private set; }

        public DockerResourceReference? LastFileResource { get; private set; }

        public int FileOperationCount { get; private set; }

        public int MutationOperationCount { get; private set; }

        public string? LastMutationContainerId { get; private set; }

        public DockerContainerAction? LastMutationAction { get; private set; }

        public ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DockerResult<DockerEngineSnapshot>>(
                SnapshotFailure is { } failure
                    ? new DockerResult<DockerEngineSnapshot>.Failure(failure)
                    : new DockerResult<DockerEngineSnapshot>.Success(Snapshot));
        }

        public ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<IReadOnlyList<DockerVolumeUsage>>>(
                new DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success([]));

        public ValueTask<DockerResult<DockerResourceInspection>> InspectAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            CancellationToken cancellationToken)
        {
            LastInspectedResource = resource;
            if (InspectFailure is { } failure)
            {
                return ValueTask.FromResult<DockerResult<DockerResourceInspection>>(
                    new DockerResult<DockerResourceInspection>.Failure(failure));
            }

            return ValueTask.FromResult<DockerResult<DockerResourceInspection>>(
                new DockerResult<DockerResourceInspection>.Success(
                    InspectionOverride ?? new DockerResourceInspection(
                        resource,
                        [
                            new DockerInspectionProperty("Id", ContainerId),
                            new DockerInspectionProperty("Name", "api"),
                            new DockerInspectionProperty("Config.Image", "registry.example/api:latest"),
                            new DockerInspectionProperty("Config.Env", "PASSWORD=needle"),
                        ],
                        "{\"Config\":{\"Env\":[\"PASSWORD=needle\"]}}")));
        }

        public ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
            ConnectionProfile connection,
            DockerContainerLogRequest request,
            CancellationToken cancellationToken)
        {
            LastLogRequest = request;
            return ValueTask.FromResult<DockerResult<DockerContainerLogPage>>(
                new DockerResult<DockerContainerLogPage>.Success(
                    LogsOverride ?? new DockerContainerLogPage(
                        [new DockerContainerLogLine("2026-08-15T00:00:00Z", "ready")],
                        HasOlder: false,
                        OldestTimestamp: "2026-08-15T00:00:00Z",
                        NewestTimestamp: "2026-08-15T00:00:00Z")));
        }

        public ValueTask<DockerResult<bool>> DownloadContainerLogsAsync(
            ConnectionProfile connection,
            string containerId,
            Stream destination,
            CancellationToken cancellationToken)
        {
            MutationOperationCount++;
            return ValueTask.FromResult<DockerResult<bool>>(
                new DockerResult<bool>.Failure(new DockerError(
                    DockerErrorCode.CommandFailed,
                    "Not available through hosted reads.",
                    false)));
        }

        public ValueTask<DockerResult<string>> ResolveContainerShellAsync(
            ConnectionProfile connection,
            string containerId,
            CancellationToken cancellationToken)
        {
            MutationOperationCount++;
            return ValueTask.FromResult<DockerResult<string>>(
                new DockerResult<string>.Failure(new DockerError(
                    DockerErrorCode.ShellUnavailable,
                    "Not available through hosted reads.",
                    false)));
        }

        public ValueTask<DockerResult<DockerFileListing>> ListFilesAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            CancellationToken cancellationToken)
        {
            FileOperationCount++;
            LastFileResource = resource;
            return ValueTask.FromResult<DockerResult<DockerFileListing>>(
                new DockerResult<DockerFileListing>.Success(new DockerFileListing(
                    resource,
                    path,
                    [
                        new DockerFileEntry("first.txt", "/srv/first.txt", DockerFileKind.File, 5, null),
                        new DockerFileEntry("second.txt", "/srv/second.txt", DockerFileKind.File, 6, null),
                    ])));
        }

        public ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            CancellationToken cancellationToken)
        {
            FileOperationCount++;
            LastFileResource = resource;
            return ValueTask.FromResult<DockerResult<DockerFileEntry>>(
                new DockerResult<DockerFileEntry>.Success(
                    new DockerFileEntry("first.txt", path, DockerFileKind.File, 5, null)));
        }

        public ValueTask<DockerResult<DockerFileContent>> ReadFileAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            FileOperationCount++;
            LastFileResource = resource;
            return ValueTask.FromResult<DockerResult<DockerFileContent>>(
                new DockerResult<DockerFileContent>.Success(
                    new DockerFileContent(
                        resource,
                        path,
                        "hello"u8.ToArray(),
                        IsTruncated: false)));
        }

        public ValueTask<DockerResult<bool>> RunContainerActionAsync(
            ConnectionProfile connection,
            string containerId,
            DockerContainerAction action,
            CancellationToken cancellationToken)
        {
            MutationOperationCount++;
            return ValueTask.FromResult<DockerResult<bool>>(
                new DockerResult<bool>.Success(true));
        }

        public ValueTask<DockerContainerMutationResult> RunContainerMutationAsync(
            ConnectionProfile connection,
            string containerId,
            DockerContainerAction action,
            CancellationToken cancellationToken)
        {
            MutationOperationCount++;
            LastMutationContainerId = containerId;
            LastMutationAction = action;
            return ValueTask.FromResult(new DockerContainerMutationResult(
                DockerContainerMutationOutcome.Applied,
                "docker_container_control_applied",
                Retryable: false));
        }
    }
}
