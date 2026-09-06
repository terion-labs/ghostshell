using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files;

/// <summary>
/// Atomically projects durable file-provider profiles into live adapters. An adapter generation is
/// retired only after its in-flight operations and queued transfers release their leases.
/// </summary>
public sealed class CatalogFileProviderRuntime :
    IFilePanelClient,
    IFileTransferQueueClient,
    IFileProviderProfileRuntime,
    IFileProviderHostKeyRepair,
    IFileContentSource
{
    private readonly object _gate = new();
    private readonly IDefinitionCatalog _catalog;
    private readonly FileProviderAdapterFactory _factory;
    private readonly IConnectionSecurityRuntime? _connectionSecurityRuntime;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<FilePanelTransferId, TransferRoute> _transferRoutes = [];
    private readonly Dictionary<FilePanelTransferId, FilePanelTransferSnapshot> _transferSnapshots = [];
    private readonly PreviewContentCache? _contentCache;
    private ProviderGeneration _active;
    private IReadOnlyList<FileProviderRuntimeDiagnostic> _diagnostics = [];
    private bool _disposed;

    public CatalogFileProviderRuntime(
        IDefinitionCatalog catalog,
        ISecretVault secretVault,
        ISshHostKeyTrustStore knownHosts,
        IConnectionSecurityRuntime? connectionSecurityRuntime = null,
        IConnectionRuntime? connectionRuntime = null,
        PreviewContentCache? contentCache = null,
        IWorkspaceNetworkConnector? networkConnector = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _contentCache = contentCache;
        _factory = new FileProviderAdapterFactory(
            secretVault ?? throw new ArgumentNullException(nameof(secretVault)),
            knownHosts ?? throw new ArgumentNullException(nameof(knownHosts)),
            connectionRuntime,
            networkConnector);
        _connectionSecurityRuntime = connectionSecurityRuntime;
        _active = CreateBuiltInGeneration();
        Attach(_active);
        _catalog.Changed += OnCatalogChanged;
        QueueRefresh(_catalog.Snapshot);
    }

    public event EventHandler? ProfilesChanged;

    public event EventHandler? TransfersChanged;

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles
    {
        get
        {
            lock (_gate)
            {
                return _active.Client.Profiles;
            }
        }
    }

    public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics;
            }
        }
    }

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_transferSnapshots.Values
                    .OrderByDescending(item => item.QueuedAt)
                    .ToArray());
            }
        }
    }

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync((client, token) => client.ListAsync(request, token), cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken) =>
        UseActiveStream(
            (client, token) => client.SearchAsync(request, token),
            cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelChange>> WatchAsync(
        FilePanelWatchRequest request,
        CancellationToken cancellationToken) =>
        UseActiveStream(
            (client, token) => client.WatchAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken) =>
        UseActiveAsync((client, token) => client.StatAsync(location, token), cancellationToken);

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync((client, token) => client.PreviewAsync(request, token), cancellationToken);

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync((client, token) => client.WriteTextAsync(request, token), cancellationToken);

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync((client, token) => client.CopyAsync(request, token), cancellationToken);

    /// <summary>
    /// Opening content runs against the generation that is active when it
    /// starts, exactly like every other operation here: a profile reload
    /// mid-download must not swap the provider under a half-fetched file.
    /// </summary>
    public ValueTask<FilePanelResult<FilePreviewContent>> OpenContentAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken) =>
        UseActiveAsync(
            (client, token) => client.OpenContentAsync(location, maximumBytes, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync(
            (client, token) => client.CreateDirectoryAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync((client, token) => client.RenameAsync(request, token), cancellationToken);

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync((client, token) => client.DeleteAsync(request, token), cancellationToken);

    public ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync(
            (client, token) => client.GetAccessControlAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken) =>
        UseActiveAsync(
            (client, token) => client.SetAccessControlAsync(request, token),
            cancellationToken);

    public async ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lease = AcquireActive();
        FilePanelResult<FilePanelTransferSnapshot> result;
        try
        {
            result = await lease.Generation.Client.EnqueueAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (!result.IsSuccess)
        {
            lease.Dispose();
            return result;
        }

        TrackTransfer(result.Value!, lease);
        return result;
    }

    public ValueTask<FilePanelResult<Unit>> CancelAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        TransferRoute? route;
        lock (_gate)
        {
            _transferRoutes.TryGetValue(id, out route);
        }

        return route is null
            ? ValueTask.FromResult(MissingTransfer<Unit>())
            : route.Generation.Client.CancelAsync(id, cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        FilePanelTransferSnapshot? snapshot;
        lock (_gate)
        {
            _transferSnapshots.TryGetValue(id, out snapshot);
        }

        if (snapshot?.CanRetry != true)
        {
            return ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Failure(
                new FilePanelError(
                    FilePanelErrorCode.Conflict,
                    "file_transfer_not_retryable",
                    "Only failed or cancelled transfers can be retried.",
                    false)));
        }

        return EnqueueAsync(snapshot.Request, cancellationToken);
    }

    public async ValueTask<FileProviderTestResult> TestAsync(
        FileProviderProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        OwnedFileProviderRegistration? owned = null;
        FilePanelClient? client = null;
        try
        {
            owned = await _factory.CreateAsync(
                profile,
                ConnectionsById(_catalog.Snapshot),
                cancellationToken).ConfigureAwait(false);
            client = new FilePanelClient([owned.Registration]);
            var descriptor = client.Profiles.Single();
            var result = await client.ListAsync(
                new FilePanelListRequest(descriptor.Root, 1, null, ShowHidden: true),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? new FileProviderTestResult(
                    true,
                    "file_provider_test_succeeded",
                    $"Connected to {profile.Name} and listed its configured root.",
                    descriptor)
                : new FileProviderTestResult(
                    false,
                    result.Error!.StableCode,
                    result.Error.Message,
                    descriptor,
                    result.Error.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FileProviderTestResult(
                false,
                "file_provider_test_cancelled",
                "The provider test was cancelled.");
        }
        catch (FileProviderAdapterConfigurationException exception)
        {
            return InvalidTest(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return InvalidTest(exception.Message);
        }
        catch (IOException)
        {
            return UnavailableTest();
        }
        catch (Exception)
        {
            return UnavailableTest();
        }
        finally
        {
            client?.Dispose();
            owned?.Dispose();
        }
    }

    public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
        FileProviderProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var connection = ResolveSftpConnection(profile);
        if (connection is null)
        {
            return ValueTask.FromResult(FailedHostKeyReview(ConnectionRuntimeErrorCode.InvalidProfile));
        }

        return _connectionSecurityRuntime is null
            ? ValueTask.FromResult(FailedHostKeyReview(ConnectionRuntimeErrorCode.AdapterUnavailable))
            : _connectionSecurityRuntime.InspectSshHostKeyAsync(
                connection,
                progress: null,
                cancellationToken);
    }

    public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
        FileProviderProfile profile,
        SshHostKeyReviewId reviewId,
        SshHostKeyTrustAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var connection = ResolveSftpConnection(profile);
        if (connection is null)
        {
            return ValueTask.FromResult(FailedHostKeyReview(ConnectionRuntimeErrorCode.InvalidProfile));
        }

        return _connectionSecurityRuntime is null
            ? ValueTask.FromResult(FailedHostKeyReview(ConnectionRuntimeErrorCode.AdapterUnavailable))
            : _connectionSecurityRuntime.TrustSshHostKeyAsync(
                new SshHostKeyTrustRequest(reviewId, connection.Id, action),
                cancellationToken);
    }

    public async ValueTask ReloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await RefreshAsync(_catalog.Snapshot, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        ProviderGeneration active;
        TransferRoute[] routes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _catalog.Changed -= OnCatalogChanged;
            _lifetime.Cancel();
            active = _active;
            routes = [.. _transferRoutes.Values];
            _transferRoutes.Clear();
        }

        foreach (var route in routes)
        {
            route.Lease.Dispose();
        }

        active.Retire();
        _refreshGate.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Captures the complete current adapter generation for one hosted File Viewer session.
    /// Catalog refresh may retire that generation, but the returned binding keeps it alive and
    /// prevents an unchanged logical location from silently resolving against a replacement root.
    /// </summary>
    internal GenerationBoundFilePanelClient AcquirePanelClientBinding(
        string providerProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerProfileId);
        var lease = AcquireActive();
        try
        {
            if (!lease.Generation.Client.Profiles.Any(
                    profile => string.Equals(
                        profile.Id,
                        providerProfileId,
                        StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"File-provider profile '{providerProfileId}' is not available.",
                    nameof(providerProfileId));
            }

            return new GenerationBoundFilePanelClient(this, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private async ValueTask<T> UseActiveAsync<T>(
        Func<FilePanelClient, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var lease = AcquireActive();
        return await operation(lease.Generation.Client, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<T> UseActiveStream<T>(
        Func<FilePanelClient, CancellationToken, IAsyncEnumerable<T>> operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = AcquireActive();
        await foreach (var item in operation(
                lease.Generation.Client,
                cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private GenerationLease AcquireActive()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _active.Acquire();
        }
    }

    internal async ValueTask<FilePanelResult<FilePanelTransferSnapshot>>
        EnqueueOnGenerationAsync(
            ProviderGeneration generation,
            FilePanelTransferRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(request);
        var lease = generation.Acquire();
        FilePanelResult<FilePanelTransferSnapshot> result;
        try
        {
            result = await generation.Client.EnqueueAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (!result.IsSuccess)
        {
            lease.Dispose();
            return result;
        }

        TrackTransfer(result.Value!, lease);
        return result;
    }

    internal async ValueTask<FilePanelResult<FilePanelTransferSnapshot>>
        RetryOnGenerationAsync(
            ProviderGeneration generation,
            FilePanelTransferId id,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var lease = generation.Acquire();
        FilePanelResult<FilePanelTransferSnapshot> result;
        try
        {
            result = await generation.Client.RetryAsync(id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (!result.IsSuccess)
        {
            lease.Dispose();
            return result;
        }

        TrackTransfer(result.Value!, lease);
        return result;
    }

    private void TrackTransfer(
        FilePanelTransferSnapshot enqueued,
        GenerationLease lease)
    {
        var release = false;
        lock (_gate)
        {
            var latest = lease.Generation.Client.Transfers
                .FirstOrDefault(item => item.Id == enqueued.Id)
                ?? enqueued;
            _transferSnapshots[latest.Id] = latest;
            if (IsTerminal(latest.State))
            {
                release = true;
            }
            else
            {
                _transferRoutes[latest.Id] = new TransferRoute(lease.Generation, lease);
            }
        }

        if (release)
        {
            lease.Dispose();
        }

        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Attach(ProviderGeneration generation)
    {
        generation.Client.TransfersChanged += (_, _) => OnGenerationTransfersChanged(generation);
    }

    private void OnGenerationTransfersChanged(ProviderGeneration generation)
    {
        List<GenerationLease> completed = [];
        lock (_gate)
        {
            foreach (var snapshot in generation.Client.Transfers)
            {
                _transferSnapshots[snapshot.Id] = snapshot;
                if (IsTerminal(snapshot.State)
                    && _transferRoutes.Remove(snapshot.Id, out var route)
                    && ReferenceEquals(route.Generation, generation))
                {
                    completed.Add(route.Lease);
                }
            }
        }

        foreach (var lease in completed)
        {
            lease.Dispose();
        }

        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueRefresh(_catalog.Snapshot);
    }

    private void QueueRefresh(DefinitionCatalogSnapshot snapshot) =>
        _ = RefreshAsync(snapshot, _lifetime.Token);

    private async Task RefreshAsync(
        DefinitionCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var built = await BuildGenerationAsync(snapshot, cancellationToken)
                    .ConfigureAwait(false);
                Attach(built.Generation);
                ProviderGeneration previous;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        built.Generation.Retire();
                        return;
                    }

                    previous = _active;
                    _active = built.Generation;
                    _diagnostics = built.Diagnostics;
                }

                previous.Retire();
                ProfilesChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _diagnostics =
                [
                    new FileProviderRuntimeDiagnostic(
                        null,
                        FileProviderRuntimeDiagnosticSeverity.Error,
                        "file_provider_refresh_failed",
                        "The saved file-provider catalog could not be refreshed; the previous adapter set remains active."),
                ];
            }

            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async ValueTask<GenerationBuild> BuildGenerationAsync(
        DefinitionCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var registrations = new List<OwnedFileProviderRegistration>
        {
            CreateBuiltInHome(),
        };
        var diagnostics = new List<FileProviderRuntimeDiagnostic>();
        var connections = ConnectionsById(snapshot);
        foreach (var connection in connections.Values.Where(
                     item => item.Endpoint is ConnectionEndpoint.Ssh))
        {
            try
            {
                registrations.Add(await _factory.CreateAsync(
                    ConnectionFileProviderProfiles.Create(connection),
                    connections,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DisposeAll(registrations);
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add(new FileProviderRuntimeDiagnostic(
                    ConnectionFileProviderProfiles.Id(connection.Id),
                    FileProviderRuntimeDiagnosticSeverity.Error,
                    "connection_file_provider_materialization_failed",
                    SafeConfigurationMessage(exception)));
            }
        }

        foreach (var stored in snapshot.FileProviderProfiles)
        {
            if (string.Equals(stored.Value.Id.Value, "builtin.files.home"
, StringComparison.Ordinal) || stored.Value.Id.Value.StartsWith(
                    "builtin.files.connection.",
                    StringComparison.Ordinal))
            {
                diagnostics.Add(new FileProviderRuntimeDiagnostic(
                    stored.Value.Id,
                    FileProviderRuntimeDiagnosticSeverity.Error,
                    "file_provider_id_reserved",
                    "The profile ID 'builtin.files.home' is reserved for the built-in Home provider."));
                continue;
            }

            try
            {
                registrations.Add(await _factory.CreateAsync(
                    stored.Value,
                    connections,
                    cancellationToken).ConfigureAwait(false));
                AddPolicyDiagnostics(stored.Value, diagnostics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DisposeAll(registrations);
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add(new FileProviderRuntimeDiagnostic(
                    stored.Value.Id,
                    FileProviderRuntimeDiagnosticSeverity.Error,
                    "file_provider_materialization_failed",
                    SafeConfigurationMessage(exception)));
            }
        }

        try
        {
            var generation = new ProviderGeneration(registrations, _contentCache);
            return new GenerationBuild(generation, Array.AsReadOnly(diagnostics.ToArray()));
        }
        catch
        {
            DisposeAll(registrations);
            throw;
        }
    }

    private ProviderGeneration CreateBuiltInGeneration() =>
        new([CreateBuiltInHome()], _contentCache);

    private static OwnedFileProviderRegistration CreateBuiltInHome()
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homePath) || !Directory.Exists(homePath))
        {
            homePath = AppContext.BaseDirectory;
        }

        // The filesystem's own root, not the user's folder: a file browser that
        // cannot leave one directory is not a file browser. It still opens at
        // home, because that is where a person's files are.
        var rootPath = Path.GetPathRoot(Path.GetFullPath(homePath));
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            rootPath = homePath;
        }

        var provider = LocalFileProvider.CreateForCurrentPlatform(new LocalFileProviderOptions(
            new FileProviderProfileId("builtin.files.home"),
            new FileAuthority("local"),
            rootPath));
        var registration = new FileProviderRegistration(
            "Local",
            OperatingSystem.IsWindows() ? FileProviderFamily.Windows : FileProviderFamily.Posix,
            provider,
            new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root),
            OperatingSystem.IsWindows()
                ? FilePanelCapability.None
                : FilePanelCapability.GovernedCreateDirectory
                    | FilePanelCapability.GovernedDelete
                    | FilePanelCapability.GovernedRename,
            LocalStart(provider, rootPath, homePath));
        return new OwnedFileProviderRegistration(
            new GhostShell.Core.FileProviderProfileId("builtin.files.home"),
            registration,
            []);
    }

    /// <summary>
    /// The home folder as a location inside the whole-filesystem provider.
    /// </summary>
    private static FileLocation LocalStart(
        LocalFileProvider provider,
        string rootPath,
        string homePath)
    {
        var path = FilePath.Root;
        var relative = Path.GetRelativePath(rootPath, homePath);
        if (relative is not ("." or ".."))
        {
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Append(new FilePathSegment(segment));
            }
        }

        return new FileLocation(provider.ProfileId, provider.Authority, path);
    }

    private static IReadOnlyDictionary<ConnectionId, ConnectionProfile> ConnectionsById(
        DefinitionCatalogSnapshot snapshot) =>
        snapshot.Connections.ToDictionary(item => item.Value.Id, item => item.Value);

    private ConnectionProfile? ResolveSftpConnection(FileProviderProfile profile)
    {
        if (profile.Configuration is not FileProviderConfiguration.Sftp sftp)
        {
            return null;
        }

        return _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == sftp.ConnectionId && item.Endpoint is ConnectionEndpoint.Ssh);
    }

    private static ConnectionRuntimeResult<SshHostKeyReview> FailedHostKeyReview(
        ConnectionRuntimeErrorCode code) =>
        ConnectionRuntimeResult<SshHostKeyReview>.Fail(ConnectionRuntimeError.Create(code));

    private static void AddPolicyDiagnostics(
        FileProviderProfile profile,
        ICollection<FileProviderRuntimeDiagnostic> diagnostics)
    {
        if (profile.Configuration is FileProviderConfiguration.Ftp
            {
                Security: FtpSecurityMode.Plaintext,
            })
        {
            diagnostics.Add(new FileProviderRuntimeDiagnostic(
                profile.Id,
                FileProviderRuntimeDiagnosticSeverity.Warning,
                "ftp_plaintext_transport",
                "FTP credentials and file contents are transmitted without TLS."));
        }

        if (profile.Configuration is FileProviderConfiguration.S3
            {
                RootPrefix.Length: > 0,
            })
        {
            diagnostics.Add(new FileProviderRuntimeDiagnostic(
                profile.Id,
                FileProviderRuntimeDiagnosticSeverity.Information,
                "s3_initial_prefix",
                "The S3 root prefix selects the initial browser location; bucket-level access is governed by the credential policy."));
        }
    }

    private static string SafeConfigurationMessage(Exception exception) => exception switch
    {
        FileProviderAdapterConfigurationException => exception.Message,
        ArgumentException => exception.Message,
        DirectoryNotFoundException => "The configured local root directory does not exist.",
        UnauthorizedAccessException => "The configured provider root is not accessible.",
        _ => "The file-provider adapter could not be materialized.",
    };

    private static FileProviderTestResult InvalidTest(string message) => new(
        false,
        "file_provider_configuration_invalid",
        message);

    private static FileProviderTestResult UnavailableTest() => new(
        false,
        "file_provider_unavailable",
        "The provider could not be reached or initialized. Review its endpoint and credentials.");

    private static FilePanelResult<T> MissingTransfer<T>() => FilePanelResult<T>.Failure(
        new FilePanelError(
            FilePanelErrorCode.NotFound,
            "file_transfer_not_found",
            "The requested transfer is no longer active.",
            false));

    private static bool IsTerminal(FilePanelTransferState state) => state is
        FilePanelTransferState.Completed
        or FilePanelTransferState.Failed
        or FilePanelTransferState.Cancelled
        or FilePanelTransferState.Skipped;

    private static void DisposeAll(IEnumerable<OwnedFileProviderRegistration> registrations)
    {
        foreach (var registration in registrations.Reverse())
        {
            registration.Dispose();
        }
    }

    private sealed record GenerationBuild(
        ProviderGeneration Generation,
        IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics);

    private sealed record TransferRoute(
        ProviderGeneration Generation,
        GenerationLease Lease);
}

internal sealed class ProviderGeneration
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<OwnedFileProviderRegistration> _owned;
    private int _leases = 1;
    private bool _retired;
    private bool _disposed;

    public ProviderGeneration(
        IReadOnlyList<OwnedFileProviderRegistration> registrations,
        PreviewContentCache? contentCache = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _owned = registrations;
        Client = new FilePanelClient(
            registrations.Select(item => item.Registration),
            TimeProvider.System,
            contentCache);
    }

    public FilePanelClient Client { get; }

    public GenerationLease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked
            {
                _leases++;
            }

            return new GenerationLease(this);
        }
    }

    public void Retire()
    {
        lock (_gate)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
        }

        Release();
    }

    public void Release()
    {
        var shouldDispose = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _leases--;
            shouldDispose = _retired && _leases == 0;
            _disposed = shouldDispose;
        }

        if (!shouldDispose)
        {
            return;
        }

        Client.Dispose();
        foreach (var registration in _owned.Reverse())
        {
            registration.Dispose();
        }
    }
}

internal sealed class GenerationLease(ProviderGeneration generation) : IDisposable
{
    private ProviderGeneration? _generation = generation;

    public ProviderGeneration Generation => _generation
        ?? throw new ObjectDisposedException(nameof(GenerationLease));

    public void Dispose() => Interlocked.Exchange(ref _generation, null)?.Release();
}

/// <summary>
/// Session-lifetime facade over one provider generation. Each operation takes a short additional
/// lease so disposing a session cannot tear down an adapter underneath an in-flight provider call.
/// </summary>
internal sealed class GenerationBoundFilePanelClient :
    IFilePanelClient,
    IFileTransferQueueClient,
    IFileContentSource,
    IDisposable
{
    private readonly object _gate = new();
    private readonly CatalogFileProviderRuntime _runtime;
    private GenerationLease? _sessionLease;

    public GenerationBoundFilePanelClient(
        CatalogFileProviderRuntime runtime,
        GenerationLease sessionLease)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _sessionLease = sessionLease
            ?? throw new ArgumentNullException(nameof(sessionLease));
        Profiles = Array.AsReadOnly(
            sessionLease.Generation.Client.Profiles.ToArray());
    }

    public event EventHandler? TransfersChanged
    {
        add => _runtime.TransfersChanged += value;
        remove => _runtime.TransfersChanged -= value;
    }

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers => _runtime.Transfers;

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.ListAsync(request, token),
            cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken) =>
        UseStream(
            (client, token) => client.SearchAsync(request, token),
            cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelChange>> WatchAsync(
        FilePanelWatchRequest request,
        CancellationToken cancellationToken) =>
        UseStream(
            (client, token) => client.WatchAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.StatAsync(location, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePreviewContent>> OpenContentAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.OpenContentAsync(location, maximumBytes, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.PreviewAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.CreateDirectoryAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.RenameAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.DeleteAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.WriteTextAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.CopyAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.GetAccessControlAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken) =>
        UseAsync(
            (client, token) => client.SetAccessControlAsync(request, token),
            cancellationToken);

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UseGenerationAsync(
            (generation, token) =>
                _runtime.EnqueueOnGenerationAsync(generation, request, token),
            cancellationToken);
    }

    public ValueTask<FilePanelResult<Unit>> CancelAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _runtime.CancelAsync(id, cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken) =>
        UseGenerationAsync(
            (generation, token) =>
                _runtime.RetryOnGenerationAsync(generation, id, token),
            cancellationToken);

    public void Dispose()
    {
        GenerationLease? lease;
        lock (_gate)
        {
            lease = _sessionLease;
            _sessionLease = null;
        }

        lease?.Dispose();
    }

    private async ValueTask<T> UseAsync<T>(
        Func<FilePanelClient, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        GenerationLease operationLease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_sessionLease is null, this);
            operationLease = _sessionLease.Generation.Acquire();
        }

        using (operationLease)
        {
            return await operation(
                    operationLease.Generation.Client,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<T> UseStream<T>(
        Func<FilePanelClient, CancellationToken, IAsyncEnumerable<T>> operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GenerationLease operationLease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_sessionLease is null, this);
            operationLease = _sessionLease.Generation.Acquire();
        }

        using (operationLease)
        {
            await foreach (var item in operation(
                    operationLease.Generation.Client,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private async ValueTask<T> UseGenerationAsync<T>(
        Func<ProviderGeneration, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        GenerationLease operationLease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_sessionLease is null, this);
            operationLease = _sessionLease.Generation.Acquire();
        }

        using (operationLease)
        {
            return await operation(
                    operationLease.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_sessionLease is null, this);
        }
    }
}
