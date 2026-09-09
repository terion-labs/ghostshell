using Asura.Core;

namespace Asura.Docker;

public enum DockerResourceKind
{
    Container,
    Image,
    Volume,
    Network,
}

public enum DockerContainerAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Resume,
    Remove,
}

public enum DockerContainerMutationOutcome
{
    Applied,
    NotDispatched,
    OutcomeUnknown,
}

public sealed record DockerContainerMutationResult(
    DockerContainerMutationOutcome Outcome,
    string StableCode,
    bool Retryable);

public sealed record DockerEngineSnapshot(
    DockerEngineSummary Engine,
    IReadOnlyList<DockerContainerSummary> Containers,
    IReadOnlyList<DockerImageSummary> Images,
    IReadOnlyList<DockerVolumeSummary> Volumes,
    IReadOnlyList<DockerNetworkSummary> Networks,
    DateTimeOffset CapturedAtUtc);

public sealed record DockerEngineSummary(
    string Version,
    string OperatingSystem,
    string Architecture,
    string ApiVersion);

public sealed record DockerContainerSummary(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    string Ports,
    string Created,
    string Cpu,
    string Memory,
    string NetworkIo,
    string BlockIo,
    string? ComposeProject = null,
    string? ComposeService = null)
{
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    public bool IsPaused => string.Equals(State, "paused", StringComparison.OrdinalIgnoreCase);

    public bool IsStandalone => string.IsNullOrWhiteSpace(ComposeProject);

    public string StackName => IsStandalone
        ? "Standalone containers"
        : ComposeProject!;
}

public sealed record DockerImageSummary(
    string Id,
    string Repository,
    string Tag,
    string Size,
    string Created);

public sealed record DockerVolumeSummary(
    string Name,
    string Driver,
    string Scope,
    string Mountpoint,
    string Size = "—",
    long? SizeBytes = null);

public sealed record DockerVolumeUsage(
    string Name,
    string Size,
    long? SizeBytes);

public sealed record DockerNetworkSummary(
    string Id,
    string Name,
    string Driver,
    string Scope,
    string Created);

public sealed record DockerResourceReference(
    DockerResourceKind Kind,
    string Id,
    string DisplayName);

public sealed record DockerInspectionProperty(
    string Name,
    string Value);

public sealed record DockerResourceInspection(
    DockerResourceReference Resource,
    IReadOnlyList<DockerInspectionProperty> Properties,
    string Json);

public enum DockerFileKind
{
    File,
    Directory,
    Link,
    Other,
}

public sealed record DockerFileEntry(
    string Name,
    string Path,
    DockerFileKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt);

public sealed record DockerFileListing(
    DockerResourceReference Resource,
    string Path,
    IReadOnlyList<DockerFileEntry> Entries);

public sealed record DockerFileContent(
    DockerResourceReference Resource,
    string Path,
    ReadOnlyMemory<byte> Content,
    bool IsTruncated);

public sealed record DockerContainerLogRequest(
    string ContainerId,
    int Limit = 500,
    string? BeforeTimestamp = null,
    string? SinceTimestamp = null,
    string? SearchText = null,
    int ContextLines = 0);

public sealed record DockerContainerLogLine(
    string Timestamp,
    string Message,
    bool StartsContextBlock = false)
{
    public string RawText => string.IsNullOrEmpty(Timestamp)
        ? Message
        : $"{Timestamp} {Message}";
}

public sealed record DockerContainerLogPage(
    IReadOnlyList<DockerContainerLogLine> Lines,
    bool HasOlder,
    string? OldestTimestamp,
    string? NewestTimestamp);

public enum DockerErrorCode
{
    RuntimeUnavailable,
    ConnectionFailed,
    CommandFailed,
    ShellUnavailable,
    TimedOut,
    Cancelled,
    InvalidResponse,
    FileNotFound,
    NotDirectory,
    FileProtocolUnavailable,
}

public sealed record DockerError(
    DockerErrorCode Code,
    string Message,
    bool Retryable);

public abstract record DockerResult<T>
{
    private DockerResult()
    {
    }

    public sealed record Success(T Value) : DockerResult<T>;

    public sealed record Failure(DockerError Error) : DockerResult<T>;
}

public interface IDockerEngineClient
{
    bool SupportsContainerMutation => false;

    ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerResourceInspection>> InspectAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
        ConnectionProfile connection,
        DockerContainerLogRequest request,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<bool>> DownloadContainerLogsAsync(
        ConnectionProfile connection,
        string containerId,
        Stream destination,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<string>> ResolveContainerShellAsync(
        ConnectionProfile connection,
        string containerId,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerFileListing>> ListFilesAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<DockerResult<DockerFileEntry>>(
            new DockerResult<DockerFileEntry>.Failure(new DockerError(
                DockerErrorCode.FileProtocolUnavailable,
                "This Docker client cannot inspect filesystem entries.",
                false)));

    ValueTask<DockerResult<DockerFileContent>> ReadFileAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<DockerResult<DockerFileContent>>(
            new DockerResult<DockerFileContent>.Failure(new DockerError(
                DockerErrorCode.CommandFailed,
                "This Docker client cannot read file contents.",
                false)));

    ValueTask<DockerResult<bool>> RunContainerActionAsync(
        ConnectionProfile connection,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken);

    ValueTask<DockerContainerMutationResult> RunContainerMutationAsync(
        ConnectionProfile connection,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DockerContainerMutationResult(
            DockerContainerMutationOutcome.NotDispatched,
            "docker_container_control_unavailable",
            Retryable: false));
}

/// <summary>
/// Builds the startup command used by a normal Asura terminal session to
/// attach to a running container. Keeping this command in the application
/// boundary lets presentation request a shell without referencing the concrete
/// Docker adapter.
/// </summary>
public static class DockerContainerShellCommand
{
    public static IReadOnlyList<string> CandidatePaths { get; } = Array.AsReadOnly(
    [
        "/bin/sh",
        "/bin/bash",
        "/bin/ash",
        "/bin/dash",
        "/bin/zsh",
        "/usr/bin/nu",
        "/bin/ksh",
    ]);

    public static string Build(string containerId, string shellPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(shellPath);
        return $"docker exec --interactive --tty {Quote(containerId)} {Quote(shellPath)}";
    }

    public static bool IsContainerShellCommand(string? command) =>
        command?.StartsWith(
            "docker exec --interactive --tty ",
            StringComparison.Ordinal) == true
        && CandidatePaths.Any(shellPath =>
            command.EndsWith($" {Quote(shellPath)}", StringComparison.Ordinal));

    private static string Quote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
