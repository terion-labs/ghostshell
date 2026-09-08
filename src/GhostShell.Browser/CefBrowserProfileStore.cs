using Exclr8Cef;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Owns the CEF request contexts behind GhostSHELL browser profiles. Durable
/// contexts use a private runtime directory and are sealed into encrypted
/// application storage after CEF shuts down. Private sessions never receive a
/// cache path and disappear with their final lease.
/// </summary>
public sealed class CefBrowserProfileStore : IBrowserProfileDataControl, IDisposable
{
    private const string LocalRoute = "local";
    private static readonly BrowserProfileStateKey EngineStateKey = new(
        new BrowserProfileSelection(
            new GhostShell.Core.BrowserProfileId(
                "builtin.browser.internal-runtime-state"),
            BrowserProfileKey.Global),
        "engine");
    private readonly object _gate = new();
    private readonly SemaphoreSlim _clearGate = new(1, 1);
    private readonly IBrowserProfileAuthenticationResolver? _authenticationResolver;
    private readonly IBrowserProfileStateStore? _stateStore;
    private readonly string? _runtimeRoot;
    private readonly Func<string?, ICefBrowserRequestContext> _createContext;
    private readonly Dictionary<ContextKey, ContextEntry> _contexts = [];
    private bool _disposed;
    private bool _contextsReleasedForShutdown;
    private readonly Dictionary<IWorkspaceNetworkConnector, (HashSet<ContextKey> Keys, Func<CancellationToken, Task> Callback)> _authenticationRouteBindings = [];
    private bool _engineShutdownCompleted;
    private Task<bool>? _shutdownSeal;

    public CefBrowserProfileStore(
        IBrowserProfileAuthenticationResolver? authenticationResolver = null)
        : this(
            authenticationResolver,
            stateStore: null,
            runtimeRoot: null,
            CefBrowserRequestContext.Create)
    {
    }

    public CefBrowserProfileStore(
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        IBrowserProfileStateStore stateStore,
        string runtimeRoot)
        : this(
            authenticationResolver,
            stateStore,
            runtimeRoot,
            CefBrowserRequestContext.Create)
    {
    }

    internal CefBrowserProfileStore(
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        Func<string?, ICefBrowserRequestContext> createContext)
        : this(authenticationResolver, null, null, createContext)
    {
    }

    internal CefBrowserProfileStore(
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        IBrowserProfileStateStore? stateStore,
        string? runtimeRoot,
        Func<string?, ICefBrowserRequestContext> createContext)
    {
        _authenticationResolver = authenticationResolver;
        _stateStore = stateStore;
        _runtimeRoot = runtimeRoot is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        _createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
    }

    public CefBrowserProfileLease AcquireLocal(BrowserProfileKey profile) =>
        AcquireLocal(BrowserProfileBinding.Legacy(profile));

    public CefBrowserProfileLease AcquireLocal(BrowserProfileBinding profile) =>
        Acquire(profile, LocalRoute, proxyEndpoint: null, proxyAuthenticationResolver: null);

    public CefBrowserProfileLease AcquireRouted(
        BrowserProfileKey profile,
        string routeIdentity,
        int socksProxyPort)
        => AcquireRouted(
            BrowserProfileBinding.Legacy(profile),
            routeIdentity,
            socksProxyPort);

    public CefBrowserProfileLease AcquireRouted(
        BrowserProfileBinding profile,
        string routeIdentity,
        int socksProxyPort,
        string? authenticationRouteIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        return Acquire(
            profile,
            RoutedRouteKey(routeIdentity),
            new Uri($"socks5://127.0.0.1:{socksProxyPort}", UriKind.Absolute),
            proxyAuthenticationResolver: null,
            authenticationRouteIdentity: authenticationRouteIdentity ?? routeIdentity);
    }

    public CefBrowserProfileLease AcquireRouted(
        BrowserProfileBinding profile,
        string routeIdentity,
        IWorkspaceNetworkConnector networkConnector,
        string? authenticationRouteIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        ArgumentNullException.ThrowIfNull(networkConnector);
        var resolver = networkConnector.LocalProxyCredentials is { } credentials
            ? new WorkspaceProxyAuthenticationResolver(
                networkConnector.BrowserProxyEndpoint,
                credentials)
            : null;
        var lease = Acquire(
            profile,
            RoutedRouteKey(routeIdentity),
            networkConnector.BrowserProxyEndpoint,
            resolver,
            networkConnector.BrowserProfileRouteIdentity is { } persistentRoute
                ? RoutedRouteKey(persistentRoute)
                : null,
            authenticationRouteIdentity: authenticationRouteIdentity,
            authenticationRouteSource: authenticationRouteIdentity is null
                ? () => networkConnector.BrowserAuthenticationRouteIdentity
                : null);
        if (authenticationRouteIdentity is null)
        {
            BindAuthenticationRoute(networkConnector, new ContextKey(profile.Selection, RouteKey(RoutedRouteKey(routeIdentity))));
        }
        return lease;
    }

    public BrowserProfileDataState ReadState(
        BrowserProfileSelection selection,
        long expectedRevision)
    {
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var matching = _contexts
                .Where(item => item.Key.Selection == selection
                    && item.Value.HasRevision(expectedRevision))
                .Select(item => item.Value)
                .ToArray();
            var storedBytes = _stateStore?.Inspect(selection).ContentBytes ?? 0;
            return new BrowserProfileDataState(
                selection,
                expectedRevision,
                matching.Length,
                matching.Sum(item => item.ActiveLeaseCount(expectedRevision)),
                storedBytes);
        }
    }

    public async ValueTask<BrowserProfileClearResult> ClearAsync(
        BrowserProfileClearRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await _clearGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            return await ClearCoreAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _clearGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var binding in _authenticationRouteBindings)
            {
                binding.Key.BrowserAuthenticationRouteChanging -= binding.Value.Callback;
            }
            _authenticationRouteBindings.Clear();
            foreach (var entry in _contexts.Values)
            {
                if (!entry.ContextReleased)
                {
                    entry.Context.Dispose();
                    entry.ContextReleased = true;
                }
            }

            _contexts.Clear();
        }
    }

    internal void Release(ContextKey key, long revision)
    {
        lock (_gate)
        {
            if (_disposed || !_contexts.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.Release(revision);
            if (entry.ActiveLeases == 0 && !entry.IsDurable)
            {
                entry.Context.Dispose();
                _contexts.Remove(key);
            }
        }
    }

    /// <summary>
    /// Seals private runtime trees left by an unclean exit before CEF can open
    /// them. Returns false without deleting anything when encrypted storage is
    /// expected but unavailable.
    /// </summary>
    public bool RecoverOrphanedRuntimeState()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtimeRoot is null)
            {
                return true;
            }

            // A process may have died during copy or after archiving but before
            // snapshot cleanup. It is never an independent recovery authority;
            // the original runtime tree or committed encrypted archive wins.
            DeleteOwnedDirectory(EngineSnapshotDirectory);

            if (Directory.Exists(EngineRestoreDirectory))
            {
                DeleteOwnedDirectory(EngineRestoreDirectory);
            }

            if (_stateStore?.IsRetentionEnabled == false)
            {
                if (Directory.Exists(_runtimeRoot))
                {
                    DeleteOwnedDirectory(_runtimeRoot);
                }

                return true;
            }

            if (_stateStore?.IsAvailable != true)
            {
                return false;
            }

            try
            {
                if (Directory.Exists(ContextsRoot))
                {
                    foreach (var entryDirectory in Directory.EnumerateDirectories(
                                 ContextsRoot))
                    {
                        ValidateRuntimeDirectory(entryDirectory);
                        BrowserProfileRuntimeRecord record;
                        try
                        {
                            record = BrowserProfileRuntimeManifest.Read(entryDirectory);
                        }
                        catch (Exception exception)
                            when (exception is FileNotFoundException
                                or EndOfStreamException
                                or InvalidDataException)
                        {
                            DeleteRuntimeEntry(entryDirectory);
                            continue;
                        }

                        if (record.Phase == BrowserProfileRuntimePhase.Preparing)
                        {
                            DeleteRuntimeEntry(entryDirectory);
                            continue;
                        }

                        var cacheDirectory = CacheDirectoryForEntry(entryDirectory);
                        if (!Directory.Exists(cacheDirectory))
                        {
                            // A previous build may have left its nested profile
                            // behind after an unclean shutdown. Seal it as-is.
                            cacheDirectory = Path.Combine(entryDirectory, "cache");
                        }

                        _stateStore.Seal(record.StateKey, cacheDirectory);
                        DeleteRuntimeEntry(entryDirectory);
                    }

                    DeleteOwnedDirectory(ContextsRoot);
                }

                if (Directory.Exists(_runtimeRoot))
                {
                    if (Directory.EnumerateFileSystemEntries(_runtimeRoot).Any())
                    {
                        _stateStore.Seal(EngineStateKey, _runtimeRoot);
                    }

                    DeleteOwnedDirectory(_runtimeRoot);
                }

                RestoreEngineStateAtomically();
                return true;
            }
            catch (Exception exception)
                when (exception is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                return false;
            }
        }
    }

    internal void ReleaseContextsForEngineShutdown()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_contextsReleasedForShutdown)
            {
                return;
            }

            foreach (var entry in _contexts.Values)
            {
                if (!entry.ContextReleased)
                {
                    entry.Context.Dispose();
                    entry.ContextReleased = true;
                }
            }

            _contextsReleasedForShutdown = true;
        }
    }

    internal Task<bool> SealRuntimeStateAfterEngineShutdownAsync(
        Func<string, string, CancellationToken, Task>? copyEngineSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engineShutdownCompleted)
            {
                return Task.FromResult(true);
            }

            if (!_contextsReleasedForShutdown)
            {
                throw new InvalidOperationException(
                    "Browser contexts must be released before their runtime state is sealed.");
            }

            return _shutdownSeal ??= SealRuntimeStateCoreAsync(copyEngineSnapshot, cancellationToken);
        }
    }

    private async Task<bool> SealRuntimeStateCoreAsync(
        Func<string, string, CancellationToken, Task>? copyEngineSnapshot,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var entry in _contexts.Values.Where(entry => entry.IsDurable))
        {
            var contextOperation = "archive-failed";
            try
            {
                if (_stateStore?.IsRetentionEnabled == false)
                {
                    DeleteRuntimeEntry(entry.EntryDirectory!);
                    continue;
                }

                if (_stateStore?.IsAvailable != true)
                {
                    succeeded = false;
                    continue;
                }

                _stateStore.Seal(entry.StateKey, entry.CacheDirectory!);
                contextOperation = "cleanup-failed";
                DeleteRuntimeEntry(entry.EntryDirectory!);
            }
            catch (Exception exception)
                when (exception is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                var reason = exception switch
                {
                    FileNotFoundException => "missing-file",
                    DirectoryNotFoundException => "missing-directory",
                    InvalidDataException => "invalid-data",
                    UnauthorizedAccessException => "access-denied",
                    IOException when (exception.HResult & 0xffff) is 11 or 32 or 33 or 35 => "file-locked",
                    IOException => "io",
                    _ => "state",
                };
                var stage = exception.Data["GhostShell.BrowserSealStage"] switch
                {
                    "open-source" => "open-source",
                    "write-content" => "write-content",
                    "open-container" => "open-container",
                    "write-metadata" => "write-metadata",
                    "write-manifest" => "write-manifest",
                    _ => "other",
                };
                var sourceCategory = exception.Data["GhostShell.BrowserSealSourceCategory"] switch
                {
                    "leveldb-lock" => "leveldb-lock",
                    "first-party-sets" => "first-party-sets",
                    "cookies" => "cookies",
                    "history" => "history",
                    _ => "other",
                };
                SecretSafeDiagnosticProjection.WriteStandardError(
                    $"browser.shutdown.context-state.{contextOperation}.{reason}.{stage}.{sourceCategory}",
                    SecretSafeDiagnosticKind.Unexpected);
                succeeded = false;
            }
        }

        if (succeeded)
        {
            var operation = "browser.shutdown.engine-state.archive-failed";
            try
            {
                if (_runtimeRoot is not null
                    && Directory.Exists(_runtimeRoot))
                {
                    if (Directory.Exists(ContextsRoot))
                    {
                        DeleteOwnedDirectory(ContextsRoot);
                    }

                    if (_stateStore?.IsRetentionEnabled == false)
                    {
                        DeleteOwnedDirectory(_runtimeRoot);
                    }
                    else if (_stateStore?.IsAvailable == true)
                    {
                        string? snapshot = null;
                        try
                        {
                            if (copyEngineSnapshot is not null)
                            {
                                snapshot = EngineSnapshotDirectory;
                                DeleteOwnedDirectory(snapshot);
                                PreparePrivateDirectory(snapshot);
                                await copyEngineSnapshot(_runtimeRoot, snapshot, cancellationToken).ConfigureAwait(false);
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            lock (_gate)
                            {
                                ObjectDisposedException.ThrowIf(_disposed, this);
                                _stateStore.Seal(EngineStateKey, snapshot ?? _runtimeRoot);
                            }
                        }
                        finally
                        {
                            if (snapshot is not null)
                            {
                                DeleteOwnedDirectory(snapshot);
                            }
                        }
                        operation = "browser.shutdown.engine-state.cleanup-failed";
                        DeleteOwnedDirectory(_runtimeRoot);
                    }
                    else
                    {
                        succeeded = false;
                    }
                }
            }
            catch (Exception exception)
                when (exception is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                var reason = exception switch
                {
                    FileNotFoundException => "missing-file",
                    DirectoryNotFoundException => "missing-directory",
                    InvalidDataException => "invalid-data",
                    UnauthorizedAccessException => "access-denied",
                    IOException when (exception.HResult & 0xffff) is 11 or 32 or 33 or 35 => "file-locked",
                    IOException => "io",
                    _ => "state",
                };
                var stage = exception.Data["GhostShell.BrowserSealStage"] switch
                {
                    "validate" => "validate",
                    "open-container" => "open-container",
                    "open-archive" => "open-archive",
                    "close-archive" => "close-archive",
                    "write-metadata" => "write-metadata",
                    "write-manifest" => "write-manifest",
                    "delete-previous" => "delete-previous",
                    "remove-unused" => "remove-unused",
                    "harden-container" => "harden-container",
                    "close-container" => "close-container",
                    "enumerate-source" => "enumerate-source",
                    "create-entry" => "create-entry",
                    "open-source" => "open-source",
                    "open-entry" => "open-entry",
                    "copy-source" => "copy-source",
                    "close-entry" => "close-entry",
                    _ => "unspecified",
                };
                var sourceCategory = exception.Data["GhostShell.BrowserSealSourceCategory"] switch
                {
                    "leveldb-lock" => "leveldb-lock",
                    "first-party-sets" => "first-party-sets",
                    "ruleset" => "ruleset",
                    "password-dictionary" => "password-dictionary",
                    "cookies" => "cookies",
                    "history" => "history",
                    "metrics" => "metrics",
                    _ => "other",
                };
                SecretSafeDiagnosticProjection.WriteStandardError(operation + "." + reason + "." + stage + "." + sourceCategory, exception);
                succeeded = false;
            }
        }

        lock (_gate)
        {
            _contexts.Clear();
            _engineShutdownCompleted = succeeded;
        }

        return succeeded;
    }

    private CefBrowserProfileLease Acquire(
        BrowserProfileBinding profile,
        string routeIdentity,
        Uri? proxyEndpoint,
        IWorkspaceProxyAuthenticationResolver? proxyAuthenticationResolver,
        string? persistentRouteIdentity = null,
        string? authenticationRouteIdentity = null,
        Func<string?>? authenticationRouteSource = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var key = new ContextKey(
            profile.Selection,
            RouteKey(routeIdentity));
        var authenticationResolver = _authenticationResolver is null
            ? null
            : new RouteAuthenticationResolver(_authenticationResolver,
                authenticationRouteSource ?? (() => authenticationRouteIdentity ?? persistentRouteIdentity ?? routeIdentity));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_contextsReleasedForShutdown)
            {
                throw new InvalidOperationException(
                    "Browser profiles cannot be acquired during engine shutdown.");
            }

            if (_contexts.TryGetValue(key, out var existing))
            {
                if (existing.ProxyEndpoint != proxyEndpoint)
                {
                    if (existing.ActiveLeases > 0 || proxyEndpoint is null)
                    {
                        throw new InvalidOperationException(
                            "The browser profile route is already active through a different proxy endpoint.");
                    }

                    existing.Ready = ConfigureProxyAsync(existing.Context, proxyEndpoint);
                    existing.ProxyEndpoint = proxyEndpoint;
                }

                existing.Acquire(profile.Revision);
                return new CefBrowserProfileLease(
                    this,
                    key,
                    existing.Context,
                    profile,
                    authenticationResolver,
                    proxyAuthenticationResolver,
                    existing.ProxyEndpoint is null
                        ? BrowserNetworkRouteKind.Local
                        : BrowserNetworkRouteKind.SshRouted,
                    existing.Ready);
            }

            var durableSelection = profile.Definition.Persistence
                == GhostShell.Core.BrowserProfilePersistence.DurableMetadata;
            var durable = durableSelection
                && _stateStore?.IsRetentionEnabled == true;
            string? entryDirectory = null;
            string? cacheDirectory = null;
            BrowserProfileStateKey? stateKey = null;
            if (durableSelection
                && _stateStore?.IsRetentionEnabled == true
                && _stateStore.IsAvailable != true)
            {
                throw new InvalidOperationException(
                    _stateStore.UnavailableReason
                    ?? "Durable browser profile storage is unavailable.");
            }

            if (durable)
            {
                if (_runtimeRoot is null)
                {
                    throw new InvalidOperationException(
                        "The durable browser runtime directory is unavailable.");
                }

                stateKey = new BrowserProfileStateKey(
                    profile.Selection,
                    RouteKey(persistentRouteIdentity ?? routeIdentity));
                entryDirectory = CreateRuntimeEntry(stateKey.Value);
                cacheDirectory = CacheDirectoryForEntry(entryDirectory);
                _stateStore!.Restore(stateKey.Value, cacheDirectory);
                PreparePrivateDirectory(cacheDirectory);
            }

            ICefBrowserRequestContext? context = null;
            try
            {
                context = _createContext(cacheDirectory);
                var ready = proxyEndpoint is { } endpoint
                    ? ConfigureProxyAsync(context, endpoint)
                    : Task.CompletedTask;

                if (entryDirectory is not null)
                {
                    BrowserProfileRuntimeManifest.MarkActive(entryDirectory);
                }

                _contexts.Add(
                    key,
                    new ContextEntry(
                        context,
                        proxyEndpoint,
                        stateKey,
                        entryDirectory,
                        cacheDirectory,
                        profile.Revision,
                        hasInitialLease: true)
                    { Ready = ready });
                return new CefBrowserProfileLease(
                    this,
                    key,
                    context,
                    profile,
                    authenticationResolver,
                    proxyAuthenticationResolver,
                    proxyEndpoint is null
                        ? BrowserNetworkRouteKind.Local
                        : BrowserNetworkRouteKind.SshRouted,
                    ready);
            }
            catch
            {
                context?.Dispose();
                if (entryDirectory is not null)
                {
                    DeleteRuntimeEntry(entryDirectory);
                }

                throw;
            }
        }
    }

    private sealed class RouteAuthenticationResolver(
        IBrowserProfileAuthenticationResolver resolver,
        Func<string?> routeIdentity) : IBrowserProfileAuthenticationResolver
    {
        public async ValueTask<BrowserAuthenticationCredentials?> ResolveAsync(
            BrowserProfileBinding profile,
            BrowserAuthenticationChallenge challenge,
            CancellationToken cancellationToken)
        {
            var authority = routeIdentity();
            if (authority is null)
            {
                return null;
            }
            var credentials = await resolver.ResolveAsync(
                profile, challenge with { RouteIdentity = authority }, cancellationToken).ConfigureAwait(false);
            return string.Equals(authority, routeIdentity(), StringComparison.Ordinal) ? credentials : null;
        }
    }

    private void BindAuthenticationRoute(IWorkspaceNetworkConnector connector, ContextKey key)
    {
        lock (_gate)
        {
            if (!_authenticationRouteBindings.TryGetValue(connector, out var binding))
            {
                HashSet<ContextKey> keys = [];
                Func<CancellationToken, Task> callback = async cancellationToken =>
                {
                    ICefBrowserRequestContext[] contexts;
                    lock (_gate)
                    {
                        contexts = [.. keys.Where(_contexts.ContainsKey).Select(key => _contexts[key])
                            .Where(entry => !entry.ContextReleased).Select(entry => entry.Context)];
                    }
                    foreach (var context in contexts)
                    {
                        await context.CloseAllConnectionsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                        await context.ClearHttpAuthCredentialsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                };
                binding = (keys, callback);
                _authenticationRouteBindings.Add(connector, binding);
                connector.BrowserAuthenticationRouteChanging += callback;
            }
            binding.Keys.Add(key);
        }
    }

    private void EnsureStoredContextsForClear(
        BrowserProfileSelection selection,
        long revision)
    {
        if (_stateStore?.IsAvailable != true || _runtimeRoot is null)
        {
            return;
        }

        foreach (var stateKey in _stateStore.ListKeys(selection))
        {
            var contextKey = new ContextKey(selection, stateKey.Route);
            if (_contexts.ContainsKey(contextKey)
                || _contexts.Values.Any(entry => entry.IsDurable && entry.StateKey == stateKey))
            {
                continue;
            }

            var entryDirectory = CreateRuntimeEntry(stateKey);
            var cacheDirectory = CacheDirectoryForEntry(entryDirectory);
            ICefBrowserRequestContext? context = null;
            try
            {
                _stateStore.Restore(stateKey, cacheDirectory);
                PreparePrivateDirectory(cacheDirectory);
                context = _createContext(cacheDirectory);
                BrowserProfileRuntimeManifest.MarkActive(entryDirectory);
                _contexts.Add(
                    contextKey,
                    new ContextEntry(
                        context,
                        proxyEndpoint: null,
                        stateKey,
                        entryDirectory,
                        cacheDirectory,
                        revision,
                        hasInitialLease: false));
                context = null;
            }
            catch
            {
                context?.Dispose();
                DeleteRuntimeEntry(entryDirectory);
                throw;
            }
        }
    }

    private async Task<BrowserProfileClearResult> ClearCoreAsync(
        BrowserProfileClearRequest request,
        CancellationToken cancellationToken)
    {
        KeyValuePair<ContextKey, ContextEntry>[] matching;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            var otherRevision = _contexts
                .Where(item => item.Key.Selection == request.Selection)
                .SelectMany(item => item.Value.ActiveRevisions)
                .Any(revision => revision != request.ExpectedRevision);
            if (otherRevision)
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.RevisionMismatch,
                    0,
                    "An open browser still owns another revision of this profile. Close it before clearing data.");
            }

            if (!request.Categories.HasFlag(BrowserProfileDataCategory.AllWebContent))
            {
                try
                {
                    EnsureStoredContextsForClear(
                        request.Selection,
                        request.ExpectedRevision);
                }
                catch (Exception exception)
                    when (exception is IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or InvalidOperationException)
                {
                    return new BrowserProfileClearResult(
                        BrowserProfileClearStatus.Failed,
                        0,
                        "The encrypted browser profile could not be opened for clearing.");
                }
            }

            matching =
            [
                .. _contexts.Where(item =>
                    item.Key.Selection == request.Selection),
            ];
            if (request.Categories.HasFlag(
                    BrowserProfileDataCategory.AllWebContent)
                && matching.Any(item => item.Value.ActiveLeases > 0))
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.InUse,
                    0,
                    "Close browser tabs using this exact profile, then reset its saved web content.");
            }

            if (request.Categories.HasFlag(BrowserProfileDataCategory.AllWebContent))
            {
                try
                {
                    foreach (var item in matching)
                    {
                        if (!item.Value.ContextReleased)
                        {
                            item.Value.Context.Dispose();
                            item.Value.ContextReleased = true;
                        }

                        if (item.Value.EntryDirectory is not null)
                        {
                            DeleteRuntimeEntry(item.Value.EntryDirectory);
                        }

                        _contexts.Remove(item.Key);
                    }

                    var deleted = _stateStore?.IsAvailable == true
                        ? _stateStore.Delete(request.Selection)
                        : 0;
                    return new BrowserProfileClearResult(
                        BrowserProfileClearStatus.Cleared,
                        deleted,
                        "The selected profile's encrypted browser state was deleted.");
                }
                catch (Exception exception)
                    when (exception is IOException
                        or InvalidDataException
                        or InvalidOperationException
                        or UnauthorizedAccessException)
                {
                    return new BrowserProfileClearResult(
                        BrowserProfileClearStatus.Failed,
                        0,
                        "The embedded browser could not clear this exact profile revision.");
                }
            }
        }

        try
        {
            foreach (var item in matching)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Categories.HasFlag(BrowserProfileDataCategory.Cookies))
                {
                    _ = await item.Value.Context.DeleteCookiesAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);
                    await item.Value.Context.FlushCookieStoreAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (request.Categories.HasFlag(
                        BrowserProfileDataCategory.HttpAuthentication))
                {
                    await item.Value.Context.ClearHttpAuthCredentialsAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);
                    await item.Value.Context.CloseAllConnectionsAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return new BrowserProfileClearResult(
                BrowserProfileClearStatus.Cleared,
                0,
                matching.Length == 0
                    ? "This exact profile revision has no browser data."
                    : ClearMessage(request.Categories));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                or ObjectDisposedException
                or TimeoutException)
        {
            return new BrowserProfileClearResult(
                BrowserProfileClearStatus.Failed,
                0,
                exception is TimeoutException
                    ? "The embedded browser did not confirm that profile data was cleared."
                    : "The embedded browser could not clear this exact profile revision.");
        }
    }

    private static string RouteKey(string routeIdentity) =>
        BrowserProfileStateKey.NormalizeRoute(routeIdentity);

    private static string RoutedRouteKey(string routeIdentity) =>
        BrowserProfileStateKey.NormalizeRoute(
            $"ssh:{BrowserProfileStateKey.NormalizeRoute(routeIdentity)}");

    private static async Task ConfigureProxyAsync(
        ICefBrowserRequestContext context,
        Uri proxyEndpoint)
    {
        foreach (var preference in
                 CefBrowserNetworkContext.RequiredPreferences(proxyEndpoint))
        {
            if (!await context.SetPreferenceAsync(preference.Key, preference.Value).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"The embedded browser rejected the required '{preference.Key}' network setting.");
            }
        }
    }

    private string ContextsRoot => Path.Combine(
        _runtimeRoot
        ?? throw new InvalidOperationException(
            "The browser runtime root is unavailable."),
        "contexts");

    private string EngineRestoreDirectory =>
        (_runtimeRoot
         ?? throw new InvalidOperationException(
             "The browser runtime root is unavailable."))
        + ".restore";

    private string EngineSnapshotDirectory =>
        _runtimeRoot + ".shutdown-snapshot";

    private void RestoreEngineStateAtomically()
    {
        if (_runtimeRoot is null || _stateStore is null)
        {
            throw new InvalidOperationException(
                "The durable browser runtime is unavailable.");
        }

        if (Directory.Exists(_runtimeRoot))
        {
            throw new InvalidOperationException(
                "The browser runtime root must be empty before engine state is restored.");
        }

        if (Directory.Exists(EngineRestoreDirectory))
        {
            DeleteOwnedDirectory(EngineRestoreDirectory);
        }

        _stateStore.Restore(EngineStateKey, EngineRestoreDirectory);
        Directory.Move(EngineRestoreDirectory, _runtimeRoot);
    }

    private string CreateRuntimeEntry(BrowserProfileStateKey stateKey)
    {
        PreparePrivateDirectory(ContextsRoot);
        var entryDirectory = Path.Combine(ContextsRoot, Guid.NewGuid().ToString("n"));
        PreparePrivateDirectory(entryDirectory);
        BrowserProfileRuntimeManifest.Write(entryDirectory, stateKey);
        return entryDirectory;
    }

    // Chromium requires each disk profile to be an immediate child of its
    // root cache directory. Recovery metadata stays outside the profile tree.
    private string CacheDirectoryForEntry(string entryDirectory) =>
        Path.Combine(_runtimeRoot!, "profile-" + Path.GetFileName(entryDirectory));

    private void DeleteRuntimeEntry(string entryDirectory)
    {
        DeleteOwnedDirectory(CacheDirectoryForEntry(entryDirectory));
        DeleteOwnedDirectory(entryDirectory);
    }

    private static void PreparePrivateDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (File.Exists(fullPath))
        {
            throw new InvalidDataException(
                "A browser runtime directory is occupied by a file.");
        }

        if (!Directory.Exists(fullPath))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                Directory.CreateDirectory(
                    fullPath,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
        }

        ValidateRuntimeDirectory(fullPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    private static void ValidateRuntimeDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        info.Refresh();
        if (!info.Exists
            || info.LinkTarget is not null
            || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "A browser runtime directory is linked or unavailable.");
        }
    }

    private static BrowserProfileClearResult Cancelled() => new(
        BrowserProfileClearStatus.Cancelled,
        0,
        "Browser profile clearing was cancelled.");

    private static string ClearMessage(BrowserProfileDataCategory categories) =>
        categories switch
        {
            BrowserProfileDataCategory.Cookies =>
                "Cookies were cleared from this exact browser profile revision.",
            BrowserProfileDataCategory.HttpAuthentication =>
                "HTTP authentication was cleared from this exact browser profile revision.",
            _ =>
                "The selected browser data categories were cleared from this exact profile revision.",
        };

    internal static void DeleteOwnedDirectory(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (root.LinkTarget is not null
            || (root.Exists
                && root.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new IOException(
                "Browser profile storage root is an unexpected filesystem link.");
        }

        if (!root.Exists)
        {
            return;
        }

        // Callers resolve this exact owner-private tree before deleting it.
        root.Delete(recursive: true);
    }

    internal readonly record struct ContextKey(
        BrowserProfileSelection Selection,
        string Route);

    private sealed class ContextEntry(
        ICefBrowserRequestContext context,
        Uri? proxyEndpoint,
        BrowserProfileStateKey? stateKey,
        string? entryDirectory,
        string? cacheDirectory,
        long initialRevision,
        bool hasInitialLease)
    {
        private readonly Dictionary<long, int> _activeLeases = new()
        {
            [initialRevision] = hasInitialLease ? 1 : 0,
        };

        public ICefBrowserRequestContext Context { get; } = context;

        public Task Ready { get; set; } = Task.CompletedTask;

        public Uri? ProxyEndpoint { get; set; } = proxyEndpoint;

        public BrowserProfileStateKey StateKey { get; } = stateKey
            ?? default;

        public bool IsDurable => stateKey is not null;

        public string? EntryDirectory { get; } = entryDirectory;

        public string? CacheDirectory { get; } = cacheDirectory;

        public bool ContextReleased { get; set; }

        public int ActiveLeases => _activeLeases.Values.Sum();

        public IEnumerable<long> ActiveRevisions => _activeLeases
            .Where(item => item.Value > 0)
            .Select(item => item.Key);

        public bool HasRevision(long revision) =>
            _activeLeases.ContainsKey(revision);

        public int ActiveLeaseCount(long revision) =>
            _activeLeases.GetValueOrDefault(revision);

        public void Acquire(long revision)
        {
            _activeLeases[revision] = checked(
                _activeLeases.GetValueOrDefault(revision) + 1);
        }

        public void Release(long revision)
        {
            if (!_activeLeases.TryGetValue(revision, out var count) || count <= 0)
            {
                throw new InvalidOperationException(
                    "The browser profile lease count is already zero.");
            }

            _activeLeases[revision] = count - 1;
        }
    }
}

/// <summary>
/// Keeps one profile context alive for one browser surface, including native
/// renderer replacement after a crash.
/// </summary>
public sealed class CefBrowserProfileLease : IDisposable
{
    private CefBrowserProfileStore? _owner;
    private readonly CefBrowserProfileStore.ContextKey _key;
    private readonly ICefBrowserRequestContext _context;
    private readonly BrowserProfileBinding _profile;
    private readonly IBrowserProfileAuthenticationResolver? _authenticationResolver;
    private readonly IWorkspaceProxyAuthenticationResolver? _proxyAuthenticationResolver;

    internal CefBrowserProfileLease(
        CefBrowserProfileStore owner,
        CefBrowserProfileStore.ContextKey key,
        ICefBrowserRequestContext context,
        BrowserProfileBinding profile,
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        IWorkspaceProxyAuthenticationResolver? proxyAuthenticationResolver,
        BrowserNetworkRouteKind routeKind,
        Task ready)
    {
        _owner = owner;
        _key = key;
        _context = context;
        _profile = profile;
        _authenticationResolver = authenticationResolver;
        _proxyAuthenticationResolver = proxyAuthenticationResolver;
        RouteKind = routeKind;
        Ready = ready;
    }

    internal BrowserNetworkRouteKind RouteKind { get; }

    public Task Ready { get; }

    internal CefBrowserView CreateView()
    {
        ObjectDisposedException.ThrowIf(_owner is null, this);
        if (!Ready.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("The browser network policy has not been accepted.");
        }
        return _context.CreateView(_profile, _authenticationResolver, _proxyAuthenticationResolver);
    }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(
        _key,
        _profile.Revision);
}

/// <summary>
/// Adapts the vendor request context at the single ownership boundary used by
/// the profile store. Tests replace this boundary without initializing CEF.
/// </summary>
internal interface ICefBrowserRequestContext : IDisposable
{
    bool SetPreference(string name, string value);

    Task<bool> SetPreferenceAsync(string name, string value) => Task.FromResult(SetPreference(name, value));

    Task<int> DeleteCookiesAsync();

    Task FlushCookieStoreAsync();

    Task ClearHttpAuthCredentialsAsync();

    Task CloseAllConnectionsAsync();

    CefBrowserView CreateView(
        BrowserProfileBinding profile,
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        IWorkspaceProxyAuthenticationResolver? proxyAuthenticationResolver);
}

internal sealed class CefBrowserRequestContext(
    CefRequestContext context) : ICefBrowserRequestContext
{
    private readonly CefRequestContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    public static ICefBrowserRequestContext Create(string? cachePath) =>
        new CefBrowserRequestContext(
        Cef.CreateRequestContext(cachePath)
        ?? throw new InvalidOperationException(
            "The embedded browser could not create its profile."));

    public bool SetPreference(string name, string value) =>
        _context.SetPreference(name, value);

    public Task<bool> SetPreferenceAsync(string name, string value) =>
        _context.SetPreferenceAsync(name, value);

    public Task<int> DeleteCookiesAsync() => _context.DeleteCookiesAsync();

    public Task FlushCookieStoreAsync() => _context.FlushCookieStoreAsync();

    public Task ClearHttpAuthCredentialsAsync() =>
        _context.ClearHttpAuthCredentialsAsync();

    public Task CloseAllConnectionsAsync() =>
        _context.CloseAllConnectionsAsync();

    public CefBrowserView CreateView(
        BrowserProfileBinding profile,
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        IWorkspaceProxyAuthenticationResolver? proxyAuthenticationResolver) => new(
        _context,
        CefBrowserContentPolicy.Ordinary,
        profile,
        authenticationResolver,
        proxyAuthenticationResolver);

    public void Dispose() => _context.Dispose();
}
