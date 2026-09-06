using GhostShell.Core;
using GhostShell.Docker;

namespace GhostShell.Application;

public readonly record struct DockerResourceReferenceId
{
    public DockerResourceReferenceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "A Docker resource reference must be an opaque bounded token.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record DockerPanelSessionState(
    string ConnectionDisplayName,
    ConnectionKind ConnectionKind,
    DockerEngineGeneration EngineGeneration,
    DockerEngineSummary Engine,
    bool IsReady);

public sealed record DockerResourceItem(
    DockerResourceReferenceId Reference,
    DockerResourceKind Kind,
    string DisplayName);

public sealed record DockerContainerItem(
    DockerResourceItem Resource,
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
    string? ComposeService = null,
    DockerContainerRevision? ControlRevision = null);

public sealed record DockerImageItem(
    DockerResourceItem Resource,
    string Repository,
    string Tag,
    string Size,
    string Created);

public sealed record DockerVolumeItem(
    DockerResourceItem Resource,
    string Driver,
    string Scope,
    string Size,
    long? SizeBytes);

public sealed record DockerNetworkItem(
    DockerResourceItem Resource,
    string Driver,
    string Scope,
    string Created);

public sealed record DockerPanelSnapshot(
    DockerEngineSummary Engine,
    IReadOnlyList<DockerContainerItem> Containers,
    IReadOnlyList<DockerImageItem> Images,
    IReadOnlyList<DockerVolumeItem> Volumes,
    IReadOnlyList<DockerNetworkItem> Networks,
    DateTimeOffset CapturedAtUtc,
    bool IsTruncated);

public sealed record DockerInspectionSnapshot(
    DockerResourceItem Resource,
    IReadOnlyList<DockerInspectionProperty> Properties,
    bool IsTruncated);

public sealed record DockerLogReadRequest(
    DockerResourceReferenceId Container,
    int Limit = 500,
    string? BeforeTimestamp = null,
    string? SinceTimestamp = null,
    string? SearchText = null,
    int ContextLines = 0);

public sealed record DockerFileListRequest(
    DockerResourceReferenceId Resource,
    string Path,
    int MaximumEntries = 500);

public sealed record DockerFileStatRequest(
    DockerResourceReferenceId Resource,
    string Path);

public sealed record DockerFileReadRequest(
    DockerResourceReferenceId Resource,
    string Path,
    int MaximumBytes);

public sealed record DockerFilePage(
    DockerResourceItem Resource,
    string Path,
    IReadOnlyList<DockerFileEntry> Entries,
    bool IsTruncated);

public sealed record DockerFileSnapshot(
    DockerResourceItem Resource,
    string Path,
    ReadOnlyMemory<byte> Content,
    bool IsTruncated);

/// <summary>
/// The primary hosted Docker engine. Embedded terminal and File Viewer panels
/// remain separate hosted sessions with their own identities and capabilities.
/// </summary>
public interface IDockerPanelSession : IPanelSession
{
    DockerSessionBinding Binding { get; }

    DockerPanelSessionState State { get; }

    ValueTask<DockerResult<DockerPanelSnapshot>> ReadStateAsync(
        int maximumResourcesPerKind,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerInspectionSnapshot>> InspectAsync(
        DockerResourceReferenceId reference,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerContainerLogPage>> ReadLogsAsync(
        DockerLogReadRequest request,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerFilePage>> ListFilesAsync(
        DockerFileListRequest request,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
        DockerFileStatRequest request,
        CancellationToken cancellationToken);

    ValueTask<DockerResult<DockerFileSnapshot>> ReadFileAsync(
        DockerFileReadRequest request,
        CancellationToken cancellationToken);

    ValueTask<DockerContainerControlResult> ControlContainerAsync(
        DockerContainerControlRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DockerContainerControlResult(
            DockerContainerControlOutcome.NotDispatched,
            "docker_container_control_unavailable",
            Retryable: false));
}

public interface IDockerPanelSessionFactory
{
    CapabilitySet Capabilities { get; }

    ValueTask<IDockerPanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        DockerSessionTarget target,
        CancellationToken cancellationToken);
}
