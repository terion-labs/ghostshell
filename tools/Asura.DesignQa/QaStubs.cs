using System.Globalization;
using System.Reflection;
using Asura.App;
using Asura.Application;
using Asura.Core;
using Asura.Docker;
using Asura.Git;
using Avalonia.Media;

namespace Asura.DesignQa;

/// <summary>
/// In-memory collaborators for the presentation-only design QA harness. They
/// never touch SQLite, the OS vault, terminal sessions, or the user profile.
/// </summary>
internal sealed class QaDefinitionCatalog : IDefinitionCatalog
{
    private readonly Func<ThemePreference, ThemePreference>? _normalizeTheme;

    public QaDefinitionCatalog(
        DefinitionCatalogSnapshot snapshot,
        Func<ThemePreference, ThemePreference>? normalizeTheme = null)
    {
        Snapshot = snapshot;
        _normalizeTheme = normalizeTheme;
    }

    public DefinitionCatalogSnapshot Snapshot { get; private set; }

    public event EventHandler? Changed;

    public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(Snapshot));

    public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(Snapshot));

    private static ValueTask<DefinitionStoreResult<StoredDefinition<T>>> Store<T>(T definition)
        where T : IDurableDefinition =>
        ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<T>>.Success(
                new StoredDefinition<T>(
                    definition,
                    1,
                    QaData.Now,
                    QaData.Now)));

    public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
        LayoutDefinition definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
        ScreenDefinition definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    /// <summary>
    /// A saved workspace lands in the snapshot, for the same reason the theme
    /// does: the workspaces rail is drawn from the catalog, so a stub that
    /// reported success and changed nothing would capture a rail that
    /// contradicts the save it just made.
    /// </summary>
    public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
        WorkspaceDefinition definition, long? expectedRevision, CancellationToken cancellationToken)
    {
        var stored = new StoredDefinition<WorkspaceDefinition>(
            definition,
            (expectedRevision ?? 0) + 1,
            QaData.Now,
            QaData.Now);
        Snapshot = Snapshot with
        {
            Workspaces =
            [
                .. Snapshot.Workspaces.Where(item => item.Value.Id != definition.Id),
                stored,
            ],
        };
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(stored));
    }

    /// <summary>
    /// The saved appearance, kept so the harness can re-publish it exactly as
    /// the product does — "settings apply immediately" is itself a reviewable
    /// behavior, and a stub that swallowed the save made it uncapturable.
    /// </summary>
    public ThemePreference? SavedTheme { get; private set; }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        ThemePreference definition, long? expectedRevision, CancellationToken cancellationToken)
    {
        var savedTheme = _normalizeTheme?.Invoke(definition) ?? definition;
        var stored = new StoredDefinition<ThemePreference>(
            savedTheme,
            (expectedRevision ?? 0) + 1,
            QaData.Now,
            QaData.Now);
        SavedTheme = savedTheme;
        Snapshot = Snapshot with
        {
            Themes =
            [
                .. Snapshot.Themes.Where(item => item.Value.Id != savedTheme.Id),
                stored,
            ],
        };
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<ThemePreference>>.Success(stored));
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        TerminalProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
        KeymapProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
        FileProviderProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
        AiProviderProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
        McpServerProfile definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        QuickTerminalSettings definition, long? expectedRevision, CancellationToken cancellationToken) =>
        Store(definition);

    public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key, long expectedRevision, CancellationToken cancellationToken) =>
        ValueTask.FromResult(DefinitionStoreResult<Unit>.Success(Unit.Value));

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class ImmediateUiDispatcher : IUiThreadDispatcher
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}

internal sealed class QaDockerEngineClient : IDockerEngineClient
{
    private static readonly DockerEngineSnapshot Snapshot = new(
        new DockerEngineSummary("28.3.0", "Docker Desktop", "arm64", "1.51"),
        [
            Container("api", "ghcr.io/asura/api:main", "running", "Up 2 hours", "3.8%", "284 MiB / 2 GiB", "asura"),
            Container("postgres", "postgres:17-alpine", "running", "Up 2 hours (healthy)", "1.1%", "196 MiB / 2 GiB", "asura"),
            Container("redis", "redis:8-alpine", "running", "Up 2 hours", "0.4%", "42 MiB / 2 GiB", "asura"),
            Container("worker", "ghcr.io/asura/worker:main", "running", "Up 2 hours", "8.6%", "418 MiB / 2 GiB", "workers"),
            Container("mailpit", "axllent/mailpit:v1.27", "running", "Up 2 hours", "0.2%", "31 MiB / 2 GiB", "tooling"),
            Container("migrations", "ghcr.io/asura/api:main", "exited", "Exited (0) 2 hours ago", "—", "—", "asura"),
        ],
        [
            new DockerImageSummary("sha256:api", "ghcr.io/asura/api", "main", "386 MB", "2 days ago"),
            new DockerImageSummary("sha256:postgres", "postgres", "17-alpine", "274 MB", "3 weeks ago"),
            new DockerImageSummary("sha256:redis", "redis", "8-alpine", "60 MB", "1 month ago"),
            new DockerImageSummary("sha256:mailpit", "axllent/mailpit", "v1.27", "52 MB", "2 months ago"),
        ],
        [
            new DockerVolumeSummary("asura_postgres", "local", "local", "/var/lib/docker/volumes/asura_postgres/_data"),
            new DockerVolumeSummary("asura_cache", "local", "local", "/var/lib/docker/volumes/asura_cache/_data"),
            new DockerVolumeSummary("asura_uploads", "local", "local", "/var/lib/docker/volumes/asura_uploads/_data"),
        ],
        [
            new DockerNetworkSummary("network-app", "asura_default", "bridge", "local", "2026-08-08"),
            new DockerNetworkSummary("network-bridge", "bridge", "bridge", "local", "2026-08-01"),
            new DockerNetworkSummary("network-host", "host", "host", "local", "2026-08-01"),
        ],
        new DateTimeOffset(2026, 8, 10, 9, 30, 0, TimeSpan.Zero));

    public ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<DockerResult<DockerEngineSnapshot>>(
            new DockerResult<DockerEngineSnapshot>.Success(Snapshot));

    public ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<DockerResult<IReadOnlyList<DockerVolumeUsage>>>(
            new DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success(
            [
                new DockerVolumeUsage("asura_postgres", "2.4 GB", 2_400_000_000),
                new DockerVolumeUsage("asura_uploads", "680 MB", 680_000_000),
                new DockerVolumeUsage("asura_cache", "42 MB", 42_000_000),
            ]));

    public ValueTask<DockerResult<DockerResourceInspection>> InspectAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerInspectionProperty> properties = resource.Kind switch
        {
            DockerResourceKind.Image =>
            [
                new DockerInspectionProperty("ID", resource.Id),
                new DockerInspectionProperty("Tag", resource.DisplayName),
                new DockerInspectionProperty("Created", "2 days ago"),
                new DockerInspectionProperty("Size", "386 MB"),
                new DockerInspectionProperty("Platform", "linux/arm64"),
                new DockerInspectionProperty("Working directory", "/app"),
                new DockerInspectionProperty("Entrypoint", "[\"/usr/local/bin/docker-entrypoint.sh\"]"),
                new DockerInspectionProperty("Command", "[\"bun\",\"server/index.mjs\"]"),
            ],
            DockerResourceKind.Network =>
            [
                new DockerInspectionProperty("Name", resource.DisplayName),
                new DockerInspectionProperty("ID", resource.Id),
                new DockerInspectionProperty("Created", "Aug 8, 2026 at 14:20"),
                new DockerInspectionProperty("Subnet", "172.22.0.0/16"),
                new DockerInspectionProperty("Gateway", "172.22.0.1"),
                new DockerInspectionProperty("Driver", "bridge"),
                new DockerInspectionProperty("Scope", "local"),
            ],
            _ =>
            [
                new DockerInspectionProperty("ID", $"sha256:{resource.Id}"),
                new DockerInspectionProperty("State", "running"),
                new DockerInspectionProperty("Created", "Aug 8, 2026 at 14:22"),
                new DockerInspectionProperty("Image", "ghcr.io/asura/api:main"),
                new DockerInspectionProperty("Ports", "0.0.0.0:8080 → 8080/tcp"),
                new DockerInspectionProperty("Network", "asura_default"),
                new DockerInspectionProperty("Address", "172.22.0.4"),
                new DockerInspectionProperty("Working directory", "/app"),
            ],
        };
        return ValueTask.FromResult<DockerResult<DockerResourceInspection>>(
            new DockerResult<DockerResourceInspection>.Success(
                new DockerResourceInspection(
                    resource,
                    properties,
                    "{\n  \"Id\": \"sha256:api\",\n  \"State\": { \"Status\": \"running\" }\n}")));
    }

    public ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
        ConnectionProfile connection,
        DockerContainerLogRequest request,
        CancellationToken cancellationToken)
    {
        var allLines = Enumerable.Range(0, 120)
            .Select(index => new DockerContainerLogLine(
                $"2026-08-10T09:{28 + (index / 60):00}:{index % 60:00}.000000000Z",
                index % 11 == 0
                    ? $"warn retrying upstream request attempt={index / 11 + 1}"
                    : $"info request completed status=200 duration={12 + index % 31}ms"))
            .ToArray();
        var lines = allLines;
        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            var indexes = new SortedSet<int>();
            for (var index = 0; index < allLines.Length; index++)
            {
                if (!allLines[index].Message.Contains(request.SearchText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (var context = Math.Max(0, index - request.ContextLines);
                     context <= Math.Min(allLines.Length - 1, index + request.ContextLines);
                     context++)
                {
                    indexes.Add(context);
                }
            }

            var previous = -2;
            lines = [.. indexes.Select(index =>
            {
                var line = allLines[index] with { StartsContextBlock = index != previous + 1 };
                previous = index;
                return line;
            })];
        }

        return ValueTask.FromResult<DockerResult<DockerContainerLogPage>>(
            new DockerResult<DockerContainerLogPage>.Success(new DockerContainerLogPage(
                lines,
                true,
                lines.FirstOrDefault()?.Timestamp,
                lines.LastOrDefault()?.Timestamp)));
    }

    public ValueTask<DockerResult<bool>> DownloadContainerLogsAsync(
        ConnectionProfile connection,
        string containerId,
        Stream destination,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<DockerResult<bool>>(new DockerResult<bool>.Success(true));

    public ValueTask<DockerResult<string>> ResolveContainerShellAsync(
        ConnectionProfile connection,
        string containerId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<DockerResult<string>>(
            new DockerResult<string>.Success("/bin/sh"));

    public ValueTask<DockerResult<DockerFileListing>> ListFilesAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerFileEntry> entries = string.Equals(path, "/"
, StringComparison.Ordinal) ?
            [
                Directory("bin"),
                Directory("etc"),
                Directory("home"),
                Directory("opt"),
                Directory("usr"),
                Directory("var"),
                File(".dockerenv", 0),
                File("README.md", 1240),
                File("run.sh", 4096),
            ]
            : [];
        return ValueTask.FromResult<DockerResult<DockerFileListing>>(
            new DockerResult<DockerFileListing>.Success(
                new DockerFileListing(resource, path, entries)));
    }

    public ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        CancellationToken cancellationToken)
    {
        var name = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        DockerFileEntry? entry = name switch
        {
            "README.md" => File("README.md", 1240),
            "run.sh" => File("run.sh", 4096),
            ".dockerenv" => File(".dockerenv", 0),
            { } directory when directory is "bin" or "etc" or "home" or "opt" or "usr" or "var" =>
                Directory(directory),
            _ => null,
        };
        return entry is null
            ? ValueTask.FromResult<DockerResult<DockerFileEntry>>(
                new DockerResult<DockerFileEntry>.Failure(new DockerError(
                    DockerErrorCode.FileNotFound,
                    $"'{path}' does not exist.",
                    false)))
            : ValueTask.FromResult<DockerResult<DockerFileEntry>>(
                new DockerResult<DockerFileEntry>.Success(entry with { Path = path }));
    }

    public ValueTask<DockerResult<DockerFileContent>> ReadFileAsync(
        ConnectionProfile connection,
        DockerResourceReference resource,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "# Container image\n\nThis content is rendered by the shared File Viewer preview.\n");
        var length = (int)Math.Min(bytes.Length, maximumBytes);
        return ValueTask.FromResult<DockerResult<DockerFileContent>>(
            new DockerResult<DockerFileContent>.Success(new DockerFileContent(
                resource,
                path,
                bytes.AsMemory(0, length),
                length < bytes.Length)));
    }

    public ValueTask<DockerResult<bool>> RunContainerActionAsync(
        ConnectionProfile connection,
        string containerId,
        DockerContainerAction action,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<DockerResult<bool>>(new DockerResult<bool>.Success(true));

    private static DockerFileEntry Directory(string name) => new(
        name,
        $"/{name}",
        DockerFileKind.Directory,
        null,
        new DateTimeOffset(2026, 8, 8, 14, 22, 0, TimeSpan.Zero));

    private static DockerFileEntry File(string name, long size) => new(
        name,
        $"/{name}",
        DockerFileKind.File,
        size,
        new DateTimeOffset(2026, 8, 8, 14, 22, 0, TimeSpan.Zero));

    private static DockerContainerSummary Container(
        string name,
        string image,
        string state,
        string status,
        string cpu,
        string memory,
        string? composeProject) =>
        new(
            $"{name}-01f9a4",
            name,
            image,
            state,
            status,
string.Equals(name, "api", StringComparison.Ordinal) ? "0.0.0.0:8080→8080/tcp" : string.Empty,
            "2 hours ago",
            cpu,
            memory,
            "4.2 MB / 2.8 MB",
            "18 MB / 4 KB",
            composeProject,
            name);
}

internal sealed class MemoryOnlySecretVault : ISecretVault
{
    public SecretVaultAvailability Availability { get; } = new(
        SecretVaultAvailabilityState.Available,
        SecretVaultPersistenceKind.MemoryOnly,
        SecretVaultCapabilities.ListMetadata,
        "qa",
        "qa_vault",
        "Isolated design QA vault");

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]));

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request, SecretMaterial material, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request, SecretMaterial material, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Dispose()
    {
    }
}

internal sealed class EmptyFileClients : IFilePanelClient, IFileTransferQueueClient
{
    private readonly List<FilePanelTransferSnapshot> _transfers = [];

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles => [];

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers => _transfers;

    public event EventHandler? TransfersChanged;

    public void PublishSampleTransfer()
    {
        var sourceRoot = new FilePanelLocation(
            "qa.files.source",
            "source",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        var destinationRoot = new FilePanelLocation(
            "qa.files.destination",
            "destination",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        var source = sourceRoot
            .Child(new FilePanelPathSegment("Archive.zip"));
        var destination = destinationRoot
            .Child(new FilePanelPathSegment("Archive.zip"));
        var request = new FilePanelTransferRequest(
            source,
            destination,
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.KeepBoth);
        _transfers.Add(new FilePanelTransferSnapshot(
            new FilePanelTransferId(
                Guid.Parse("e3c4b9cc-96f1-4eb4-8220-cb760c970ae3")),
            request,
            destination,
            FilePanelTransferState.Running,
            "Writing destination",
            700_000_000,
            5_000_000_000,
            null,
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 31, 12, 0, 1, TimeSpan.Zero),
            null));
        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        if (_transfers.Count == 0)
        {
            return;
        }

        _transfers.Clear();
        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(new FilePanelPage([], null)));

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
        FilePanelTransferRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<Unit>> CancelAsync(
        FilePanelTransferId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
        FilePanelTransferId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class MemoryOnlyAuditStore : IAuditStore
{
    public ValueTask<AuditStoreResult<Unit>> AppendAsync(
        AuditEventRecord auditEvent, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));

    public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>> ListByCorrelationAsync(
        string correlationId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success([]));
}

internal sealed class UnusedConnectionRuntime : IConnectionRuntime
{
    public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public class UnusedProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException(targetMethod?.Name ?? "unknown method");
}

/// <summary>
/// A fixed set of recent sessions so the Home page's Recent Sessions section is
/// reviewable. Sessions are read-only here: recording and clearing succeed
/// without changing anything, because a capture run must not depend on the order
/// its routes happen to run in.
/// </summary>
internal sealed class QaRecentSessionStore : IRecentSessionStore
{
    private static readonly RecentSessionRecord[] Records =
    [
        Record("qa-session-api", "production-api", "production-api", TimeSpan.FromMinutes(2)),
        Record("qa-session-cache", "redis-cache", "redis-cache", TimeSpan.FromHours(1)),
        Record("qa-session-local", "local-dev", "local-dev", TimeSpan.FromHours(3)),
        Record("qa-session-db", "postgres-primary", "postgres-primary", TimeSpan.FromHours(15)),
    ];

    private static RecentSessionRecord Record(
        string sessionId,
        string connectionId,
        string title,
        TimeSpan age)
    {
        var endedAt = QaData.Now - age;
        return new RecentSessionRecord(
            new SessionId(sessionId),
            new DefinitionKey(ConnectionProfile.Kind, connectionId),
            PanelKind.Terminal,
            title,
            endedAt - TimeSpan.FromMinutes(12),
            endedAt,
            RecentSessionOutcome.GracefullyClosed);
    }

    public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
        RecentSessionRecord recentSession,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

    public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
        RecentSessionCompletion completion,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

    public ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>> ListRecentAsync(
        RecentSessionQuery query,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success(
            [.. Records.Take(query.Limit)]));

    public ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

    public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
        DateTimeOffset through,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

    public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));
}

/// <summary>
/// Pins "now" to the fixture's timestamp so relative times in captures read the
/// same on every run instead of drifting with the wall clock.
/// </summary>
internal sealed class QaTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => QaData.Now;
}

/// <summary>
/// A file-provider runtime whose test always succeeds, so the unified editor's
/// files family renders without any provider adapter behind the harness.
/// </summary>
internal sealed class QaFileProviderRuntime : IFileProviderProfileRuntime
{
    public event EventHandler? ProfilesChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

    public ValueTask<FileProviderTestResult> TestAsync(
        FileProviderProfile profile,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new FileProviderTestResult(true, "ok", profile.Name));

    public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}

/// <summary>
/// A synchronous database client so the viewer's table list and result grid are
/// reviewable without any database engine behind the harness.
/// </summary>
internal sealed class QaDatabasePanelClient : IDatabasePanelClient
{
    private static readonly IReadOnlyList<DatabaseColumnSchema> DeploymentSchema =
    [
        new(
            "id",
            1,
            "BIGINT",
            DatabaseValueKind.SignedInteger,
            typeof(long).FullName,
            IsNullable: false,
            IsPrimaryKey: true,
            PrimaryKeyOrdinal: 1,
            IsIdentity: true,
            IsReadOnly: true),
        new(
            "service",
            2,
            "VARCHAR(128)",
            DatabaseValueKind.Text,
            typeof(string).FullName,
            IsNullable: false,
            Length: 128),
        new(
            "region",
            3,
            "VARCHAR(32)",
            DatabaseValueKind.Text,
            typeof(string).FullName,
            IsNullable: false,
            Length: 32),
        new(
            "status",
            4,
            "VARCHAR(32)",
            DatabaseValueKind.Text,
            typeof(string).FullName,
            IsNullable: true,
            DefaultExpression: "'pending'",
            Length: 32),
        new(
            "deployed_at",
            5,
            "TIMESTAMP WITH TIME ZONE",
            DatabaseValueKind.TimestampWithZone,
            typeof(DateTimeOffset).FullName,
            IsNullable: false,
            DefaultExpression: "CURRENT_TIMESTAMP"),
    ];

    private static readonly IReadOnlyList<DatabaseIndexSchema> DeploymentIndexes =
    [
        new(
            "deployments_pkey",
            "btree",
            IsUnique: true,
            IsPrimary: true,
            IsValid: true,
            [new DatabaseIndexColumn("id", 1)]),
        new(
            "ix_deployments_service_region",
            "btree",
            IsUnique: false,
            IsPrimary: false,
            IsValid: true,
            [
                new DatabaseIndexColumn("service", 1),
                new DatabaseIndexColumn("region", 2),
                new DatabaseIndexColumn("status", 3, IsIncluded: true),
            ]),
        new(
            "ix_deployments_failures",
            "btree",
            IsUnique: false,
            IsPrimary: false,
            IsValid: true,
            [new DatabaseIndexColumn("deployed_at", 1, IsDescending: true)],
            Predicate: "status = 'rolled-back'"),
    ];

    private static readonly QaDeployment[] Deployments =
    [
        new(184, "billing-api", "eu-central-1", "healthy", At(21, 14, 9)),
        new(183, "billing-api", "us-east-1", "healthy", At(21, 12, 44)),
        new(182, "checkout-web", "eu-central-1", "rolled-back", At(19, 3, 18)),
        new(181, "ledger-worker", "eu-central-1", "healthy", At(17, 40, 51)),
        new(180, "ledger-worker", "us-east-1", null, At(17, 39, 12)),
        new(179, "checkout-web", "ap-south-1", "healthy", At(15, 22, 30)),
    ];

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
    [
        new(
            "postgres",
            "PostgreSQL",
            "Host=localhost;Database=app;Username=postgres",
            DefaultPort: 5432,
            CanListDatabases: true),
        new(
            "mysql",
            "MySQL",
            "Server=localhost;Database=app;User ID=root",
            DefaultPort: 3306,
            CanListDatabases: true),
        new("sqlite", "SQLite", "/path/to/database.db", IsFileBased: true),
    ];

    /// <summary>Captures show a full session line, never a connection string.</summary>
    public Task<DatabaseSessionInfo> DescribeSessionAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        Task.FromResult(driverId switch
        {
            "postgres" => new DatabaseSessionInfo("16.4", "TLSv1.3"),
            "mysql" => new DatabaseSessionInfo("8.4.2", "TLSv1.2"),
            _ => new DatabaseSessionInfo("3.46.1"),
        });

    public Task<IReadOnlyList<string>> ListDatabasesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
            driverId is "sqlite" ? [] : ["app", "app_staging", "postgres"]);

    public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>(
        [
            new("deployments", DatabaseTableKind.Table),
            new("environments", DatabaseTableKind.Table),
            new("recent_failures", DatabaseTableKind.View),
            new("releases", DatabaseTableKind.Table),
            new("service_owners", DatabaseTableKind.View),
        ]);

    public Task<DatabaseQueryPage> QueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateQueryPage(includeProvenance: false));

    public Task<DatabaseQueryPage> QueryWithProvenanceAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateQueryPage(includeProvenance: true));

    public async Task<DatabaseTablePage> ReadQueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        _ = sourceSql;
        var page = await ReadTableAsync(
            driverId,
            connectionString,
            tunnel,
            new DatabaseTableDescriptor("deployments", DatabaseTableKind.Table),
            query,
            cancellationToken);
        return sourceColumns.Count == page.Result.Columns.Count
            ? page with { Result = page.Result with { Columns = sourceColumns } }
            : page;
    }

    public Task<long> CountQueryRowsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        IReadOnlyList<DatabaseFilterCondition> filters,
        CancellationToken cancellationToken)
    {
        _ = driverId;
        _ = connectionString;
        _ = tunnel;
        _ = sourceSql;
        _ = sourceColumns;
        cancellationToken.ThrowIfCancellationRequested();
        var count = Deployments.LongCount(row => filters.All(filter => Matches(row, filter)));
        return Task.FromResult(count);
    }

    private static DatabaseQueryPage CreateQueryPage(bool includeProvenance)
    {
        var baseObject = includeProvenance
            ? new DatabaseObjectId(null, null, "deployments")
            : null;
        return new DatabaseQueryPage(
            [
                new(
                    "id",
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    IsNullable: false,
                    IsKey: includeProvenance,
                    IsIdentity: includeProvenance,
                    IsReadOnly: includeProvenance,
                    BaseColumnName: includeProvenance ? "id" : null,
                    BaseObject: baseObject),
                new(
                    "service",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: false,
                    BaseColumnName: includeProvenance ? "service" : null,
                    BaseObject: baseObject),
                new(
                    "region",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: false,
                    BaseColumnName: includeProvenance ? "region" : null,
                    BaseObject: baseObject),
                new(
                    "status",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: true,
                    BaseColumnName: includeProvenance ? "status" : null,
                    BaseObject: baseObject),
                new(
                    "deployed_at",
                    "TEXT",
                    DatabaseValueKind.TimestampWithZone,
                    IsNullable: false,
                    BaseColumnName: includeProvenance ? "deployed_at" : null,
                    BaseObject: baseObject),
            ],
            [
                ["184", "billing-api", "eu-central-1", "healthy", "2026-08-02T21:14:09Z"],
                ["183", "billing-api", "us-east-1", "healthy", "2026-08-02T21:12:44Z"],
                ["182", "checkout-web", "eu-central-1", "rolled-back", "2026-08-02T19:03:18Z"],
                ["181", "ledger-worker", "eu-central-1", "healthy", "2026-08-02T17:40:51Z"],
                ["180", "ledger-worker", "us-east-1", null, "2026-08-02T17:39:12Z"],
                ["179", "checkout-web", "ap-south-1", "healthy", "2026-08-02T15:22:30Z"],
            ],
            Truncated: false,
            RowsAffected: 0,
            TimeSpan.FromMilliseconds(12));
    }

    public Task<DatabaseObjectDetails> GetObjectDetailsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canEdit = databaseObject.Kind == DatabaseTableKind.Table;
        return Task.FromResult(new DatabaseObjectDetails(
            databaseObject,
            DeploymentSchema,
            canEdit ? DeploymentIndexes : [],
            canEdit,
            canEdit ? null : "Views are read-only."));
    }

    public Task<DatabaseTablePage> ReadTableAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);

        IEnumerable<QaDeployment> rows = string.Equals(table.Name, "recent_failures"
, StringComparison.Ordinal) ? Deployments.Where(row => string.Equals(row.Status, "rolled-back", StringComparison.Ordinal))
            : Deployments;
        var filteredRows = rows
            .Where(row => query.Filters.All(filter => Matches(row, filter)))
            .ToArray();
        var ordered = Sort(filteredRows, query.Sorts);
        var window = ordered
            .Skip(query.Offset)
            .Take(query.Limit + 1)
            .ToArray();
        var hasMore = window.Length > query.Limit;
        var pageRows = window.Take(query.Limit).ToArray();
        var columns = DeploymentSchema
            .Select(column => new DatabaseColumnDescriptor(
                column.Name,
                column.DataTypeName,
                column.ValueKind,
                column.ClrTypeName,
                column.IsNullable,
                column.IsPrimaryKey,
                column.IsIdentity,
                !column.CanEdit,
                column.Name))
            .ToArray();
        var values = pageRows
            .Select(row => (IReadOnlyList<DatabaseValue>)ToValues(row))
            .ToArray();
        var displayRows = values
            .Select(row => (IReadOnlyList<string?>)[.. row.Select(value => value.IsNull ? null : value.DisplayText)])
            .ToArray();
        var result = new DatabaseQueryPage(
            columns,
            displayRows,
            hasMore,
            RowsAffected: 0,
            TimeSpan.FromMilliseconds(8),
            values);
        return Task.FromResult(new DatabaseTablePage(
            result,
            query.Offset,
            query.Limit,
            hasMore,
            filteredRows.LongLength));
    }

    public Task<DatabaseMutationResult> ApplyTableChangesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DatabaseMutationResult(
            changes.Inserts.Count,
            changes.Updates.Count,
            changes.Deletes.Count,
            Message: changes.IsEmpty ? "No changes to save." : "Changes saved."));
    }

    public string BuildTablePreviewQuery(string driverId, string tableName, int limit) =>
        $"SELECT * FROM \"{tableName}\" LIMIT {limit};";

    public string BuildInsertStatement(
        string driverId,
        DatabaseObjectDetails details,
        DatabaseInsertedRow row) =>
        $"INSERT INTO \"{details.Object.Name}\" DEFAULT VALUES;";

    public DatabaseConnectionDetails ParseConnectionDetails(
        string driverId,
        string connectionString)
    {
        if (!connectionString.Contains('=', StringComparison.Ordinal))
        {
            return new DatabaseConnectionDetails(FilePath: connectionString);
        }

        var values = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        return new DatabaseConnectionDetails(
            values.GetValueOrDefault("Host"),
            values.TryGetValue("Port", out var port)
                && int.TryParse(port, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null,
            values.GetValueOrDefault("Database"),
            values.GetValueOrDefault("Username"),
            values.GetValueOrDefault("Password"),
            Options: null);
    }

    public string BuildConnectionString(string driverId, DatabaseConnectionDetails details)
    {
        if (details.FilePath is { } filePath)
        {
            return filePath;
        }

        var pairs = new List<string>();
        Append("Host", details.Host);
        Append("Port", details.Port?.ToString(CultureInfo.InvariantCulture));
        Append("Database", details.Database);
        Append("Username", details.Username);
        Append("Password", details.Password);
        if (details.Options is { } options)
        {
            pairs.Add(options);
        }

        return string.Join(';', pairs);

        void Append(string key, string? value)
        {
            if (value is not null)
            {
                pairs.Add($"{key}={value}");
            }
        }
    }

    private static DateTimeOffset At(int hour, int minute, int second) =>
        new(2026, 8, 2, hour, minute, second, TimeSpan.Zero);

    private static DatabaseValue[] ToValues(QaDeployment row) =>
    [
        new(row.Id, DatabaseValueKind.SignedInteger, row.Id.ToString(CultureInfo.InvariantCulture)),
        new(row.Service, DatabaseValueKind.Text, row.Service),
        new(row.Region, DatabaseValueKind.Text, row.Region),
        new(row.Status, DatabaseValueKind.Text, row.Status ?? "NULL"),
        new(
            row.DeployedAt,
            DatabaseValueKind.TimestampWithZone,
            row.DeployedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
    ];

    private static bool Matches(QaDeployment row, DatabaseFilterCondition filter)
    {
        var current = ColumnValue(row, filter.ColumnName);
        if (filter.Operator == DatabaseFilterOperator.IsNull)
        {
            return current is null;
        }

        if (filter.Operator == DatabaseFilterOperator.IsNotNull)
        {
            return current is not null;
        }

        var comparison = Compare(current, filter.Value);
        var currentText = Convert.ToString(current, CultureInfo.InvariantCulture) ?? string.Empty;
        var expectedText = Convert.ToString(filter.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        return filter.Operator switch
        {
            DatabaseFilterOperator.Equal => comparison == 0,
            DatabaseFilterOperator.NotEqual => comparison != 0,
            DatabaseFilterOperator.LessThan => comparison < 0,
            DatabaseFilterOperator.LessThanOrEqual => comparison <= 0,
            DatabaseFilterOperator.GreaterThan => comparison > 0,
            DatabaseFilterOperator.GreaterThanOrEqual => comparison >= 0,
            DatabaseFilterOperator.Contains => currentText.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            DatabaseFilterOperator.StartsWith => currentText.StartsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            DatabaseFilterOperator.EndsWith => currentText.EndsWith(expectedText, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static IReadOnlyList<QaDeployment> Sort(
        IEnumerable<QaDeployment> rows,
        IReadOnlyList<DatabaseSort> sorts)
    {
        var result = rows.ToList();
        if (sorts.Count == 0)
        {
            return result;
        }

        result.Sort((left, right) =>
        {
            foreach (var sort in sorts)
            {
                var comparison = Compare(
                    ColumnValue(left, sort.ColumnName),
                    ColumnValue(right, sort.ColumnName));
                if (comparison != 0)
                {
                    return sort.Descending ? -comparison : comparison;
                }
            }

            return 0;
        });
        return result;
    }

    private static object? ColumnValue(QaDeployment row, string columnName) =>
        columnName.ToLowerInvariant() switch
        {
            "id" => row.Id,
            "service" => row.Service,
            "region" => row.Region,
            "status" => row.Status,
            "deployed_at" => row.DeployedAt,
            _ => null,
        };

    private static int Compare(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null ? right is null ? 0 : -1 : 1;
        }

        if (left is long integer
            && long.TryParse(
                Convert.ToString(right, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                out var expectedInteger))
        {
            return integer.CompareTo(expectedInteger);
        }

        if (left is DateTimeOffset timestamp
            && DateTimeOffset.TryParse(
                Convert.ToString(right, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expectedTimestamp))
        {
            return timestamp.CompareTo(expectedTimestamp);
        }

        return string.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record QaDeployment(
        long Id,
        string Service,
        string Region,
        string? Status,
        DateTimeOffset DeployedAt);
}

/// <summary>
/// Layout-only stand-in for the statistics view model: real presentation values
/// with no monitoring runtime behind them, so the panel's card layout is
/// capturable at any width.
/// </summary>
internal sealed class QaStatisticsPreview
{
    public string Title => "Statistics";

    public bool IsActive => true;

    public bool HasAttention => false;

    public bool IsAgentActive => false;

    public bool IsNotificationPulseActive => false;

    public bool IsZoomed => false;

    public bool ShowContent => true;

    public bool IsVisibleInLayout => true;

    public string StatusText => "Live · Local";

    public IBrush StatusColor => Brushes.MediumSeaGreen;

    public string ConnectionDisplayName => "Local";

    public string CpuText => "3.8%";

    public string MemoryText => "101.9 GiB";

    public string NetworkReceivedText => "4.6 MiB/s";

    public string NetworkSentText => "2.3 MiB/s";

    public string ProcessCountText => "1 180";

    public string ProcessDetailText => "Resource details available for all processes";

    public string UptimeText => "9d 5h 30m";

    public int ProcessorCountText => 16;

    public bool HasIssue => false;

    public bool ShowLoading => false;

    public bool ShowTerminalError => false;

    public string CapturedAtText => "Captured 09:30:00";

    public string IssueTitle => string.Empty;

    public string IssueMessage => string.Empty;

    public IReadOnlyList<double?> CpuHistory { get; } =
        [4.1, 3.6, 5.2, 3.9, 4.4, 3.2, 3.8, 4.9, 3.5, 3.8];

    public IReadOnlyList<double?> MemoryHistory { get; } =
        [101.2, 101.4, 101.3, 101.6, 101.5, 101.8, 101.7, 101.9];

    public IReadOnlyList<double?> NetworkReceivedHistory { get; } =
        [1.8, 2.1, 3.4, 2.7, 4.1, 2.9, 3.6, 5.2, 3.8, 4.6];

    public IReadOnlyList<double?> NetworkSentHistory { get; } =
        [0.9, 1.1, 1.7, 1.3, 2.2, 1.5, 1.8, 2.6, 1.9, 2.3];
}

/// <summary>
/// Layout-only stand-in for the process monitor view model, with names long
/// enough to prove the name column trims instead of overlapping its neighbours.
/// </summary>
internal sealed class QaProcessMonitorPreview
{
    public sealed record Row(
        int ProcessId,
        string Name,
        string Cpu,
        string Memory,
        string Started,
        bool IsAsura)
    {
        public string AccessibleSummary =>
            $"PID {ProcessId}, {Name}, CPU {Cpu}, memory {Memory}, started {Started}.";
    }

    public string Title => "Process Monitor";

    public bool IsActive => true;

    public bool HasAttention => false;

    public bool IsAgentActive => false;

    public bool IsNotificationPulseActive => false;

    public bool IsZoomed => false;

    public bool IsVisibleInLayout => true;

    public bool ShowContent => true;

    public bool ShowLoading => false;

    public bool ShowTerminalError => false;

    public bool ShowInlineIssue => false;

    public string StatusText => "Live · Local";

    public IBrush StatusColor => Brushes.MediumSeaGreen;

    public string ConnectionDisplayName => "Local";

    public string Filter => string.Empty;

    public bool IsSortingByCpu => true;

    public bool IsSortingByMemory => false;

    public bool IsSortingByName => false;

    public bool IsSortingByProcessId => false;

    public bool IsSortDescending => true;

    public string IssueTitle => string.Empty;

    public string IssueMessage => string.Empty;

    public string ShowingText => "250 processes";

    public string CapturedAtText => "Captured 18:30:53";

    public IReadOnlyList<Row> Processes { get; } =
    [
        new(96971, "Asura", "0.8%", "562.4 MiB", "03.08.2026 18:26", true),
        new(61335, "Brave Browser Helper (Renderer)", "0.2%", "214.5 MiB", "01.08.2026 22:55", false),
        new(37330, "AMSUIPaymentsViewService_macOS", "0.0%", "15.0 MiB", "31.07.2026 14:09", false),
        new(49345, "BackgroundTaskManagementAgent", "0.0%", "28.1 MiB", "01.08.2026 22:46", false),
        new(51129, "sysmond", "0.2%", "5.3 MiB", "01.08.2026 22:48", false),
        new(17880, "Signal", "0.0%", "269.1 MiB", "31.07.2026 11:39", false),
        new(908, "bridge-gui", "0.0%", "90.7 MiB", "25.07.2026 12:52", false),
    ];

    public Row? SelectedProcess { get; set; }
}

/// <summary>
/// A Redis server with enough in it to photograph: keys of every type the panel
/// renders differently, a search index, and a subscription that has already
/// received traffic. It answers from memory and opens no socket.
/// </summary>
internal sealed class QaRedisPanelSessionFactory : IRedisPanelSessionFactory
{
    public Task<IRedisPanelSession> OpenAsync(
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        Task.FromResult<IRedisPanelSession>(new QaRedisPanelSession());
}

internal sealed class QaRedisPanelSession : IRedisPanelSession
{
    private static readonly RedisKeySummary[] Catalog =
    [
        Summary("session:9f3c1a:profile", "hash", TimeSpan.FromMinutes(43), 2_184),
        Summary("session:9f3c1a:cart", "list", TimeSpan.FromMinutes(43), 964),
        Summary("feature:checkout-v2", "string", null, 128),
        Summary("leaderboard:weekly", "zset", TimeSpan.FromHours(19), 41_920),
        Summary("presence:online", "set", null, 8_744),
        Summary("events:orders", "stream", null, 262_144),
        Summary("catalog:sku:44192", "json", null, 3_072),
        Summary("metrics:cpu:api-1", "timeseries", null, 131_072),
    ];

    public RedisServerFacts Facts { get; } = new(
        "7.4.1",
        "RESP3",
        RedisTopologyKind.Standalone,
        RedisLogicalDatabaseMode.Selectable,
        SelectedDatabase: 0,
        ConfiguredDatabaseCount: 16,
        SearchAvailable: true,
        JsonAvailable: true,
        TimeSeriesAvailable: true,
        ShardedPubSubAvailable: false);

    public event EventHandler<RedisPubSubMessage>? MessageReceived;

    public Task SelectDatabaseAsync(int database, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<RedisScanPage> ScanKeysAsync(
        string pattern,
        string? cursor,
        int count,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RedisScanPage(Catalog, NextCursor: null, IsComplete: true));

    public Task<RedisKeySnapshot> ReadKeyAsync(
        RedisKeyReference key,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        var summary = Catalog.FirstOrDefault(candidate => string.Equals(candidate.Key.DisplayName, key.DisplayName, StringComparison.Ordinal))
            ?? Catalog[0];
        if (string.Equals(summary.Type, "json", StringComparison.Ordinal))
        {
            return Task.FromResult(new RedisKeySnapshot(
                summary,
                Length: 1,
                [new("$", null, "{\n  \"sku\": \"44192\",\n  \"title\": \"Field notebook\",\n  \"price\": 1800\n}")],
                Truncated: false));
        }

        if (string.Equals(summary.Type, "list", StringComparison.Ordinal))
        {
            return Task.FromResult(new RedisKeySnapshot(
                summary,
                Length: 3,
                [
                    new("0", null, "queued"),
                    new("1", null, "picking"),
                    new("2", null, "shipped"),
                ],
                Truncated: false));
        }

        return Task.FromResult(new RedisKeySnapshot(
            summary,
            Length: 4,
            [
                new("id", "id", "9f3c1a"),
                new("email", "email", "ops@asura.dev"),
                new("plan", "plan", "team"),
                new("last_seen", "last_seen", "2026-08-13T09:41:22Z"),
            ],
            Truncated: true,
            Limitation: "Showing the first 4 of 128 entries; raise the page bound to read further."));
    }

    public Task SetStringAsync(RedisKeyReference key, string value, TimeSpan? expiry, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SetHashFieldAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AppendListValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SetListValueAsync(RedisKeyReference key, long index, string value, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AddSetValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AddSortedSetValueAsync(RedisKeyReference key, string value, double score, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AddStreamEntryAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SetJsonAsync(RedisKeyReference key, string json, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AddTimeSeriesSampleAsync(RedisKeyReference key, double value, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> DeleteKeyAsync(RedisKeyReference key, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<RedisEntryRemovalOutcome> RemoveEntryAsync(
        RedisKeyReference key,
        string type,
        RedisValueEntry entry,
        CancellationToken cancellationToken) =>
        Task.FromResult(RedisEntryRemovalOutcome.Removed);
    public Task SetExpiryAsync(RedisKeyReference key, TimeSpan? expiry, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken)
    {
        var receivedAt = QaData.Now;
        foreach (var payload in new[]
                 {
                     "{\"order\":\"A-4471\",\"state\":\"paid\"}",
                     "{\"order\":\"A-4472\",\"state\":\"picking\"}",
                     "{\"order\":\"A-4473\",\"state\":\"shipped\"}",
                 })
        {
            MessageReceived?.Invoke(
                this,
                new RedisPubSubMessage(subscription, subscription.Name, payload, receivedAt));
        }

        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<long> PublishAsync(string channel, string payload, bool sharded, CancellationToken cancellationToken) =>
        Task.FromResult(1L);

    public Task<IReadOnlyList<RedisSearchIndex>> ListSearchIndexesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RedisSearchIndex>>(
        [
            new("idx:catalog", "ON JSON PREFIX 1 catalog:", "sku, title, price", 18_402),
            new("idx:sessions", "ON HASH PREFIX 1 session:", "email, plan", 964),
        ]);

    public Task<RedisSearchResult> SearchAsync(
        string index,
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RedisSearchResult(
            Total: 3,
            [
                new("catalog:sku:44192", "json", "{\"title\":\"Field notebook\",\"price\":1800}"),
                new("catalog:sku:44193", "json", "{\"title\":\"Ink cartridge\",\"price\":600}"),
                new("catalog:sku:44194", "json", "{\"title\":\"Desk mat\",\"price\":4200}"),
            ],
            Truncated: false));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RedisKeySummary Summary(string name, string type, TimeSpan? ttl, long memory) =>
        new(
            new RedisKeyReference(name, System.Text.Encoding.UTF8.GetBytes(name)),
            type,
            ttl,
            memory);
}

/// <summary>The connection catalog the Redis panel reads its driver from.</summary>
internal sealed class QaRedisConnectionCatalog : IDatabaseConnectionCatalog
{
    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } = [RedisDatabase.Descriptor];

    public DatabaseConnectionDetails ParseConnectionDetails(string driverId, string connectionString) =>
        new("cache.internal", 6379);

    public string BuildConnectionString(string driverId, DatabaseConnectionDetails details) =>
        "cache.internal:6379";
}

internal sealed class QaGitRepositoryClient : IGitRepositoryClient
{
    private const string Root = "/Users/qa/projects/asura";

    // A believable ancestry: one feature branch merging back in, so the
    // history graph shows a real second lane converging at the branch point.
    private static readonly GitCommitItem[] History =
    [
        Commit("8bfacd85", "browser new tab", 42_50, "dev", "4ff4a98a"),
        Commit("4ff4a98a", "ui improvements", 3_60_00, "origin/dev", "2861c1b0"),
        Commit("2861c1b0", "terminal and browser improvements", 13 * 3600, null, "3b684070"),
        Commit("3b684070", "Merge agent tooling", 14 * 3600, null, "67ceef20", "1f3e1870"),
        Commit("67ceef20", "Implement provider-neutral file discovery", 26 * 3600, null, "e49f1870"),
        Commit("1f3e1870", "somewhat working agent", 27 * 3600, null, "e49f1870"),
        Commit("e49f1870", "redis support", 49 * 3600, null, "3edc27e0"),
        Commit("3edc27e0", "macOS wake/display-scale repair", 50 * 3600, null, "55cb86c0"),
        Commit("55cb86c0", "better tabs", 52 * 3600, null, "4b3c6b50"),
        Commit("4b3c6b50", "quick terminal improvements", 74 * 3600, null, "bcf5eec0"),
        Commit("bcf5eec0", "quick terminal properly working", 75 * 3600, null, "c13459e0"),
        Commit("c13459e0", "multiplexed terminals", 76 * 3600, "v0.8.0"),
    ];

    public ValueTask<GitResult<GitRepositoryHandle>> OpenRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitRepositoryHandle>>(
            new GitResult<GitRepositoryHandle>.Success(new GitRepositoryHandle(connection, Root)));

    public ValueTask<GitResult<GitUnit>> TrustRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken) => Record($"trust {path}");

    public ValueTask<GitResult<GitTreeListing>> ReadTreeAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitTreeListing>>(
            new GitResult<GitTreeListing>.Success(new GitTreeListing(
                path,
                path.Length == 0
                    ?
                    [
                        new GitTreeEntry("docs", IsTree: true, Size: null),
                        new GitTreeEntry("src", IsTree: true, Size: null),
                        new GitTreeEntry("tests", IsTree: true, Size: null),
                        new GitTreeEntry("Asura.slnx", IsTree: false, Size: 4210),
                        new GitTreeEntry("README.md", IsTree: false, Size: 1832),
                        new GitTreeEntry("design-qa.md", IsTree: false, Size: 20714),
                    ]
                    :
                    [
                        new GitTreeEntry("cef-runtime-packaging.md", IsTree: false, Size: 6113),
                        new GitTreeEntry("notifications.md", IsTree: false, Size: 2450),
                    ])));

    public ValueTask<GitResult<GitBlobSnapshot>> ReadBlobAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitBlobSnapshot>>(
            new GitResult<GitBlobSnapshot>.Success(new GitBlobSnapshot(
                path,
                "# Asura\n\nA connection-first shell: terminals, files, browsers,\ndatabases, Docker, and Git panels over one process boundary.\n",
                IsBinary: false,
                IsTruncated: false)));

    public ValueTask<GitResult<GitDirectoryListing>> ListDirectoriesAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitDirectoryListing>>(
            new GitResult<GitDirectoryListing>.Success(new GitDirectoryListing(
                "/Users/qa/projects",
                [
                    new GitDirectoryEntry("asura", IsRepository: true),
                    new GitDirectoryEntry("asura-wt", IsRepository: true),
                    new GitDirectoryEntry("archive", IsRepository: false),
                    new GitDirectoryEntry("designs", IsRepository: false),
                    new GitDirectoryEntry("experiments", IsRepository: false),
                ])));

    public ValueTask<GitResult<GitRepositorySnapshot>> ReadSnapshotAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken)
    {
        var snapshot = new GitRepositorySnapshot(
            generation,
            new GitHeadState("dev", History[0].Sha, "origin/dev", Ahead: 1, Behind: 0, IsDetached: false),
            [
                new GitFileChange("src/Asura.App/ViewModels/GitRuntimePanelViewModel.cs", null, GitChangeKind.Modified, GitChangeArea.Unstaged),
                new GitFileChange("src/Asura.App/Views/RuntimePanels/GitRuntimePanelView.axaml", null, GitChangeKind.Modified, GitChangeArea.Unstaged),
                new GitFileChange("docs/git-panel.md", null, GitChangeKind.Untracked, GitChangeArea.Unstaged),
            ],
            [
                new GitFileChange("src/Asura.Application/GitRepositoryModels.cs", null, GitChangeKind.Added, GitChangeArea.Staged),
                new GitFileChange("src/Asura.Git/GitRepositoryClient.cs", "src/Asura.Git/GitCliRepository.cs", GitChangeKind.Renamed, GitChangeArea.Staged),
            ],
            [
                new GitRefItem("refs/heads/dev", "dev", GitRefKind.LocalBranch, History[0].Sha, IsCurrent: true, "origin/dev", Ahead: 1, Behind: 0),
                new GitRefItem("refs/heads/main", "main", GitRefKind.LocalBranch, History[2].Sha, IsCurrent: false, "origin/main", Ahead: 0, Behind: 0),
                new GitRefItem("refs/heads/codex/cef-osr-migration", "codex/cef-osr-migration", GitRefKind.LocalBranch, History[3].Sha, IsCurrent: false),
                new GitRefItem("refs/remotes/origin/dev", "origin/dev", GitRefKind.RemoteBranch, History[1].Sha, IsCurrent: false),
                new GitRefItem("refs/remotes/origin/main", "origin/main", GitRefKind.RemoteBranch, History[2].Sha, IsCurrent: false),
                new GitRefItem("refs/tags/v0.8.0", "v0.8.0", GitRefKind.Tag, History[^1].Sha, IsCurrent: false),
            ],
            [new GitRemoteItem("origin", "git@github.com:terion/asura.git")],
            [new GitStashItem("stash@{0}", "WIP on dev: diff viewer polish")],
            [
                new GitWorktreeItem(Root, "dev", History[0].Sha, IsMain: true),
                new GitWorktreeItem("/Users/qa/projects/asura-wt", "codex/cef-osr-migration", History[3].Sha, IsMain: false),
            ],
            [],
            new DateTimeOffset(2026, 8, 18, 11, 42, 0, TimeSpan.Zero));
        return ValueTask.FromResult<GitResult<GitRepositorySnapshot>>(
            new GitResult<GitRepositorySnapshot>.Success(snapshot));
    }

    public ValueTask<GitResult<GitWorkingSet>> ReadWorkingSetAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitWorkingSet>>(
            new GitResult<GitWorkingSet>.Success(new GitWorkingSet(
                generation,
                new GitHeadState("dev", History[0].Sha, "origin/dev", Ahead: 1, Behind: 0, IsDetached: false),
                [
                    new GitFileChange("src/Asura.App/ViewModels/GitRuntimePanelViewModel.cs", null, GitChangeKind.Modified, GitChangeArea.Unstaged),
                    new GitFileChange("src/Asura.App/Views/RuntimePanels/GitRuntimePanelView.axaml", null, GitChangeKind.Modified, GitChangeArea.Unstaged),
                    new GitFileChange("docs/git-panel.md", null, GitChangeKind.Untracked, GitChangeArea.Unstaged),
                ],
                [
                    new GitFileChange("src/Asura.Application/GitRepositoryModels.cs", null, GitChangeKind.Added, GitChangeArea.Staged),
                    new GitFileChange("src/Asura.Git/GitRepositoryClient.cs", "src/Asura.Git/GitCliRepository.cs", GitChangeKind.Renamed, GitChangeArea.Staged),
                ])));

    public ValueTask<GitResult<GitCommitPage>> ReadCommitPageAsync(
        GitRepositoryHandle repository,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitCommitPage>>(
            new GitResult<GitCommitPage>.Success(new GitCommitPage(History, offset, HasMore: false)));

    public ValueTask<GitResult<GitCommitDetail>> ReadCommitDetailAsync(
        GitRepositoryHandle repository,
        string sha,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitCommitDetail>>(
            new GitResult<GitCommitDetail>.Success(new GitCommitDetail(
                History[0],
                "The browser panel now opens fresh tabs beside the active one and keeps\nthe session alive across layout changes.",
                "terion",
                History[0].AuthoredAt,
                [
                    new GitFileChange("src/Asura.Browser/BrowserEngineRuntime.cs", null, GitChangeKind.Modified, GitChangeArea.Staged),
                    new GitFileChange("docs/cef-runtime-packaging.md", null, GitChangeKind.Modified, GitChangeArea.Staged),
                    new GitFileChange("tests/Asura.Browser.Tests/BrowserEngineRuntimeTests.cs", null, GitChangeKind.Modified, GitChangeArea.Staged),
                ])));

    public ValueTask<GitResult<GitDiffDocument>> ReadDiffAsync(
        GitRepositoryHandle repository,
        GitDiffRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitDiffDocument>>(
            new GitResult<GitDiffDocument>.Success(new GitDiffDocument(
                request.Path,
                request.OriginalPath,
                IsBinary: false,
                IsTruncated: false,
                [
                    new GitDiffHunk(
                        "@@ -12,7 +12,7 @@ public static class BrowserEngineRuntime",
                        [
                            new GitDiffLine(GitDiffLineKind.Context, "{", 12, 12),
                            new GitDiffLine(GitDiffLineKind.Context, "    private const string ExpectedCefVersion = \"150.0.9\";", 13, 13),
                            new GitDiffLine(GitDiffLineKind.Removed, "    private const string ExpectedShimVersion = \"0.8.0-asura.3\";", 14, null),
                            new GitDiffLine(GitDiffLineKind.Added, "    private const string ExpectedShimVersion = \"0.8.0-asura.4\";", null, 14),
                            new GitDiffLine(GitDiffLineKind.Context, "    private static readonly object StateGate = new();", 15, 15),
                        ]),
                    new GitDiffHunk(
                        "@@ -40,6 +40,9 @@ public static class BrowserEngineRuntime",
                        [
                            new GitDiffLine(GitDiffLineKind.Context, "    public static void Initialize()", 40, 40),
                            new GitDiffLine(GitDiffLineKind.Context, "    {", 41, 41),
                            new GitDiffLine(GitDiffLineKind.Added, "        // A fresh tab adopts the active session's profile before", null, 42),
                            new GitDiffLine(GitDiffLineKind.Added, "        // the renderer spawns, so cookies follow the workspace.", null, 43),
                            new GitDiffLine(GitDiffLineKind.Added, "        AdoptActiveProfile();", null, 44),
                            new GitDiffLine(GitDiffLineKind.Context, "        EnsureRuntime();", 42, 45),
                        ]),
                ])));

    public ValueTask<GitResult<GitUnit>> StageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) => Success();

    public ValueTask<GitResult<GitUnit>> UnstageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) => Success();

    public ValueTask<GitResult<GitUnit>> DiscardAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<GitFileChange> changes,
        CancellationToken cancellationToken) => Success();

    public ValueTask<GitResult<GitUnit>> CommitAsync(
        GitRepositoryHandle repository,
        GitCommitRequest request,
        CancellationToken cancellationToken) => Success();

    public ValueTask<GitResult<GitUnit>> CheckoutBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"checkout {name}");

    public ValueTask<GitResult<GitUnit>> CreateBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"create-branch {name}");

    public ValueTask<GitResult<GitUnit>> RenameBranchAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        CancellationToken cancellationToken) => Record($"rename-branch {oldName} {newName}");

    public ValueTask<GitResult<GitUnit>> DeleteBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"delete-branch {name}");

    public ValueTask<GitResult<GitUnit>> MergeBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"merge {name}");

    public ValueTask<GitResult<GitUnit>> CreateTagAsync(
        GitRepositoryHandle repository,
        string name,
        string? message,
        string? revision,
        CancellationToken cancellationToken) => Record($"create-tag {name}");

    public ValueTask<GitResult<GitUnit>> DeleteTagAsync(
        GitRepositoryHandle repository,
        string name,
        IReadOnlyList<string> alsoOnRemotes,
        CancellationToken cancellationToken) => Record($"delete-tag {name}");

    public ValueTask<GitResult<GitUnit>> AddRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        string url,
        CancellationToken cancellationToken) => Record($"add-remote {name}");

    public ValueTask<GitResult<GitUnit>> EditRemoteAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        string url,
        CancellationToken cancellationToken) => Record($"edit-remote {oldName} {newName}");

    public ValueTask<GitResult<GitUnit>> RemoveRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"remove-remote {name}");

    public ValueTask<GitResult<GitUnit>> FetchRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"fetch {name}");

    public ValueTask<GitResult<GitUnit>> WorktreeAddAsync(
        GitRepositoryHandle repository,
        string path,
        string branch,
        CancellationToken cancellationToken) => Record($"worktree-add {path} {branch}");

    public ValueTask<GitResult<GitUnit>> FastForwardAsync(
        GitRepositoryHandle repository,
        string branch,
        string upstream,
        bool isCurrent,
        CancellationToken cancellationToken) => Record($"fast-forward {branch} {upstream}");

    public ValueTask<GitResult<GitUnit>> PushBranchAsync(
        GitRepositoryHandle repository,
        string remote,
        string branch,
        CancellationToken cancellationToken) => Record($"push-branch {remote} {branch}");

    public ValueTask<GitResult<GitUnit>> RebaseAsync(
        GitRepositoryHandle repository,
        string onto,
        CancellationToken cancellationToken) => Record($"rebase {onto}");

    public ValueTask<GitResult<GitUnit>> PullAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken) => Record("pull");

    public ValueTask<GitResult<GitUnit>> PushAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken) => Record("push");

    public ValueTask<GitResult<GitUnit>> StashPushAsync(
        GitRepositoryHandle repository,
        string? message,
        CancellationToken cancellationToken) => Record("stash-push");

    public ValueTask<GitResult<GitUnit>> StashApplyAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken) => Record($"stash-apply {reference}");

    public ValueTask<GitResult<GitUnit>> StashPopAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken) => Record($"stash-pop {reference}");

    public ValueTask<GitResult<GitUnit>> StashDropAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken) => Record($"stash-drop {reference}");

    /// <summary>The ref operations the harness has been asked to run.</summary>
    public List<string> RefOperations { get; } = [];

    private ValueTask<GitResult<GitUnit>> Record(string operation)
    {
        RefOperations.Add(operation);
        return Success();
    }

    private static GitCommitItem Commit(
        string shortSha,
        string subject,
        long ageSeconds,
        string? refName,
        params string[] parentShortShas) =>
        new(
            FullSha(shortSha),
            shortSha,
            [.. parentShortShas.Select(FullSha)],
            "terion",
            "mail@terion.name",
            new DateTimeOffset(2026, 8, 18, 11, 42, 0, TimeSpan.Zero) - TimeSpan.FromSeconds(ageSeconds),
            subject,
            refName is null ? [] : [refName]);

    private static string FullSha(string shortSha) =>
        shortSha + "00000000000000000000000000000000";

    private static ValueTask<GitResult<GitUnit>> Success() =>
        ValueTask.FromResult<GitResult<GitUnit>>(new GitResult<GitUnit>.Success(GitUnit.Value));
}
