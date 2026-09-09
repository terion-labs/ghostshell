using System.Formats.Tar;
using System.Text;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.Docker.Tests;

public sealed class DockerEngineClientTests
{
    [Fact]
    public async Task SnapshotReadsEveryResourceFamilyAndJoinsContainerStatistics()
    {
        var executor = SuccessfulExecutor();
        var client = new DockerEngineClient(
            executor,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));

        var result = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(result).Value;
        Assert.Equal("28.3.0", snapshot.Engine.Version);
        var container = Assert.Single(snapshot.Containers);
        Assert.Equal("api", container.Name);
        Assert.Equal("2.5%", container.Cpu);
        Assert.Equal("128MiB / 1GiB", container.Memory);
        Assert.True(container.IsRunning);
        Assert.Equal("asura", container.ComposeProject);
        Assert.Equal("api", container.ComposeService);
        Assert.Equal("demo/api:latest", Assert.Single(snapshot.Images).DisplayName());
        Assert.Equal("app-data", Assert.Single(snapshot.Volumes).Name);
        Assert.Equal("app-network", Assert.Single(snapshot.Networks).Name);
        Assert.All(executor.Requests, request => Assert.Equal("docker", request.Executable));
        Assert.Equal(
            [
                "volume", "ls", "--format",
                "{{json .Name}}\t{{json .Driver}}\t{{json .Scope}}\t{{json .Mountpoint}}",
            ],
            Assert.Single(executor.Requests, request =>
                request.Arguments is ["volume", "ls", ..]).Arguments);
    }

    [Fact]
    public async Task SnapshotKeepsWorkingWhenStatisticsAreUnavailable()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["stats"] = new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            1,
            string.Empty,
            "stats are unavailable");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(result).Value;
        Assert.Equal("—", Assert.Single(snapshot.Containers).Cpu);
    }

    [Fact]
    public async Task SnapshotCarriesTheSshProfileThroughTheCommandBoundary()
    {
        var remote = new ConnectionProfile(
            new ConnectionId("remote-docker"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote Docker",
            new ConnectionEndpoint.Ssh("docker.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var executor = SuccessfulExecutor();
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadSnapshotAsync(remote, CancellationToken.None);

        Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(result);
        Assert.NotEmpty(executor.Requests);
        Assert.All(executor.Requests, request => Assert.Same(remote, request.Connection));
    }

    [Fact]
    public async Task VolumeUsageParsesDockerSizesAndCarriesTheConnectionProfile()
    {
        var remote = new ConnectionProfile(
            new ConnectionId("remote-volume-usage"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote Docker",
            new ConnectionEndpoint.Ssh("docker.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var executor = SuccessfulExecutor();
        executor.Responses["system df"] = Exited(
            """
            "large"	"1.5GB"
            "small"	"3.788kB"
            "binary"	"2MiB"
            "unknown"	"N/A"
            """);
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadVolumeUsageAsync(remote, CancellationToken.None);

        var usage = Assert.IsType<DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success>(result).Value;
        Assert.Collection(
            usage,
            item => Assert.Equal(("large", "1.5GB", 1_500_000_000L), (item.Name, item.Size, item.SizeBytes)),
            item => Assert.Equal(("small", "3.788kB", 3_788L), (item.Name, item.Size, item.SizeBytes)),
            item => Assert.Equal(("binary", "2MiB", 2_097_152L), (item.Name, item.Size, item.SizeBytes)),
            item =>
            {
                Assert.Equal("unknown", item.Name);
                Assert.Null(item.SizeBytes);
            });
        var request = Assert.Single(executor.Requests, request =>
            request.Arguments is ["system", "df", ..]);
        Assert.Same(remote, request.Connection);
        Assert.Equal(
            [
                "system", "df", "--verbose", "--format",
                "{{range .Volumes}}{{json .Name}}\t{{json .Size}}{{println}}{{end}}",
            ],
            request.Arguments);
    }

    [Fact]
    public async Task InspectReturnsCuratedPropertiesAndPrettyJson()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["container inspect"] = Exited(
            """
            [{"Id":"abc","Created":"2026-08-10T10:00:00Z","State":{"Status":"running","Running":true},"Config":{"Image":"demo/api:latest","WorkingDir":"/app"},"HostConfig":{"NetworkMode":"bridge"},"NetworkSettings":{"IPAddress":"172.17.0.2"}}]
            """);
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.InspectAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(DockerResourceKind.Container, "abc", "api"),
            CancellationToken.None);

        var inspection = Assert.IsType<DockerResult<DockerResourceInspection>.Success>(result).Value;
        Assert.Contains(inspection.Properties, property => string.Equals(property.Name, "State.Status", StringComparison.Ordinal) && string.Equals(property.Value, "running", StringComparison.Ordinal));
        Assert.Contains("\n  \"Id\": \"abc\"", inspection.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectCompactsArrayPropertiesWithoutChangingThePrettyJsonDocument()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["image inspect"] = Exited(
            """
            [{
              "Id": "sha256:image",
              "Created": "2026-08-10T10:00:00Z",
              "Os": "linux",
              "Architecture": "arm64",
              "Size": 125891486,
              "Config": {
                "WorkingDir": "/app",
                "Entrypoint": [
                  "/usr/local/bin/docker-entrypoint.sh"
                ],
                "Cmd": [
                  "bun",
                  "server/index.mjs"
                ]
              }
            }]
            """);
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.InspectAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(DockerResourceKind.Image, "sha256:image", "demo:latest"),
            CancellationToken.None);

        var inspection = Assert.IsType<DockerResult<DockerResourceInspection>.Success>(result).Value;
        Assert.Contains(inspection.Properties, property => string.Equals(property.Name, "Config.Entrypoint"
, StringComparison.Ordinal) && string.Equals(property.Value, "[\"/usr/local/bin/docker-entrypoint.sh\"]", StringComparison.Ordinal));
        Assert.Contains(inspection.Properties, property => string.Equals(property.Name, "Config.Cmd"
, StringComparison.Ordinal) && string.Equals(property.Value, "[\"bun\",\"server/index.mjs\"]", StringComparison.Ordinal));
        Assert.Contains(
            "\n    \"Entrypoint\": [\n      \"/usr/local/bin/docker-entrypoint.sh\"\n    ]",
            inspection.Json,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandFailureSurfacesBoundedDockerErrorText()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["version"] = new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            1,
            string.Empty,
            "permission denied while trying to connect to the Docker daemon socket");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var error = Assert.IsType<DockerResult<DockerEngineSnapshot>.Failure>(result).Error;
        Assert.Equal(DockerErrorCode.CommandFailed, error.Code);
        Assert.Contains("permission denied", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Error: failed to find target executable docker in container asura-workspace")]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
    public async Task UnavailableRuntimeErrorsExplainHowToRecover(string standardError)
    {
        var executor = SuccessfulExecutor();
        executor.Responses["version"] = new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            1,
            string.Empty,
            standardError);
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var error = Assert.IsType<DockerResult<DockerEngineSnapshot>.Failure>(result).Error;
        Assert.Equal(DockerErrorCode.RuntimeUnavailable, error.Code);
        Assert.Equal(
            "Docker is not installed in this environment, or its daemon is not running. "
            + "Install or start Docker, then retry.",
            error.Message);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("target executable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructurallyInvalidJsonReturnsAnInvalidResponse()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["version"] = Exited("null");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var error = Assert.IsType<DockerResult<DockerEngineSnapshot>.Failure>(result).Error;
        Assert.Equal(DockerErrorCode.InvalidResponse, error.Code);
    }

    [Fact]
    public async Task TruncatedInventoryReturnsAnExplicitSizeFailure()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["volume ls"] = new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            0,
            "\"partial",
            OutputTruncated: true);
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var error = Assert.IsType<DockerResult<DockerEngineSnapshot>.Failure>(result).Error;
        Assert.Equal(DockerErrorCode.InvalidResponse, error.Code);
        Assert.Equal("Docker returned more than 16 MB of data.", error.Message);
    }

    [Fact]
    public void ContainerShellCommandQuotesContainerIdentity()
    {
        var command = DockerContainerShellCommand.Build(
            "api'; reboot; '",
            "/bin/sh'; touch /tmp/pwned; '");

        Assert.Equal(
            "docker exec --interactive --tty 'api'\"'\"'; reboot; '\"'\"'' "
            + "'/bin/sh'\"'\"'; touch /tmp/pwned; '\"'\"''",
            command);
    }

    [Fact]
    public async Task ContainerShellResolutionTriesCandidatesInOrder()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["exec /bin/sh"] = Exited(126, "", "missing /bin/sh");
        executor.Responses["exec /bin/bash"] = Exited(127, "", "missing /bin/bash");
        executor.Responses["exec /bin/ash"] = Exited(string.Empty);
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ResolveContainerShellAsync(
            BuiltInConnections.Local,
            "api",
            CancellationToken.None);

        Assert.Equal(
            "/bin/ash",
            Assert.IsType<DockerResult<string>.Success>(result).Value);
        Assert.Equal(
            ["/bin/sh", "/bin/bash", "/bin/ash"],
            executor.Requests
                .Where(request => request.Arguments is ["exec", ..])
                .Select(request => request.Arguments[2]), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ContainerShellResolutionReportsWhenNoCandidateExists()
    {
        var executor = SuccessfulExecutor();
        foreach (var shellPath in DockerContainerShellCommand.CandidatePaths)
        {
            executor.Responses[$"exec {shellPath}"] = Exited(126, "", "missing");
        }

        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ResolveContainerShellAsync(
            BuiltInConnections.Local,
            "api",
            CancellationToken.None);

        var error = Assert.IsType<DockerResult<string>.Failure>(result).Error;
        Assert.Equal(DockerErrorCode.ShellUnavailable, error.Code);
        Assert.All(
            DockerContainerShellCommand.CandidatePaths,
            candidate => Assert.Contains(candidate, error.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContainerLogsReturnLatestBoundedPageWithTimestampCursors()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["logs page"] = Exited(
            "2026-08-10T10:00:00.000000001Z first\n"
            + "2026-08-10T10:00:01.000000001Z second\n"
            + "2026-08-10T10:00:02.000000001Z third\n");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadContainerLogsAsync(
            BuiltInConnections.Local,
            new DockerContainerLogRequest("api", Limit: 2),
            CancellationToken.None);

        var page = Assert.IsType<DockerResult<DockerContainerLogPage>.Success>(result).Value;
        Assert.True(page.HasOlder);
        Assert.Equal(["second", "third"], page.Lines.Select(line => line.Message), StringComparer.Ordinal);
        Assert.Equal("2026-08-10T10:00:01.000000001Z", page.OldestTimestamp);
        Assert.Equal("2026-08-10T10:00:02.000000001Z", page.NewestTimestamp);
        var request = Assert.Single(executor.Requests, item => string.Equals(item.Executable, "/bin/sh", StringComparison.Ordinal));
        Assert.Contains("--tail", request.Arguments[1], StringComparison.Ordinal);
        Assert.Equal("3", request.Arguments[^1]);
    }

    [Fact]
    public async Task ContainerLogSearchRunsOnTargetAndMarksContextBlocks()
    {
        var remote = new ConnectionProfile(
            new ConnectionId("remote-log-search"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote logs",
            new ConnectionEndpoint.Ssh("docker.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var executor = SuccessfulExecutor();
        executor.Responses["logs search"] = Exited(
            "2026-08-10T10:00:00Z before\n"
            + "2026-08-10T10:00:01Z needle\n"
            + "--\n"
            + "2026-08-10T10:02:00Z needle again\n");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadContainerLogsAsync(
            remote,
            new DockerContainerLogRequest(
                "api",
                Limit: 50,
                SearchText: "needle",
                ContextLines: 3),
            CancellationToken.None);

        var page = Assert.IsType<DockerResult<DockerContainerLogPage>.Success>(result).Value;
        Assert.Equal(3, page.Lines.Count);
        Assert.False(page.Lines[0].StartsContextBlock);
        Assert.True(page.Lines[2].StartsContextBlock);
        var request = Assert.Single(executor.Requests, item => string.Equals(item.Executable, "/bin/sh", StringComparison.Ordinal));
        Assert.Same(remote, request.Connection);
        Assert.Contains("grep -F -i -C", request.Arguments[1], StringComparison.Ordinal);
        Assert.Contains("mktemp -d", request.Arguments[1], StringComparison.Ordinal);
        Assert.Contains("status_dir/status", request.Arguments[1], StringComparison.Ordinal);
        Assert.Contains("rmdir \"$status_dir\"", request.Arguments[1], StringComparison.Ordinal);
        Assert.DoesNotContain("$$.status", request.Arguments[1], StringComparison.Ordinal);
        Assert.Equal("needle", request.Arguments[^1]);
        Assert.Equal("3", request.Arguments[^2]);
    }

    [Fact]
    public async Task ContainerLogDownloadStreamsWithoutCapturingTheCompleteLog()
    {
        var executor = SuccessfulExecutor();
        executor.BinaryResponses["logs download"] = BinaryExited(
            Encoding.UTF8.GetBytes("one\ntwo\n"));
        var client = new DockerEngineClient(executor, TimeProvider.System);
        using var destination = new MemoryStream();

        var result = await client.DownloadContainerLogsAsync(
            BuiltInConnections.Local,
            "api",
            destination,
            CancellationToken.None);

        Assert.IsType<DockerResult<bool>.Success>(result);
        Assert.Equal("one\ntwo\n", Encoding.UTF8.GetString(destination.ToArray()));
        var request = Assert.Single(executor.BinaryRequests);
        Assert.Equal("/bin/sh", request.Executable);
        Assert.Contains("docker container logs", request.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerFileListingUsesAShallowExecAndDoesNotCopyTheFilesystem()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["file list /bin/sh"] = Exited(FileRows(
            ("etc", DockerFileKind.Directory, null, 1_786_320_000),
            ("read me\nnow.txt", DockerFileKind.File, 5, 1_786_320_001)));
        var client = new DockerEngineClient(executor, TimeProvider.System);
        var resource = new DockerResourceReference(
            DockerResourceKind.Container,
            "container-api",
            "api");

        var result = await client.ListFilesAsync(
            BuiltInConnections.Local,
            resource,
            "/",
            CancellationToken.None);

        var listing = Assert.IsType<DockerResult<DockerFileListing>.Success>(result).Value;
        Assert.Equal("/", listing.Path);
        Assert.Equal(["etc", "read me\nnow.txt"], listing.Entries.Select(entry => entry.Name), StringComparer.Ordinal);
        Assert.Equal(DockerFileKind.Directory, listing.Entries[0].Kind);
        Assert.Equal(5, listing.Entries[1].Size);
        var request = Assert.Single(executor.Requests, request =>
            request.Arguments.Contains("asura-file-protocol", StringComparer.Ordinal));
        Assert.Equal("exec", request.Arguments[0]);
        Assert.Equal("container-api", request.Arguments[1]);
        Assert.Equal("/bin/sh", request.Arguments[2]);
        Assert.Equal("/", request.Arguments[^1]);
        Assert.DoesNotContain(executor.Requests, request =>
            request.Arguments is ["container", "create", ..]);
        Assert.Empty(executor.BinaryRequests);
    }

    [Fact]
    public async Task ContainerFileReadCopiesOnlyTheSelectedFileAndBoundsThePreview()
    {
        var executor = SuccessfulExecutor();
        executor.BinaryResponses["container cp"] = BinaryExited(Archive(
            ("readme.txt", DockerFileKind.File, "hello")));
        var client = new DockerEngineClient(executor, TimeProvider.System);
        var resource = new DockerResourceReference(
            DockerResourceKind.Container,
            "container-api",
            "api");

        var result = await client.ReadFileAsync(
            BuiltInConnections.Local,
            resource,
            "/readme.txt",
            3,
            CancellationToken.None);

        var content = Assert.IsType<DockerResult<DockerFileContent>.Success>(result).Value;
        Assert.Equal("hel", Encoding.UTF8.GetString(content.Content.Span));
        Assert.True(content.IsTruncated);
        Assert.Equal(
            ["container", "cp", "container-api:/readme.txt", "-"],
            Assert.Single(executor.BinaryRequests).Arguments);
    }

    [Fact]
    public async Task ImageFileReadCopiesOnlyTheSelectedPathFromATemporaryContainer()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["container create"] = Exited("abcdef123456\n");
        executor.Responses["container rm"] = Exited("abcdef123456\n");
        executor.BinaryResponses["container cp"] = BinaryExited(Archive(
            ("config.json", DockerFileKind.File, "{}")));
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadFileAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(DockerResourceKind.Image, "sha256:image", "demo:latest"),
            "/etc/config.json",
            128,
            CancellationToken.None);

        Assert.IsType<DockerResult<DockerFileContent>.Success>(result);
        var create = Assert.Single(executor.Requests, request =>
            request.Arguments is ["container", "create", ..]);
        Assert.Equal("asura-file-copy", create.Arguments[^1]);
        Assert.Equal(
            ["container", "cp", "abcdef123456:/etc/config.json", "-"],
            Assert.Single(executor.BinaryRequests).Arguments);
        Assert.Contains(executor.Requests, request =>
            request.Arguments is ["container", "rm", "--force", "abcdef123456"]);
    }

    [Fact]
    public async Task VolumeFileReadUsesAReadOnlyMountAndCopiesOnlyTheSelectedPath()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["image ls quiet"] = Exited("sha256:helper\n");
        executor.Responses["container create"] = Exited("abcdef123456\n");
        executor.Responses["container rm"] = Exited("abcdef123456\n");
        executor.BinaryResponses["container cp"] = BinaryExited(Archive(
            ("data.db", DockerFileKind.File, "db")));
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadFileAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(DockerResourceKind.Volume, "app-data", "app-data"),
            "/nested/data.db",
            128,
            CancellationToken.None);

        Assert.IsType<DockerResult<DockerFileContent>.Success>(result);
        var create = Assert.Single(executor.Requests, request =>
            request.Arguments is ["container", "create", ..]);
        Assert.Contains(
            "type=volume,source=app-data,target=/asura-volume,readonly",
            create.Arguments, StringComparer.Ordinal);
        Assert.Equal(
            ["container", "cp", "abcdef123456:/asura-volume/nested/data.db", "-"],
            Assert.Single(executor.BinaryRequests).Arguments);
    }

    [Fact]
    public async Task ImageFileListingUsesAReadOnlyEphemeralExecWithoutCopyingTheFilesystem()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["file list /bin/sh"] = Exited(FileRows(
            ("app", DockerFileKind.Directory, null, 1_786_320_000)));
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ListFilesAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(DockerResourceKind.Image, "sha256:image", "demo:latest"),
            "/",
            CancellationToken.None);

        Assert.IsType<DockerResult<DockerFileListing>.Success>(result);
        var run = Assert.Single(executor.Requests);
        Assert.Equal("run", run.Arguments[0]);
        Assert.Contains("--rm", run.Arguments, StringComparer.Ordinal);
        Assert.Contains("--read-only", run.Arguments, StringComparer.Ordinal);
        Assert.Contains("sha256:image", run.Arguments, StringComparer.Ordinal);
        Assert.Empty(executor.BinaryRequests);
    }

    [Fact]
    public async Task VolumeFileListingUsesAReadOnlyEphemeralMountWithoutCopyingTheFilesystem()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["image ls quiet"] = Exited("sha256:helper\n");
        executor.Responses["file list /bin/sh"] = Exited(FileRows(
            ("data.db", DockerFileKind.File, 2, 1_786_320_000)));
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ListFilesAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(DockerResourceKind.Volume, "app-data", "app-data"),
            "/",
            CancellationToken.None);

        Assert.IsType<DockerResult<DockerFileListing>.Success>(result);
        var run = Assert.Single(executor.Requests, request =>
            request.Arguments is ["run", ..]);
        Assert.Contains(
            "type=volume,source=app-data,target=/asura-volume,readonly",
            run.Arguments, StringComparer.Ordinal);
        Assert.Equal("/asura-volume", run.Arguments[^1]);
        Assert.Empty(executor.BinaryRequests);
    }

    [Fact]
    public async Task ContainerFileStatProbesOnlyTheRequestedPath()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["file stat /bin/sh"] = Exited(FileRows(
            ("settings.json", DockerFileKind.File, 42, 1_786_320_000)));
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.StatFileAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(DockerResourceKind.Container, "container-api", "api"),
            "/etc/settings.json",
            CancellationToken.None);

        var entry = Assert.IsType<DockerResult<DockerFileEntry>.Success>(result).Value;
        Assert.Equal("/etc/settings.json", entry.Path);
        Assert.Equal(42, entry.Size);
        Assert.Equal("/etc/settings.json", Assert.Single(executor.Requests).Arguments[^1]);
        Assert.Empty(executor.BinaryRequests);
    }

    [Fact]
    public async Task FileProtocolCarriesTheSshProfileThroughExecAndSelectedFileCopy()
    {
        var remote = new ConnectionProfile(
            new ConnectionId("remote-docker-files"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote Docker Files",
            new ConnectionEndpoint.Ssh("docker.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var executor = SuccessfulExecutor();
        executor.Responses["file list /bin/sh"] = Exited(FileRows(
            ("readme.txt", DockerFileKind.File, 5, 1_786_320_000)));
        executor.BinaryResponses["container cp"] = BinaryExited(Archive(
            ("readme.txt", DockerFileKind.File, "hello")));
        var client = new DockerEngineClient(executor, TimeProvider.System);
        var resource = new DockerResourceReference(
            DockerResourceKind.Container,
            "container-api",
            "api");

        Assert.IsType<DockerResult<DockerFileListing>.Success>(await client.ListFilesAsync(
            remote,
            resource,
            "/",
            CancellationToken.None));
        Assert.IsType<DockerResult<DockerFileContent>.Success>(await client.ReadFileAsync(
            remote,
            resource,
            "/readme.txt",
            128,
            CancellationToken.None));

        Assert.All(executor.Requests, request => Assert.Same(remote, request.Connection));
        Assert.All(executor.BinaryRequests, request => Assert.Same(remote, request.Connection));
    }

    [Fact]
    public async Task RestartUsesDockerContainerRestartOnTheSelectedConnection()
    {
        var remote = new ConnectionProfile(
            new ConnectionId("remote-docker-restart"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote Docker Restart",
            new ConnectionEndpoint.Ssh("docker.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var executor = SuccessfulExecutor();
        executor.Responses["container restart"] = Exited("container-api");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.RunContainerActionAsync(
            remote,
            "container-api",
            DockerContainerAction.Restart,
            CancellationToken.None);

        Assert.IsType<DockerResult<bool>.Success>(result);
        var request = Assert.Single(executor.Requests);
        Assert.Same(remote, request.Connection);
        Assert.Equal(["container", "restart", "container-api"], request.Arguments);
    }

    [Fact]
    public async Task GovernedStopUsesFixedArgvAndDiscardsProviderOutput()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["container stop"] = Exited("private-provider-output");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.RunContainerMutationAsync(
            BuiltInConnections.Local,
            "sha256:exact-container-id",
            DockerContainerAction.Stop,
            CancellationToken.None);

        Assert.Equal(DockerContainerMutationOutcome.Applied, result.Outcome);
        Assert.Equal("docker_container_control_applied", result.StableCode);
        Assert.False(result.Retryable);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(
            ["container", "stop", "--time", "10", "sha256:exact-container-id"],
            request.Arguments);
        Assert.Equal(1_024, request.MaximumOutputCharacters);
    }

    [Fact]
    public async Task GovernedMutationDoesNotRetryAnUncertainOutcome()
    {
        var executor = SuccessfulExecutor();
        executor.Responses["container pause"] = new ConnectionCommandResult(
            ConnectionCommandOutcome.TimedOut,
            null,
            string.Empty,
            "Password=provider-secret");
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.RunContainerMutationAsync(
            BuiltInConnections.Local,
            "sha256:exact-container-id",
            DockerContainerAction.Pause,
            CancellationToken.None);

        Assert.Equal(DockerContainerMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal("docker_mutation_outcome_unknown", result.StableCode);
        Assert.False(result.Retryable);
        Assert.Single(executor.Requests);
        Assert.DoesNotContain("provider-secret", result.StableCode, StringComparison.Ordinal);
    }

    private static RoutingExecutor SuccessfulExecutor()
    {
        var executor = new RoutingExecutor();
        executor.Responses["version"] = Exited(
            "{\"Version\":\"28.3.0\",\"ApiVersion\":\"1.51\",\"Os\":\"linux\",\"Arch\":\"arm64\"}");
        executor.Responses["container ls"] = Exited(
            "{\"ID\":\"abc123\",\"Names\":\"api\",\"Image\":\"demo/api:latest\",\"State\":\"running\",\"Status\":\"Up 2 hours\",\"Ports\":\"0.0.0.0:8080->8080/tcp\",\"RunningFor\":\"2 hours ago\",\"Labels\":\"com.docker.compose.project=asura,com.docker.compose.service=api,other=value\"}\n");
        executor.Responses["image ls"] = Exited(
            "{\"ID\":\"sha256:def\",\"Repository\":\"demo/api\",\"Tag\":\"latest\",\"Size\":\"184MB\",\"CreatedSince\":\"2 days ago\"}\n");
        executor.Responses["volume ls"] = Exited(
            "\"app-data\"\t\"local\"\t\"local\"\t\"/var/lib/docker/volumes/app-data/_data\"\n");
        executor.Responses["network ls"] = Exited(
            "{\"ID\":\"net123\",\"Name\":\"app-network\",\"Driver\":\"bridge\",\"Scope\":\"local\",\"CreatedAt\":\"2026-08-10 10:00:00 +0000 UTC\"}\n");
        executor.Responses["stats"] = Exited(
            "{\"Container\":\"abc123\",\"Name\":\"api\",\"CPUPerc\":\"2.5%\",\"MemUsage\":\"128MiB / 1GiB\",\"NetIO\":\"1.2MB / 840kB\",\"BlockIO\":\"14MB / 2MB\"}\n");
        return executor;
    }

    private static ConnectionCommandResult Exited(string output) =>
        new(ConnectionCommandOutcome.Exited, 0, output);

    private static ConnectionCommandResult Exited(
        int exitCode,
        string output,
        string error) =>
        new(ConnectionCommandOutcome.Exited, exitCode, output, error);

    private static ConnectionBinaryCommandResult BinaryExited(byte[] output) =>
        new(ConnectionCommandOutcome.Exited, 0, output);

    private static byte[] Archive(
        params (string Path, DockerFileKind Kind, string? Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, kind, content) in entries)
            {
                var entry = new PaxTarEntry(
                    kind == DockerFileKind.Directory
                        ? TarEntryType.Directory
                        : TarEntryType.RegularFile,
                    path);
                if (content is not null)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                }

                writer.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    private static string FileRows(
        params (string Name, DockerFileKind Kind, long? Size, long ModifiedAt)[] entries)
    {
        var builder = new StringBuilder();
        foreach (var (name, kind, size, modifiedAt) in entries)
        {
            var kindCode = kind switch
            {
                DockerFileKind.File => "f",
                DockerFileKind.Directory => "d",
                DockerFileKind.Link => "l",
                DockerFileKind.Other => "o",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            builder.Append(name).Append('\0')
                .Append(kindCode).Append('\0')
                .Append(size?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(modifiedAt.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\0');
        }

        return builder.ToString();
    }

    private sealed class RoutingExecutor : IConnectionCommandExecutor
    {
        public Dictionary<string, ConnectionCommandResult> Responses { get; } =
            new(StringComparer.Ordinal);

        public List<ConnectionCommand> Requests { get; } = [];

        public Dictionary<string, ConnectionBinaryCommandResult> BinaryResponses { get; } =
            new(StringComparer.Ordinal);

        public List<ConnectionBinaryCommand> BinaryRequests { get; } = [];

        public ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests)
            {
                Requests.Add(request);
            }

            var key = request.Arguments switch
            {
                _ when string.Equals(request.Executable, "/bin/sh"
, StringComparison.Ordinal) && request.Arguments[1].Contains("grep -F -i -C", StringComparison.Ordinal) =>
                    "logs search",
                _ when string.Equals(request.Executable, "/bin/sh", StringComparison.Ordinal) => "logs page",
                ["version", ..] => "version",
                ["container", "ls", ..] => "container ls",
                ["container", "inspect", ..] => "container inspect",
                ["image", "inspect", ..] => "image inspect",
                ["image", "ls", "--quiet", ..] => "image ls quiet",
                ["image", "ls", ..] => "image ls",
                ["volume", "ls", ..] => "volume ls",
                ["system", "df", ..] => "system df",
                ["network", "ls", ..] => "network ls",
                ["stats", ..] => "stats",
                ["exec", _, var shellPath, "-c", "exit 0"] => $"exec {shellPath}",
                ["exec", _, ..] when request.Arguments.Contains("asura-file-protocol", StringComparer.Ordinal) =>
                    FileProtocolKey(request.Arguments),
                ["run", ..] when request.Arguments.Contains("asura-file-protocol", StringComparer.Ordinal) =>
                    FileProtocolKey(request.Arguments),
                ["container", "create", ..] => "container create",
                ["container", "rm", ..] => "container rm",
                ["container", "restart", ..] => "container restart",
                ["container", "start", ..] => "container start",
                ["container", "stop", ..] => "container stop",
                ["container", "pause", ..] => "container pause",
                ["container", "unpause", ..] => "container unpause",
                _ => throw new InvalidOperationException(
                    $"Unexpected Docker request: {string.Join(' ', request.Arguments)}"),
            };
            return ValueTask.FromResult(Responses[key]);
        }

        private static string FileProtocolKey(IReadOnlyList<string> arguments)
        {
            var shellIndex = string.Equals(arguments[0], "exec"
, StringComparison.Ordinal) ? 2
                : Array.IndexOf([.. arguments], "--entrypoint") + 1;
            var script = arguments.First(argument =>
                argument.Contains("emit_entry()", StringComparison.Ordinal));
            var operation = script.Contains("for entry in", StringComparison.Ordinal)
                ? "list"
                : "stat";
            return $"file {operation} {arguments[shellIndex]}";
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryRequests.Add(request);
            var key = request.Arguments switch
            {
                _ when string.Equals(request.Executable, "/bin/sh", StringComparison.Ordinal) => "logs download",
                ["container", "cp", ..] => "container cp",
                _ => throw new InvalidOperationException(
                    $"Unexpected binary Docker request: {string.Join(' ', request.Arguments)}"),
            };
            return ValueTask.FromResult(BinaryResponses[key]);
        }

        public async ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryRequests.Add(request);
            var key = request.Arguments switch
            {
                _ when string.Equals(request.Executable, "/bin/sh", StringComparison.Ordinal) => "logs download",
                ["container", "cp", ..] => "container cp",
                _ => throw new InvalidOperationException(
                    $"Unexpected streaming Docker request: {string.Join(' ', request.Arguments)}"),
            };
            var response = BinaryResponses[key];
            if (response.Outcome != ConnectionCommandOutcome.Exited || response.ExitCode != 0)
            {
                return new ConnectionStreamingCommandResult<T>(
                    response.Outcome,
                    response.ExitCode,
                    default,
                    response.StandardError);
            }

            using var stream = new MemoryStream(response.StandardOutput.ToArray(), writable: false);
            var value = await consumeOutput(stream, cancellationToken);
            return new ConnectionStreamingCommandResult<T>(
                response.Outcome,
                response.ExitCode,
                value,
                response.StandardError);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

internal static class DockerImageSummaryTestExtensions
{
    public static string DisplayName(this DockerImageSummary image) =>
        $"{image.Repository}:{image.Tag}";
}
