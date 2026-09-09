using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Docker;

/// <summary>
/// Reads Docker's stable CLI JSON surface through Asura's connection command
/// boundary. Local and SSH targets therefore share authentication, host trust,
/// cancellation, output limits, and process teardown with the rest of the app.
/// </summary>
public sealed class DockerEngineClient(
    IConnectionCommandExecutor executor,
    TimeProvider timeProvider) : IDockerEngineClient
{
    private const string RuntimeUnavailableMessage =
        "Docker is not installed in this environment, or its daemon is not running. "
        + "Install or start Docker, then retry.";
    private const int ListOutputLimit = 16 * 1024 * 1024;
    private const int InspectOutputLimit = 2 * 1024 * 1024;
    private const int LogOutputLimit = 16 * 1024 * 1024;
    private const int VolumeUsageOutputLimit = 16 * 1024 * 1024;
    private const string VolumeBrowsePath = "/asura-volume";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LogReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogSearchTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FileReadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LogDownloadTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan VolumeUsageTimeout = TimeSpan.FromMinutes(1);

    public bool SupportsContainerMutation => true;

    public async ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Supports(connection))
        {
            return Unsupported<DockerEngineSnapshot>();
        }

        var version = await ExecuteAsync(
            connection,
            ["version", "--format", "{{json .Server}}"],
            ReadTimeout,
            ListOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (version is DockerResult<string>.Failure versionFailure)
        {
            return new DockerResult<DockerEngineSnapshot>.Failure(versionFailure.Error);
        }

        var containersTask = ExecuteAsync(
            connection,
            ["container", "ls", "--all", "--no-trunc", "--format", "{{json .}}"],
            ReadTimeout,
            ListOutputLimit,
            cancellationToken).AsTask();
        var imagesTask = ExecuteAsync(
            connection,
            ["image", "ls", "--all", "--no-trunc", "--format", "{{json .}}"],
            ReadTimeout,
            ListOutputLimit,
            cancellationToken).AsTask();
        var volumesTask = ExecuteAsync(
            connection,
            [
                "volume", "ls", "--format",
                "{{json .Name}}\t{{json .Driver}}\t{{json .Scope}}\t{{json .Mountpoint}}",
            ],
            ReadTimeout,
            ListOutputLimit,
            cancellationToken).AsTask();
        var networksTask = ExecuteAsync(
            connection,
            ["network", "ls", "--no-trunc", "--format", "{{json .}}"],
            ReadTimeout,
            ListOutputLimit,
            cancellationToken).AsTask();
        var statsTask = ExecuteAsync(
            connection,
            ["stats", "--all", "--no-stream", "--format", "{{json .}}"],
            ReadTimeout,
            ListOutputLimit,
            cancellationToken).AsTask();

        await Task.WhenAll(
            containersTask,
            imagesTask,
            volumesTask,
            networksTask,
            statsTask).ConfigureAwait(false);
        var containers = await containersTask.ConfigureAwait(false);
        var images = await imagesTask.ConfigureAwait(false);
        var volumes = await volumesTask.ConfigureAwait(false);
        var networks = await networksTask.ConfigureAwait(false);
        var statsResult = await statsTask.ConfigureAwait(false);

        var required = new[]
        {
            containers,
            images,
            volumes,
            networks,
        };
        if (required.OfType<DockerResult<string>.Failure>().FirstOrDefault() is { } failure)
        {
            return new DockerResult<DockerEngineSnapshot>.Failure(failure.Error);
        }

        try
        {
            var statistics = statsResult is DockerResult<string>.Success stats
                ? ParseStatistics(stats.Value)
                : new Dictionary<string, ContainerStatistics>(StringComparer.Ordinal);
            var snapshot = new DockerEngineSnapshot(
                ParseEngine(((DockerResult<string>.Success)version).Value),
                ParseContainers(
                    ((DockerResult<string>.Success)containers).Value,
                    statistics),
                ParseImages(((DockerResult<string>.Success)images).Value),
                ParseVolumes(((DockerResult<string>.Success)volumes).Value),
                ParseNetworks(((DockerResult<string>.Success)networks).Value),
                timeProvider.GetUtcNow());
            return new DockerResult<DockerEngineSnapshot>.Success(snapshot);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return InvalidResponse<DockerEngineSnapshot>();
        }
    }

    public async ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Supports(connection))
        {
            return Unsupported<IReadOnlyList<DockerVolumeUsage>>();
        }

        var result = await ExecuteAsync(
            connection,
            [
                "system", "df", "--verbose", "--format",
                "{{range .Volumes}}{{json .Name}}\t{{json .Size}}{{println}}{{end}}",
            ],
            VolumeUsageTimeout,
            VolumeUsageOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (result is DockerResult<string>.Failure failure)
        {
            return new DockerResult<IReadOnlyList<DockerVolumeUsage>>.Failure(failure.Error);
        }

        try
        {
            return new DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success(
                ParseVolumeUsage(((DockerResult<string>.Success)result).Value));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return InvalidResponse<IReadOnlyList<DockerVolumeUsage>>();
        }
    }

    public async ValueTask<DockerResult<DockerResourceInspection>> InspectAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(resource);
        if (!Supports(connection))
        {
            return Unsupported<DockerResourceInspection>();
        }

        var noun = resource.Kind switch
        {
            DockerResourceKind.Container => "container",
            DockerResourceKind.Image => "image",
            DockerResourceKind.Volume => "volume",
            DockerResourceKind.Network => "network",
            _ => throw new ArgumentOutOfRangeException(nameof(resource)),
        };
        var result = await ExecuteAsync(
            connection,
            [noun, "inspect", resource.Id],
            ReadTimeout,
            InspectOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (result is DockerResult<string>.Failure failure)
        {
            return new DockerResult<DockerResourceInspection>.Failure(failure.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(((DockerResult<string>.Success)result).Value);
            var root = document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.GetArrayLength() > 0
                    ? document.RootElement[0]
                    : throw new JsonException("Docker inspect returned no resource.");
            var json = FormatJson(root, indented: true);
            return new DockerResult<DockerResourceInspection>.Success(
                new DockerResourceInspection(
                    resource,
                    InspectionProperties(resource.Kind, root),
                    json));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return InvalidResponse<DockerResourceInspection>();
        }
    }

    public async ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
        ConnectionProfile connection,
        DockerContainerLogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContainerId);
        if (request.Limit is <= 0 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Log page size must be between 1 and 2,000 rows.");
        }

        if (request.ContextLines is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Log search context must be between 0 and 100 rows.");
        }

        if (request.BeforeTimestamp is not null && request.SinceTimestamp is not null)
        {
            throw new ArgumentException("A log request cannot page backward and forward at once.", nameof(request));
        }

        if (!Supports(connection))
        {
            return Unsupported<DockerContainerLogPage>();
        }

        var result = await ExecuteLogCommandAsync(connection, request, cancellationToken)
            .ConfigureAwait(false);
        if (result is DockerResult<string>.Failure failure)
        {
            return new DockerResult<DockerContainerLogPage>.Failure(failure.Error);
        }

        return new DockerResult<DockerContainerLogPage>.Success(ParseLogPage(
            ((DockerResult<string>.Success)result).Value,
            request.Limit));
    }

    public async ValueTask<DockerResult<bool>> DownloadContainerLogsAsync(
        ConnectionProfile connection,
        string containerId,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The log destination must be writable.", nameof(destination));
        }

        if (!Supports(connection))
        {
            return Unsupported<bool>();
        }

        var result = await executor.ExecuteStreamingAsync(
            new ConnectionBinaryCommand(
                connection,
                "/bin/sh",
                [
                    "-c",
                    "exec docker container logs --timestamps \"$1\" 2>&1",
                    "asura-docker-logs",
                    containerId,
                ],
                LogDownloadTimeout,
                64 * 1024 * 1024),
            async (source, token) =>
            {
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
                await destination.FlushAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return result.Outcome == ConnectionCommandOutcome.Exited && result.ExitCode == 0
            ? new DockerResult<bool>.Success(true)
            : new DockerResult<bool>.Failure(CommandError(result));
    }

    public async ValueTask<DockerResult<string>> ResolveContainerShellAsync(
        ConnectionProfile connection,
        string containerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (!Supports(connection))
        {
            return Unsupported<string>();
        }

        foreach (var shellPath in DockerContainerShellCommand.CandidatePaths)
        {
            var result = await executor.ExecuteAsync(
                new ConnectionCommand(
                    connection,
                    "docker",
                    ["exec", containerId, shellPath, "-c", "exit 0"],
                    ReadTimeout,
                    32 * 1024),
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome == ConnectionCommandOutcome.Exited && result.ExitCode == 0)
            {
                return new DockerResult<string>.Success(shellPath);
            }

            if (result.Outcome == ConnectionCommandOutcome.Exited
                && result.ExitCode is 126 or 127)
            {
                continue;
            }

            return new DockerResult<string>.Failure(CommandError(result));
        }

        return new DockerResult<string>.Failure(new DockerError(
            DockerErrorCode.ShellUnavailable,
            "The container has none of the supported interactive shells: "
            + string.Join(", ", DockerContainerShellCommand.CandidatePaths)
            + ".",
            false));
    }

    public async ValueTask<DockerResult<DockerFileListing>> ListFilesAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(resource);
        var normalizedPath = NormalizeFilePath(path);
        if (!Supports(connection))
        {
            return Unsupported<DockerFileListing>();
        }

        var output = await ExecuteFileScriptAsync(
            connection,
            resource,
            DockerFileExecProtocol.ListScript,
            normalizedPath,
            cancellationToken).ConfigureAwait(false);
        return output is DockerResult<string>.Failure failure
            ? new DockerResult<DockerFileListing>.Failure(failure.Error)
            : DockerFileExecProtocol.ParseListing(
                resource,
                normalizedPath,
                ((DockerResult<string>.Success)output).Value);
    }

    public async ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(resource);
        var normalizedPath = NormalizeFilePath(path);
        if (!Supports(connection))
        {
            return Unsupported<DockerFileEntry>();
        }

        var output = await ExecuteFileScriptAsync(
            connection,
            resource,
            DockerFileExecProtocol.StatScript,
            normalizedPath,
            cancellationToken).ConfigureAwait(false);
        return output is DockerResult<string>.Failure failure
            ? new DockerResult<DockerFileEntry>.Failure(failure.Error)
            : DockerFileExecProtocol.ParseStat(
                normalizedPath,
                ((DockerResult<string>.Success)output).Value);
    }

    public async ValueTask<DockerResult<DockerFileContent>> ReadFileAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var normalizedPath = NormalizeFilePath(path);
        if (!Supports(connection))
        {
            return Unsupported<DockerFileContent>();
        }

        var leaseResult = await AcquireFileContainerAsync(
            connection,
            resource,
            cancellationToken).ConfigureAwait(false);
        if (leaseResult is DockerResult<FileContainerLease>.Failure leaseFailure)
        {
            return new DockerResult<DockerFileContent>.Failure(leaseFailure.Error);
        }

        var lease = ((DockerResult<FileContainerLease>.Success)leaseResult).Value;
        try
        {
            var sourcePath = string.Equals(lease.RootPath, "/"
, StringComparison.Ordinal) ? normalizedPath
                : string.Equals(normalizedPath, "/"
, StringComparison.Ordinal) ? lease.RootPath
                    : $"{lease.RootPath}{normalizedPath}";
            ConnectionStreamingCommandResult<DockerFileContent?> copy;
            try
            {
                copy = await executor.ExecuteStreamingAsync(
                    new ConnectionBinaryCommand(
                        connection,
                        "docker",
                        ["container", "cp", $"{lease.ContainerId}:{sourcePath}", "-"],
                        FileReadTimeout,
                        64 * 1024),
                    (stream, token) => ValueTask.FromResult(
                        ParseFileContent(
                            stream,
                            resource,
                            normalizedPath,
                            maximumBytes,
                            token)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                return InvalidResponse<DockerFileContent>();
            }

            if (copy.Outcome != ConnectionCommandOutcome.Exited || copy.ExitCode != 0)
            {
                return new DockerResult<DockerFileContent>.Failure(CommandError(copy));
            }

            return copy.Value is { } content
                ? new DockerResult<DockerFileContent>.Success(content)
                : InvalidResponse<DockerFileContent>();
        }
        finally
        {
            if (lease.IsTemporary)
            {
                _ = await ExecuteAsync(
                    connection,
                    ["container", "rm", "--force", lease.ContainerId],
                    ActionTimeout,
                    64 * 1024,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<DockerResult<bool>> RunContainerActionAsync(
        ConnectionProfile connection,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (!Supports(connection))
        {
            return Unsupported<bool>();
        }

        var verb = action switch
        {
            DockerContainerAction.Start => "start",
            DockerContainerAction.Stop => "stop",
            DockerContainerAction.Restart => "restart",
            DockerContainerAction.Pause => "pause",
            DockerContainerAction.Resume => "unpause",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
        var result = await ExecuteAsync(
            connection,
            ["container", verb, containerId],
            ActionTimeout,
            64 * 1024,
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            DockerResult<string>.Success => new DockerResult<bool>.Success(true),
            DockerResult<string>.Failure failure =>
                new DockerResult<bool>.Failure(failure.Error),
            _ => throw new InvalidOperationException("Unknown Docker command result."),
        };
    }

    public async ValueTask<DockerContainerMutationResult> RunContainerMutationAsync(
        ConnectionProfile connection,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (!Supports(connection) || cancellationToken.IsCancellationRequested)
        {
            return new DockerContainerMutationResult(
                DockerContainerMutationOutcome.NotDispatched,
                "docker_container_control_unavailable",
                Retryable: false);
        }

        IReadOnlyList<string> arguments = action switch
        {
            DockerContainerAction.Start => ["container", "start", containerId],
            DockerContainerAction.Stop =>
                ["container", "stop", "--time", "10", containerId],
            DockerContainerAction.Restart =>
                ["container", "restart", "--time", "10", containerId],
            DockerContainerAction.Pause => ["container", "pause", containerId],
            DockerContainerAction.Resume => ["container", "unpause", containerId],
            DockerContainerAction.Remove => ["container", "rm", containerId],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

        ConnectionCommandResult result;
        try
        {
            result = await executor.ExecuteAsync(
                    new ConnectionCommand(
                        connection,
                        "docker",
                        arguments,
                        ActionTimeout,
                        1_024),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return UnknownMutationOutcome();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return UnknownMutationOutcome();
        }

        if (result.Outcome == ConnectionCommandOutcome.Exited && result.ExitCode == 0)
        {
            return new DockerContainerMutationResult(
                DockerContainerMutationOutcome.Applied,
                "docker_container_control_applied",
                Retryable: false);
        }

        return result.Outcome == ConnectionCommandOutcome.StartFailed
            ? new DockerContainerMutationResult(
                DockerContainerMutationOutcome.NotDispatched,
                "docker_container_control_not_dispatched",
                Retryable: false)
            : UnknownMutationOutcome();
    }

    private static DockerContainerMutationResult UnknownMutationOutcome() => new(
        DockerContainerMutationOutcome.OutcomeUnknown,
        "docker_mutation_outcome_unknown",
        Retryable: false);

    private async ValueTask<DockerResult<string>> ExecuteLogCommandAsync(
        ConnectionProfile connection,
        DockerContainerLogRequest request,
        CancellationToken cancellationToken)
    {
        var mode = request.BeforeTimestamp is not null
            ? "before"
            : request.SinceTimestamp is not null
                ? "since"
                : "latest";
        var cursor = request.BeforeTimestamp ?? request.SinceTimestamp ?? string.Empty;
        var isSearch = !string.IsNullOrWhiteSpace(request.SearchText);
        var requestedRows = isSearch
            ? checked((request.Limit * 2) + 1)
            : request.Limit + 1;
        var script = isSearch
            ? """
              status_dir=$(mktemp -d "${TMPDIR:-/tmp}/asura-docker-logs.XXXXXX") || exit 1
              status_file="$status_dir/status"
              trap 'rm -f "$status_file"; rmdir "$status_dir"' EXIT HUP INT TERM
              {
                case "$1" in
                  before) docker container logs --timestamps --until "$3" "$2" 2>&1 ;;
                  since) docker container logs --timestamps --since "$3" "$2" 2>&1 ;;
                  *) docker container logs --timestamps "$2" 2>&1 ;;
                esac
                printf '%s' "$?" > "$status_file" || exit 1
              } | grep -F -i -C "$5" -- "$6" | tail -n "$4"
              docker_status=$(cat "$status_file" 2>/dev/null || printf '1')
              [ "$docker_status" -eq 0 ] || exit "$docker_status"
              """
            : """
              case "$1" in
                before) exec docker container logs --tail "$4" --timestamps --until "$3" "$2" 2>&1 ;;
                since) exec docker container logs --tail "$4" --timestamps --since "$3" "$2" 2>&1 ;;
                *) exec docker container logs --tail "$4" --timestamps "$2" 2>&1 ;;
              esac
              """;
        var arguments = isSearch
            ? new[]
            {
                "-c",
                script,
                "asura-docker-logs",
                mode,
                request.ContainerId,
                cursor,
                requestedRows.ToString(CultureInfo.InvariantCulture),
                request.ContextLines.ToString(CultureInfo.InvariantCulture),
                request.SearchText!.Trim(),
            }
            :
            [
                "-c",
                script,
                "asura-docker-logs",
                mode,
                request.ContainerId,
                cursor,
                requestedRows.ToString(CultureInfo.InvariantCulture),
            ];
        var result = await executor.ExecuteAsync(
            new ConnectionCommand(
                connection,
                "/bin/sh",
                arguments,
                isSearch ? LogSearchTimeout : LogReadTimeout,
                LogOutputLimit),
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ConnectionCommandOutcome.Exited && result.ExitCode == 0)
        {
            return result.OutputTruncated
                ? new DockerResult<string>.Failure(new DockerError(
                    DockerErrorCode.InvalidResponse,
                    "The selected log slice exceeds the 16 MB display limit. Narrow the search or download the complete log.",
                    true))
                : new DockerResult<string>.Success(result.StandardOutput);
        }

        return new DockerResult<string>.Failure(CommandErrorIncludingOutput(result));
    }

    private static DockerContainerLogPage ParseLogPage(string output, int limit)
    {
        var lines = new List<DockerContainerLogLine>();
        var startsContextBlock = false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (string.Equals(line, "--", StringComparison.Ordinal))
            {
                startsContextBlock = true;
                continue;
            }

            var separator = line.IndexOf(' ');
            var timestamp = separator > 0 && LooksLikeDockerTimestamp(line.AsSpan(0, separator))
                ? line[..separator]
                : string.Empty;
            var message = timestamp.Length == 0 ? line : line[(separator + 1)..];
            lines.Add(new DockerContainerLogLine(timestamp, message, startsContextBlock));
            startsContextBlock = false;
        }

        var hasOlder = lines.Count > limit;
        var visible = hasOlder
            ? lines.GetRange(lines.Count - limit, limit)
            : lines;
        return new DockerContainerLogPage(
            Array.AsReadOnly(visible.ToArray()),
            hasOlder,
            visible.FirstOrDefault(line => line.Timestamp.Length > 0)?.Timestamp,
            visible.LastOrDefault(line => line.Timestamp.Length > 0)?.Timestamp);
    }

    private static bool LooksLikeDockerTimestamp(ReadOnlySpan<char> value) =>
        value.Length >= 20
        && value[4] == '-'
        && value[7] == '-'
        && value[10] == 'T';

    private static DockerError CommandErrorIncludingOutput(ConnectionCommandResult result) =>
        CommandError(string.IsNullOrWhiteSpace(result.StandardError)
            && !string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result with { StandardError = result.StandardOutput }
                : result);

    private async ValueTask<DockerResult<string>> ExecuteAsync(
        ConnectionProfile connection,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new ConnectionCommand(
                connection,
                "docker",
                arguments,
                timeout,
                outputLimit),
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ConnectionCommandOutcome.Exited && result.ExitCode == 0)
        {
            if (result.OutputTruncated)
            {
                return new DockerResult<string>.Failure(new DockerError(
                    DockerErrorCode.InvalidResponse,
                    $"Docker returned more than {outputLimit / (1024 * 1024)} MB of data.",
                    true));
            }

            var output = string.IsNullOrEmpty(result.StandardOutput)
                ? result.StandardError
                : result.StandardOutput;
            return new DockerResult<string>.Success(output);
        }

        return new DockerResult<string>.Failure(CommandError(result));
    }

    private async ValueTask<DockerResult<string>> ExecuteFileScriptAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string script,
        string path,
        CancellationToken cancellationToken)
    {
        var targetsResult = await ResolveFileExecTargetsAsync(
            connection,
            resource,
            cancellationToken).ConfigureAwait(false);
        if (targetsResult is DockerResult<IReadOnlyList<FileExecTarget>>.Failure failure)
        {
            return new DockerResult<string>.Failure(failure.Error);
        }

        var targets = ((DockerResult<IReadOnlyList<FileExecTarget>>.Success)targetsResult).Value;
        foreach (var target in targets)
        {
            var effectivePath = string.Equals(target.RootPath, "/"
, StringComparison.Ordinal) ? path
                : string.Equals(path, "/"
, StringComparison.Ordinal) ? target.RootPath
                    : $"{target.RootPath}{path}";
            foreach (var shellPath in DockerFileExecProtocol.ShellPaths)
            {
                var result = await executor.ExecuteAsync(
                    new ConnectionCommand(
                        connection,
                        "docker",
                        FileExecArguments(target, shellPath, script, effectivePath),
                        ReadTimeout,
                        ListOutputLimit),
                    cancellationToken).ConfigureAwait(false);
                if (result.Outcome != ConnectionCommandOutcome.Exited)
                {
                    return new DockerResult<string>.Failure(CommandError(result));
                }

                if (result.ExitCode == 0)
                {
                    return new DockerResult<string>.Success(result.StandardOutput);
                }

                if (result.ExitCode == DockerFileExecProtocol.MissingExitCode)
                {
                    return new DockerResult<string>.Failure(new DockerError(
                        DockerErrorCode.FileNotFound,
                        $"'{path}' does not exist in this Docker resource.",
                        false));
                }

                if (result.ExitCode == DockerFileExecProtocol.NotDirectoryExitCode)
                {
                    return new DockerResult<string>.Failure(new DockerError(
                        DockerErrorCode.NotDirectory,
                        $"'{path}' is not a directory.",
                        false));
                }

                if (result.ExitCode is not (126 or 127))
                {
                    return new DockerResult<string>.Failure(CommandError(result));
                }
            }
        }

        return new DockerResult<string>.Failure(new DockerError(
            DockerErrorCode.FileProtocolUnavailable,
            "This Docker resource has no supported POSIX shell for shallow filesystem browsing.",
            false));
    }

    private async ValueTask<DockerResult<IReadOnlyList<FileExecTarget>>> ResolveFileExecTargetsAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        CancellationToken cancellationToken)
    {
        if (resource.Kind == DockerResourceKind.Container)
        {
            return new DockerResult<IReadOnlyList<FileExecTarget>>.Success(
                Array.AsReadOnly([new FileExecTarget(resource.Id, "/", null, true)]));
        }

        if (resource.Kind == DockerResourceKind.Image)
        {
            return new DockerResult<IReadOnlyList<FileExecTarget>>.Success(
                Array.AsReadOnly([new FileExecTarget(resource.Id, "/", null, false)]));
        }

        if (resource.Kind == DockerResourceKind.Network)
        {
            return new DockerResult<IReadOnlyList<FileExecTarget>>.Failure(new DockerError(
                DockerErrorCode.FileProtocolUnavailable,
                "Docker networks do not have a filesystem.",
                false));
        }

        var images = await ExecuteAsync(
            connection,
            ["image", "ls", "--quiet", "--no-trunc"],
            ReadTimeout,
            ListOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (images is DockerResult<string>.Failure imageFailure)
        {
            return new DockerResult<IReadOnlyList<FileExecTarget>>.Failure(imageFailure.Error);
        }

        var targets = ((DockerResult<string>.Success)images).Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .Select(imageId => new FileExecTarget(
                imageId,
                VolumeBrowsePath,
                $"type=volume,source={resource.Id},target={VolumeBrowsePath},readonly",
                false))
            .ToArray();
        return targets.Length == 0
            ? new DockerResult<IReadOnlyList<FileExecTarget>>.Failure(new DockerError(
                DockerErrorCode.FileProtocolUnavailable,
                "Browsing a volume needs one local image with a supported POSIX shell.",
                false))
            : new DockerResult<IReadOnlyList<FileExecTarget>>.Success(Array.AsReadOnly(targets));
    }

    private static IReadOnlyList<string> FileExecArguments(
        FileExecTarget target,
        string shellPath,
        string script,
        string path)
    {
        if (target.IsExistingContainer)
        {
            return Array.AsReadOnly<string>(
            [
                "exec", target.Identity, shellPath, "-c", script,
                "asura-file-protocol", path,
            ]);
        }

        var arguments = new List<string>
        {
            "run", "--rm", "--network", "none", "--read-only",
        };
        if (target.ReadOnlyMount is { } mount)
        {
            arguments.Add("--mount");
            arguments.Add(mount);
        }

        arguments.Add("--entrypoint");
        arguments.Add(shellPath);
        arguments.Add(target.Identity);
        arguments.AddRange(
        [
            "-c", script, "asura-file-protocol", path,
        ]);
        return Array.AsReadOnly(arguments.ToArray());
    }

    private async ValueTask<DockerResult<FileContainerLease>> AcquireFileContainerAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        CancellationToken cancellationToken)
    {
        if (resource.Kind == DockerResourceKind.Container)
        {
            return new DockerResult<FileContainerLease>.Success(
                new FileContainerLease(resource.Id, "/", false));
        }

        if (resource.Kind == DockerResourceKind.Network)
        {
            return new DockerResult<FileContainerLease>.Failure(new DockerError(
                DockerErrorCode.CommandFailed,
                "Docker networks do not have a filesystem.",
                false));
        }

        IReadOnlyList<string> createArguments;
        var rootPath = "/";
        if (resource.Kind == DockerResourceKind.Image)
        {
            createArguments =
            [
                "container", "create",
                "--label", "com.asura.transient=docker-files",
                resource.Id,
                "asura-file-copy",
            ];
        }
        else
        {
            var imageResult = await ExecuteAsync(
                connection,
                ["image", "ls", "--quiet", "--no-trunc"],
                ReadTimeout,
                ListOutputLimit,
                cancellationToken).ConfigureAwait(false);
            if (imageResult is DockerResult<string>.Failure imageFailure)
            {
                return new DockerResult<FileContainerLease>.Failure(imageFailure.Error);
            }

            var imageId = ((DockerResult<string>.Success)imageResult).Value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (imageId is null)
            {
                return new DockerResult<FileContainerLease>.Failure(new DockerError(
                    DockerErrorCode.CommandFailed,
                    "Browsing a volume needs one local Docker image to mount it. Pull or build an image, then retry.",
                    false));
            }

            createArguments =
            [
                "container", "create",
                "--label", "com.asura.transient=docker-files",
                "--mount", $"type=volume,source={resource.Id},target={VolumeBrowsePath},readonly",
                imageId,
                "asura-file-copy",
            ];
            rootPath = VolumeBrowsePath;
        }

        var createResult = await ExecuteAsync(
            connection,
            createArguments,
            ActionTimeout,
            128 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (createResult is DockerResult<string>.Failure createFailure)
        {
            return new DockerResult<FileContainerLease>.Failure(createFailure.Error);
        }

        var containerId = ((DockerResult<string>.Success)createResult).Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.All(char.IsAsciiHexDigit));
        return containerId is null
            ? InvalidResponse<FileContainerLease>()
            : new DockerResult<FileContainerLease>.Success(
                new FileContainerLease(containerId, rootPath, true));
    }

    private static DockerError CommandError(ConnectionCommandResult result) =>
        result.Outcome switch
        {
            ConnectionCommandOutcome.StartFailed => new DockerError(
                DockerErrorCode.RuntimeUnavailable,
                RuntimeUnavailableMessage,
                true),
            ConnectionCommandOutcome.ConnectionFailed => new DockerError(
                DockerErrorCode.ConnectionFailed,
                "Asura could not connect to this target.",
                true),
            ConnectionCommandOutcome.TimedOut => new DockerError(
                DockerErrorCode.TimedOut,
                "Docker did not respond before the operation timed out.",
                true),
            ConnectionCommandOutcome.Cancelled => new DockerError(
                DockerErrorCode.Cancelled,
                "The Docker operation was cancelled.",
                true),
            _ when IsRuntimeUnavailableFailure(result.StandardError) => new DockerError(
                DockerErrorCode.RuntimeUnavailable,
                RuntimeUnavailableMessage,
                true),
            _ => new DockerError(
                DockerErrorCode.CommandFailed,
                CommandFailureMessage(result.StandardError),
                true),
        };

    private static DockerError CommandError(ConnectionBinaryCommandResult result) =>
        result.Outcome switch
        {
            ConnectionCommandOutcome.StartFailed => new DockerError(
                DockerErrorCode.RuntimeUnavailable,
                RuntimeUnavailableMessage,
                true),
            ConnectionCommandOutcome.ConnectionFailed => new DockerError(
                DockerErrorCode.ConnectionFailed,
                "Asura could not connect to this target.",
                true),
            ConnectionCommandOutcome.TimedOut => new DockerError(
                DockerErrorCode.TimedOut,
                "Docker did not respond before the operation timed out.",
                true),
            ConnectionCommandOutcome.Cancelled => new DockerError(
                DockerErrorCode.Cancelled,
                "The Docker operation was cancelled.",
                true),
            _ when IsRuntimeUnavailableFailure(result.StandardError) => new DockerError(
                DockerErrorCode.RuntimeUnavailable,
                RuntimeUnavailableMessage,
                true),
            _ => new DockerError(
                DockerErrorCode.CommandFailed,
                CommandFailureMessage(result.StandardError),
                true),
        };

    private static DockerError CommandError<T>(ConnectionStreamingCommandResult<T> result) =>
        result.Outcome switch
        {
            ConnectionCommandOutcome.StartFailed => new DockerError(
                DockerErrorCode.RuntimeUnavailable,
                RuntimeUnavailableMessage,
                true),
            ConnectionCommandOutcome.ConnectionFailed => new DockerError(
                DockerErrorCode.ConnectionFailed,
                "Asura could not connect to this target.",
                true),
            ConnectionCommandOutcome.TimedOut => new DockerError(
                DockerErrorCode.TimedOut,
                "Docker did not respond before the operation timed out.",
                true),
            ConnectionCommandOutcome.Cancelled => new DockerError(
                DockerErrorCode.Cancelled,
                "The Docker operation was cancelled.",
                true),
            _ when IsRuntimeUnavailableFailure(result.StandardError) => new DockerError(
                DockerErrorCode.RuntimeUnavailable,
                RuntimeUnavailableMessage,
                true),
            _ => new DockerError(
                DockerErrorCode.CommandFailed,
                CommandFailureMessage(result.StandardError),
                true),
        };

    private static DockerFileContent? ParseFileContent(
        Stream copyStream,
        DockerResourceReference resource,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        // `docker cp ... -` wraps even one selected file in a tar envelope.
        // This parser consumes that one-file response; directory enumeration
        // never reaches this code path.
        using var reader = new TarReader(copyStream, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is not (
                    TarEntryType.RegularFile
                    or TarEntryType.V7RegularFile
                    or TarEntryType.ContiguousFile)
                || entry.DataStream is null)
            {
                continue;
            }

            var capacity = (int)Math.Min(maximumBytes, int.MaxValue);
            using var content = new MemoryStream(Math.Min(capacity, 64 * 1024));
            var buffer = new byte[Math.Min(capacity, 64 * 1024)];
            while (content.Length < capacity)
            {
                var remaining = capacity - (int)content.Length;
                var read = entry.DataStream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                content.Write(buffer, 0, read);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new DockerFileContent(
                resource,
                path,
                content.ToArray(),
                entry.Length > content.Length);
        }

        return null;
    }

    private static string NormalizeFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] != '/' || path.Contains('\0'))
        {
            throw new ArgumentException("Docker file paths must be absolute POSIX paths.", nameof(path));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Docker file paths cannot contain traversal segments.", nameof(path));
        }

        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private static bool Supports(ConnectionProfile connection) =>
        connection.Endpoint is ConnectionEndpoint.Local or ConnectionEndpoint.Ssh;

    private static DockerEngineSummary ParseEngine(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new DockerEngineSummary(
            Text(root, "Version"),
            Text(root, "Os"),
            Text(root, "Arch"),
            Text(root, "ApiVersion"));
    }

    private static IReadOnlyList<DockerContainerSummary> ParseContainers(
        string output,
        IReadOnlyDictionary<string, ContainerStatistics> statistics)
    {
        var containers = new List<DockerContainerSummary>();
        foreach (var row in JsonLines(output))
        {
            var id = Text(row, "ID");
            var name = Text(row, "Names");
            statistics.TryGetValue(id, out var stats);
            stats ??= statistics.GetValueOrDefault(name);
            containers.Add(new DockerContainerSummary(
                id,
                name,
                Text(row, "Image"),
                Text(row, "State"),
                Text(row, "Status"),
                Text(row, "Ports"),
                Text(row, "RunningFor", Text(row, "CreatedAt")),
                stats?.Cpu ?? "—",
                stats?.Memory ?? "—",
                stats?.NetworkIo ?? "—",
                stats?.BlockIo ?? "—",
                Label(row, "com.docker.compose.project"),
                Label(row, "com.docker.compose.service")));
        }

        return Array.AsReadOnly(containers.ToArray());
    }

    private static IReadOnlyList<DockerImageSummary> ParseImages(string output) =>
        Array.AsReadOnly(JsonLines(output)
            .Select(row => new DockerImageSummary(
                Text(row, "ID"),
                Text(row, "Repository", "<none>"),
                Text(row, "Tag", "<none>"),
                Text(row, "Size"),
                Text(row, "CreatedSince", Text(row, "CreatedAt"))))
            .ToArray());

    private static IReadOnlyList<DockerVolumeSummary> ParseVolumes(string output)
    {
        var volumes = new List<DockerVolumeSummary>();
        foreach (var line in OutputLines(output))
        {
            var fields = line.Split('\t');
            if (fields.Length != 4)
            {
                throw new InvalidOperationException("Docker volume list row has an invalid shape.");
            }

            volumes.Add(new DockerVolumeSummary(
                ParseJsonString(fields[0]),
                ParseJsonString(fields[1]),
                ParseJsonString(fields[2]),
                ParseJsonString(fields[3])));
        }

        return Array.AsReadOnly(volumes.ToArray());
    }

    private static IReadOnlyList<DockerVolumeUsage> ParseVolumeUsage(string output)
    {
        var usage = new List<DockerVolumeUsage>();
        foreach (var line in OutputLines(output))
        {
            var fields = line.Split('\t');
            if (fields.Length != 2)
            {
                throw new InvalidOperationException("Docker volume usage row has an invalid shape.");
            }

            var size = ParseJsonString(fields[1]);
            usage.Add(new DockerVolumeUsage(
                ParseJsonString(fields[0]),
                size,
                ParseDockerByteSize(size)));
        }

        return Array.AsReadOnly(usage.ToArray());
    }

    private static IEnumerable<string> OutputLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'));

    private static string ParseJsonString(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString() ?? string.Empty
            : throw new InvalidOperationException("Docker formatted field is not a JSON string.");
    }

    private static long? ParseDockerByteSize(string value)
    {
        var span = value.AsSpan().Trim();
        var unitIndex = 0;
        while (unitIndex < span.Length
               && (char.IsAsciiDigit(span[unitIndex]) || span[unitIndex] is '.' or ','))
        {
            unitIndex++;
        }

        if (unitIndex == 0
            || !decimal.TryParse(
                span[..unitIndex].ToString().Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount)
            || amount < 0)
        {
            return null;
        }

        var multiplier = span[unitIndex..].Trim().ToString().ToUpperInvariant() switch
        {
            "B" => 1m,
            "KB" => 1_000m,
            "MB" => 1_000_000m,
            "GB" => 1_000_000_000m,
            "TB" => 1_000_000_000_000m,
            "KIB" => 1_024m,
            "MIB" => 1_048_576m,
            "GIB" => 1_073_741_824m,
            "TIB" => 1_099_511_627_776m,
            _ => -1m,
        };
        if (multiplier <= 0 || amount > long.MaxValue / multiplier)
        {
            return null;
        }

        var bytes = amount * multiplier;
        return bytes <= long.MaxValue
            ? decimal.ToInt64(decimal.Round(bytes, 0, MidpointRounding.AwayFromZero))
            : null;
    }

    private static IReadOnlyList<DockerNetworkSummary> ParseNetworks(string output) =>
        Array.AsReadOnly(JsonLines(output)
            .Select(row => new DockerNetworkSummary(
                Text(row, "ID"),
                Text(row, "Name"),
                Text(row, "Driver"),
                Text(row, "Scope"),
                Text(row, "CreatedAt")))
            .ToArray());

    private static IReadOnlyDictionary<string, ContainerStatistics> ParseStatistics(string output)
    {
        var statistics = new Dictionary<string, ContainerStatistics>(StringComparer.Ordinal);
        foreach (var row in JsonLines(output))
        {
            var value = new ContainerStatistics(
                Text(row, "CPUPerc", "—"),
                Text(row, "MemUsage", "—"),
                Text(row, "NetIO", "—"),
                Text(row, "BlockIO", "—"));
            var id = Text(row, "Container");
            var name = Text(row, "Name");
            if (!string.IsNullOrEmpty(id))
            {
                statistics[id] = value;
            }

            if (!string.IsNullOrEmpty(name))
            {
                statistics[name] = value;
            }
        }

        return statistics;
    }

    private static IReadOnlyList<DockerInspectionProperty> InspectionProperties(
        DockerResourceKind kind,
        JsonElement root)
    {
        var paths = kind switch
        {
            DockerResourceKind.Container => new[]
            {
                "Id", "Created", "State.Status", "State.Running", "State.StartedAt",
                "Config.Image", "Config.Hostname", "Config.WorkingDir",
                "HostConfig.NetworkMode", "NetworkSettings.IPAddress",
            },
            DockerResourceKind.Image =>
            [
                "Id", "Created", "Os", "Architecture", "Size", "Config.WorkingDir",
                "Config.Entrypoint", "Config.Cmd",
            ],
            DockerResourceKind.Volume =>
            [
                "Name", "Driver", "Mountpoint", "Scope", "CreatedAt", "Labels",
            ],
            DockerResourceKind.Network =>
            [
                "Name", "Id", "Created", "Driver", "Scope", "Internal", "IPAM.Config",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return Array.AsReadOnly(paths
            .Select(path => new DockerInspectionProperty(path, ReadPath(root, path)))
            .Where(property => !string.IsNullOrWhiteSpace(property.Value))
            .ToArray());
    }

    private static IEnumerable<JsonElement> JsonLines(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private static string ReadPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return string.Empty;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            // docker inspect is pretty-printed for the JSON tab. Reusing that
            // source whitespace here turned arrays into tall, bracket-per-line
            // values in the property inspector. Serialize compound values again
            // so the summary remains valid JSON without inheriting presentation.
            _ => FormatJson(current, indented: false),
        };
    }

    private static string FormatJson(JsonElement value, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = indented }))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static string Text(JsonElement row, string name, string fallback = "") =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string? Label(JsonElement row, string name)
    {
        var labels = Text(row, "Labels");
        if (string.IsNullOrWhiteSpace(labels))
        {
            return null;
        }

        var prefix = $"{name}=";
        return labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(label => label.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    private static string CommandFailureMessage(string standardError)
    {
        var message = standardError.Trim();
        if (string.IsNullOrEmpty(message))
        {
            return "Docker could not complete this operation.";
        }

        const int maximumLength = 800;
        return message.Length <= maximumLength
            ? message
            : $"{message[..maximumLength]}…";
    }

    private static bool IsRuntimeUnavailableFailure(string standardError) =>
        standardError.Contains(
            "failed to find target executable docker",
            StringComparison.OrdinalIgnoreCase)
        || standardError.Contains(
            "docker: command not found",
            StringComparison.OrdinalIgnoreCase)
        || standardError.Contains(
            "command not found: docker",
            StringComparison.OrdinalIgnoreCase)
        || standardError.Contains(
            "cannot connect to the docker daemon",
            StringComparison.OrdinalIgnoreCase)
        || standardError.Contains(
            "is the docker daemon running",
            StringComparison.OrdinalIgnoreCase);

    private static DockerResult<T> Unsupported<T>() =>
        new DockerResult<T>.Failure(new DockerError(
            DockerErrorCode.ConnectionFailed,
            "Docker panels support local and SSH connections.",
            false));

    private static DockerResult<T> InvalidResponse<T>() =>
        new DockerResult<T>.Failure(new DockerError(
            DockerErrorCode.InvalidResponse,
            "Docker returned a response Asura could not read.",
            true));

    private sealed record ContainerStatistics(
        string Cpu,
        string Memory,
        string NetworkIo,
        string BlockIo);

    private sealed record FileContainerLease(
        string ContainerId,
        string RootPath,
        bool IsTemporary);

    private sealed record FileExecTarget(
        string Identity,
        string RootPath,
        string? ReadOnlyMount,
        bool IsExistingContainer);
}
