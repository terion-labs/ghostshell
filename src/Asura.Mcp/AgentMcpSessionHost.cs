using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Mcp;

/// <summary>
/// Native .NET execution bridge from governed agent authorization to
/// directly launched stdio MCP servers.
/// </summary>
public sealed class AgentMcpSessionHost :
    IAgentMcpSessionHost,
    IMcpCredentialSessionInvalidator,
    IMcpServerDiagnostics,
    IAsyncDisposable
{
    private const int MaximumRuns = 16;
    private const int MaximumProfilesPerRun = 16;
    private const int MaximumToolsPerRun = 128;
    private const int MaximumAggregateSchemaBytes = 512 * 1024;
    private const int MaximumEnvironmentValueBytes = 32 * 1024;
    private const int MaximumEnvironmentBytes = 128 * 1024;
    private const int MaximumWindowsEnvironmentBlockCodeUnits = 32_767;
    private static readonly TimeSpan ContextDeadline =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumTestDuration =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumDiagnosticEventAge =
        TimeSpan.FromHours(24);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IDefinitionCatalog _catalog;
    private readonly ISecretVault _secretVault;
    private readonly ISessionHostClient _sessionHost;
    private readonly IAgentAuthorizationConsumer _authorizationConsumer;
    private readonly IAgentMcpRunAuthorityVerifier _mcpRunAuthorityVerifier;
    private readonly IAgentApprovalPrincipal _approvalPrincipal;
    private readonly IMcpServerDiagnosticStore? _diagnosticStore;
    private readonly IWorkspaceNetworkRouteResolver? _workspaceNetworkRoutes;
    private readonly AgentMcpToolCallActionComposer _composer;
    private readonly TimeProvider _timeProvider;
    private readonly McpSessionOptions _clientOptions;
    private readonly Func<Uri, HttpMessageHandler?>
        _streamableHttpHandlerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _testGate = new(1, 1);
    private readonly SemaphoreSlim _diagnosticLoadGate = new(1, 1);
    private readonly CancellationTokenSource _testShutdown = new();
    private readonly ConcurrentDictionary<AgentRunId, RunSession> _runs = [];
    private readonly object _diagnosticGate = new();
    private readonly Dictionary<
        McpServerProfileId,
        McpServerDiagnosticSummary> _diagnosticSummaries = [];
    private readonly object _catalogStateGate = new();
    private readonly SemaphoreSlim _catalogCleanupSignal = new(0, 1);
    private readonly CancellationTokenSource _catalogCleanupShutdown = new();
    private readonly Task _catalogCleanupWorker;
    private McpCatalogState _mcpCatalogState;
    private IReadOnlyDictionary<
        McpServerProfileId,
        McpTestProfileFingerprint> _mcpTestProfileFingerprint;
    private readonly Dictionary<
        McpServerProfileId,
        CancellationTokenSource> _testProfileGenerationSources = [];
    private CancellationTokenSource _catalogGenerationSource = new();
    private bool _catalogEventsStopped;
    private int _disposeStarted;
    private int _cleanupUncertain;
    private DateTimeOffset? _cleanupUncertainAtUtc;
    private volatile bool _diagnosticsLoaded;
    private volatile bool _disposed;

    public event EventHandler<McpServerDiagnosticsChangedEventArgs>? Changed;

    public McpServerDiagnosticsSnapshot Snapshot
    {
        get
        {
            lock (_diagnosticGate)
            {
                PruneDiagnosticsUnsafe(
                    _timeProvider.GetUtcNow().ToUniversalTime());
                return CaptureDiagnosticSnapshotUnsafe();
            }
        }
    }

    public ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        EnsureDiagnosticsLoadedAsync(cancellationToken);

    public async ValueTask<bool> ClearHistoryAsync(
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsAuthenticatedHuman(context))
        {
            return false;
        }

        await EnsureDiagnosticsLoadedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (_diagnosticStore is not null)
        {
            ApplicationRunResult<Unit> cleared;
            try
            {
                cleared = await _diagnosticStore.ClearAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException
                && (exception is not OperationCanceledException
                    || !cancellationToken.IsCancellationRequested))
            {
                _ = exception;
                return false;
            }

            if (!cleared.IsSuccess)
            {
                return false;
            }
        }

        McpServerDiagnosticsSnapshot snapshot;
        lock (_diagnosticGate)
        {
            _diagnosticSummaries.Clear();
            snapshot = CaptureDiagnosticSnapshotUnsafe();
        }

        Changed?.Invoke(
            this,
            new McpServerDiagnosticsChangedEventArgs(snapshot));
        return true;
    }

    public AgentMcpSessionHost(
        IDefinitionCatalog catalog,
        ISecretVault secretVault,
        ISessionHostClient sessionHost,
        IAgentAuthorizationConsumer authorizationConsumer,
        IAgentMcpRunAuthorityVerifier mcpRunAuthorityVerifier,
        AgentMcpToolCallActionComposer composer,
        TimeProvider timeProvider,
        IAgentApprovalPrincipal approvalPrincipal,
        IMcpServerDiagnosticStore diagnosticStore,
        IWorkspaceNetworkRouteResolver workspaceNetworkRoutes)
        : this(
            catalog,
            secretVault,
            sessionHost,
            authorizationConsumer,
            mcpRunAuthorityVerifier,
            composer,
            timeProvider,
            approvalPrincipal,
            CreateDefaultOptions(),
            streamableHttpHandlerFactory: null,
            diagnosticStore: diagnosticStore,
            workspaceNetworkRoutes: workspaceNetworkRoutes)
    {
    }

    internal AgentMcpSessionHost(
        IDefinitionCatalog catalog,
        ISecretVault secretVault,
        ISessionHostClient sessionHost,
        IAgentAuthorizationConsumer authorizationConsumer,
        IAgentMcpRunAuthorityVerifier mcpRunAuthorityVerifier,
        AgentMcpToolCallActionComposer composer,
        TimeProvider timeProvider,
        IAgentApprovalPrincipal approvalPrincipal,
        McpSessionOptions clientOptions,
        Func<Uri, HttpMessageHandler?>? streamableHttpHandlerFactory = null,
        IMcpServerDiagnosticStore? diagnosticStore = null,
        IWorkspaceNetworkRouteResolver? workspaceNetworkRoutes = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _secretVault =
            secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _sessionHost =
            sessionHost ?? throw new ArgumentNullException(nameof(sessionHost));
        _authorizationConsumer = authorizationConsumer
            ?? throw new ArgumentNullException(nameof(authorizationConsumer));
        _mcpRunAuthorityVerifier = mcpRunAuthorityVerifier
            ?? throw new ArgumentNullException(
                nameof(mcpRunAuthorityVerifier));
        _approvalPrincipal = approvalPrincipal
            ?? throw new ArgumentNullException(nameof(approvalPrincipal));
        _diagnosticStore = diagnosticStore;
        _workspaceNetworkRoutes = workspaceNetworkRoutes;
        if (_approvalPrincipal.Actor.Kind != ActorKind.Human
            || _approvalPrincipal.Actor.ClientId is not { } principalClient
            || !string.Equals(
                _approvalPrincipal.Actor.Id.Value,
                principalClient.Value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The MCP diagnostics principal must identify the authenticated human client.",
                nameof(approvalPrincipal));
        }

        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _timeProvider =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _clientOptions =
            clientOptions ?? throw new ArgumentNullException(nameof(clientOptions));
        _clientOptions.Validate();
        _streamableHttpHandlerFactory =
            streamableHttpHandlerFactory ?? (_ => null);
        var catalogSnapshot = _catalog.Snapshot;
        _mcpCatalogState = McpCatalogState.Capture(catalogSnapshot);
        _mcpTestProfileFingerprint =
            CaptureTestProfileFingerprint(catalogSnapshot);
        foreach (var profileId in _mcpTestProfileFingerprint.Keys)
        {
            _testProfileGenerationSources.Add(
                profileId,
                new CancellationTokenSource());
        }

        _catalogCleanupWorker = RunCatalogCleanupWorkerAsync();
        _catalog.Changed += OnCatalogChanged;
    }

    public async ValueTask<AgentMcpHostResult<AgentMcpRunManifest>>
        OpenRunAsync(
            AgentMcpOpenRunRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgentMcpRunAuthorityResult authorityResult;
        try
        {
            authorityResult = await _mcpRunAuthorityVerifier.AcquireAsync(
                    new AgentMcpRunAuthorityRequest(
                        request.RunId,
                        request.Actor),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return Failure<AgentMcpRunManifest>(
                "mcp_run_not_authorized",
                "The MCP run does not have live launch authority.");
        }

        if (authorityResult is not
            AgentMcpRunAuthorityResult.Granted
            {
                Lease: { } lease,
            })
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure<AgentMcpRunManifest>(
                "mcp_run_not_authorized",
                "The MCP run does not have live launch authority.");
        }

        await EnsureDiagnosticsLoadedAsync(cancellationToken)
            .ConfigureAwait(false);

        var catalogGenerationToken =
            CaptureCatalogGenerationToken();
        using var discoveryCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lease.RevocationToken,
                catalogGenerationToken);
        var entered = false;
        try
        {
            await _gate.WaitAsync(discoveryCancellation.Token)
                .ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsCleanupUncertain)
            {
                return Failure<AgentMcpRunManifest>(
                    "mcp_cleanup_uncertain",
                    "A previous MCP process cleanup could not be confirmed.");
            }

            if (_runs.TryGetValue(request.RunId, out var existing))
            {
                if (existing.Agent != request.Actor
                    || existing.Agent != lease.Agent
                    || existing.WorkspaceId != request.WorkspaceId
                    || existing.PolicyGeneration
                        != lease.PolicyGeneration
                    || existing.AuthorityRevocationToken
                        .IsCancellationRequested)
                {
                    return Failure<AgentMcpRunManifest>(
                        "mcp_run_not_authorized",
                        "The MCP run does not have matching live launch authority.");
                }

                if (existing.IsClosing)
                {
                    return Failure<AgentMcpRunManifest>(
                        "mcp_manifest_changed",
                        "The frozen MCP profile set changed.");
                }

                discoveryCancellation.Token.ThrowIfCancellationRequested();
                return new AgentMcpHostResult<
                    AgentMcpRunManifest>.Success(existing.Manifest);
            }

            if (_runs.Count >= MaximumRuns)
            {
                return Failure<AgentMcpRunManifest>(
                    "mcp_run_capacity_exceeded",
                    "The MCP run capacity is exhausted.");
            }

            var profiles = _catalog.Snapshot.McpServerProfiles
                .Where(stored =>
                    stored.Value.IsEnabled
                    && stored.Value.IsTrusted
                    && stored.Value.EnabledTools.Count > 0)
                .OrderBy(stored => stored.Value.Name, StringComparer.Ordinal)
                .ThenBy(
                    stored => stored.Value.Id.Value,
                    StringComparer.Ordinal)
                .ToArray();
            if (profiles.Length > MaximumProfilesPerRun)
            {
                return Failure<AgentMcpRunManifest>(
                    "mcp_profile_capacity_exceeded",
                    "Too many enabled MCP profiles are in scope.");
            }

            RunSession? run = null;
            try
            {
                run = await CreateRunAsync(
                        request,
                        profiles,
                        lease,
                        discoveryCancellation.Token)
                    .ConfigureAwait(false);
                discoveryCancellation.Token.ThrowIfCancellationRequested();
                if (!_runs.TryAdd(request.RunId, run))
                {
                    throw new InvalidOperationException(
                        "The MCP run was added concurrently.");
                }

                return new AgentMcpHostResult<
                    AgentMcpRunManifest>.Success(run.Manifest);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                if (run is not null)
                {
                    await run.DisposeAsync().ConfigureAwait(false);
                    if (run.CleanupUncertain)
                    {
                        MarkCleanupUncertain();
                    }
                }

                throw;
            }
            catch (OperationCanceledException)
                when (lease.RevocationToken.IsCancellationRequested)
            {
                if (run is not null)
                {
                    await run.DisposeAsync().ConfigureAwait(false);
                    if (run.CleanupUncertain)
                    {
                        MarkCleanupUncertain();
                        return Failure<AgentMcpRunManifest>(
                            "mcp_cleanup_uncertain",
                            "MCP process cleanup could not be confirmed.");
                    }
                }

                return Failure<AgentMcpRunManifest>(
                    "mcp_run_authority_revoked",
                    "The MCP run launch authority changed during discovery.");
            }
            catch (OperationCanceledException)
                when (catalogGenerationToken.IsCancellationRequested)
            {
                if (run is not null)
                {
                    await run.DisposeAsync().ConfigureAwait(false);
                    if (run.CleanupUncertain)
                    {
                        MarkCleanupUncertain();
                        return Failure<AgentMcpRunManifest>(
                            "mcp_cleanup_uncertain",
                            "MCP process cleanup could not be confirmed.");
                    }
                }

                return Failure<AgentMcpRunManifest>(
                    "mcp_manifest_changed",
                    "The MCP profile set changed during discovery.");
            }
            catch (McpHostFailureException exception)
            {
                if (run is not null)
                {
                    await run.DisposeAsync().ConfigureAwait(false);
                    if (run.CleanupUncertain)
                    {
                        MarkCleanupUncertain();
                        return Failure<AgentMcpRunManifest>(
                            "mcp_cleanup_uncertain",
                            "MCP process cleanup could not be confirmed.");
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "MCP discovery was cancelled.",
                        exception,
                        cancellationToken);
                }

                return Failure<AgentMcpRunManifest>(
                    exception.StableCode,
                    exception.Message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                if (run is not null)
                {
                    await run.DisposeAsync().ConfigureAwait(false);
                    if (run.CleanupUncertain)
                    {
                        MarkCleanupUncertain();
                        return Failure<AgentMcpRunManifest>(
                            "mcp_cleanup_uncertain",
                            "MCP process cleanup could not be confirmed.");
                    }
                }

                return Failure<AgentMcpRunManifest>(
                    "mcp_discovery_failed",
                    "MCP discovery failed safely.");
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (lease.RevocationToken.IsCancellationRequested)
        {
            return Failure<AgentMcpRunManifest>(
                "mcp_run_authority_revoked",
                "The MCP run launch authority changed during discovery.");
        }
        catch (OperationCanceledException)
            when (catalogGenerationToken.IsCancellationRequested)
        {
            return Failure<AgentMcpRunManifest>(
                "mcp_manifest_changed",
                "The MCP profile set changed during discovery.");
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask<
        AgentMcpHostResult<AgentMcpToolCallReceipt>> RunToolAsync(
        AgentAuthorizationId authorizationId,
        AgentMcpToolCallAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunSession? run;
        CancellationToken catalogGenerationToken;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure<AgentMcpToolCallReceipt>(
                "caller_cancelled",
                "The MCP call was cancelled before entering the run.");
        }

        try
        {
            if (_disposed
                || !_runs.TryGetValue(action.Proposal.RunId, out run))
            {
                return Failure<AgentMcpToolCallReceipt>(
                    "mcp_run_not_found",
                    "The MCP run is no longer available.");
            }

            catalogGenerationToken =
                CaptureCatalogGenerationToken();
        }
        finally
        {
            _gate.Release();
        }

        bool entered;
        try
        {
            entered = await run.TryEnterAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure<AgentMcpToolCallReceipt>(
                "caller_cancelled",
                "The MCP call was cancelled before entering the run.");
        }

        if (!entered)
        {
            return Failure<AgentMcpToolCallReceipt>(
                "mcp_run_closed",
                "The MCP run is closing.");
        }

        try
        {
            return await RunToolCoreAsync(
                    run,
                    authorizationId,
                    action,
                    cancellationToken,
                    catalogGenerationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            run.Exit();
        }
    }

    public async ValueTask CloseRunAsync(
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        RunSession? run = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runs.TryRemove(runId, out var removed))
            {
                run = removed;
                run.BeginClose();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (run is not null)
        {
            await run.DisposeAsync().ConfigureAwait(false);
            if (run.CleanupUncertain)
            {
                MarkCleanupUncertain();
            }
        }
    }

    public async ValueTask InvalidateAsync(SecretRef reference)
    {
        RunSession[] affectedRuns;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!_disposed)
            {
                affectedRuns = [.. _runs
                    .Where(pair => pair.Value.ReferencesSecret(reference))
                    .Select(pair => pair.Value)];
                foreach (var run in affectedRuns)
                {
                    _runs.TryRemove(
                        run.Manifest.RunId,
                        out _);
                    run.BeginClose();
                }
            }
            else
            {
                affectedRuns = [];
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var run in affectedRuns)
        {
            await run.DisposeAsync().ConfigureAwait(false);
            if (run.CleanupUncertain)
            {
                MarkCleanupUncertain();
            }
        }

        try
        {
            await _testGate.WaitAsync().ConfigureAwait(false);
            _testGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Host shutdown already made diagnostics quiescent.
        }
    }

    public async ValueTask<McpServerTestResult> TestAsync(
        McpServerTestRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!IsAuthenticatedHuman(context))
        {
            return TestFailure(
                "mcp_test_not_authenticated",
                "The MCP server test requires an authenticated human client.",
                retryable: false);
        }

        await EnsureDiagnosticsLoadedAsync(cancellationToken)
            .ConfigureAwait(false);

        if (context.ExpectedRevision != request.ExpectedRevision)
        {
            return TestFailure(
                "mcp_profile_revision_mismatch",
                "The MCP server changed before the test could start.",
                retryable: false);
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var duration = MaximumTestDuration;
        if (context.DeadlineUtc is { } deadline)
        {
            if (deadline.Offset != TimeSpan.Zero || deadline <= now)
            {
                return TestFailure(
                    "mcp_test_deadline_invalid",
                    "The MCP server test deadline is invalid or expired.",
                    retryable: false);
            }

            var remaining = deadline - now;
            if (remaining < duration)
            {
                duration = remaining;
            }
        }

        var catalogGenerationToken =
            CaptureTestProfileGenerationToken(
                request.ProfileId);
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(
                cancellationToken,
                _testShutdown.Token,
                catalogGenerationToken);
        timeout.CancelAfter(duration);
        var entered = false;
        try
        {
            await _testGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (_disposed)
            {
                return TestFailure(
                    "mcp_test_unavailable",
                    "MCP diagnostics are no longer available.",
                    retryable: false);
            }

            if (IsCleanupUncertain)
            {
                return TestFailure(
                    "mcp_cleanup_uncertain",
                    "A previous MCP process cleanup could not be confirmed.",
                    retryable: false);
            }

            var stored = _catalog.Snapshot.McpServerProfiles
                .SingleOrDefault(item =>
                    item.Value.Id == request.ProfileId);
            if (stored is null)
            {
                return TestFailure(
                    "mcp_profile_not_found",
                    "The MCP server profile no longer exists.",
                    retryable: false);
            }

            if (stored.Revision != request.ExpectedRevision)
            {
                return TestFailure(
                    "mcp_profile_revision_mismatch",
                    "The MCP server changed before the test could start.",
                    retryable: false);
            }

            if (!stored.Value.IsTrusted)
            {
                return TestFailure(
                    "mcp_profile_untrusted",
                    "Review and trust the MCP server profile before testing it.",
                    retryable: false);
            }

            var diagnostic = BeginDiagnostic(
                stored,
                McpServerSessionKind.Test,
                McpServerLifecycleState.Testing,
                "mcp_testing",
                "Testing the MCP server connection and tool discovery.");
            var session = await OpenProfileAsync(
                    stored,
                    diagnostic,
                    workspaceId: null,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            McpServerTestResult result;
            await using (session)
            {
                timeout.Token.ThrowIfCancellationRequested();
                result = !IsProfileRevisionCurrent(stored)
                    ? TestFailure(
                        "mcp_profile_revision_mismatch",
                        "The MCP server changed during discovery.",
                        retryable: false)
                    : new McpServerTestResult.Success(
                        new McpServerTestReport(
                            stored.Value.Id,
                            stored.Revision,
                            session.DiscoveredToolCount,
                            session.Manifests.Count,
                            _timeProvider.GetUtcNow()
                                .ToUniversalTime()));
            }

            if (session.CleanupUncertain)
            {
                MarkCleanupUncertain();
                return TestFailure(
                    "mcp_cleanup_uncertain",
                    "The MCP test process cleanup could not be confirmed.",
                    retryable: false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (catalogGenerationToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !_testShutdown.IsCancellationRequested)
            {
                return TestFailure(
                    "mcp_profile_revision_mismatch",
                    "The MCP server changed during the test.",
                    retryable: false);
            }

            return CreateTestCancellationFailure(
                cancellationToken,
                timeout.Token);
        }
        catch (McpHostFailureException exception)
        {
            if (string.Equals(exception.StableCode, "mcp_cancelled"
, StringComparison.Ordinal) && catalogGenerationToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !_testShutdown.IsCancellationRequested)
            {
                return TestFailure(
                    "mcp_profile_revision_mismatch",
                    "The MCP server changed during the test.",
                    retryable: false);
            }

            if (string.Equals(exception.StableCode, "mcp_cancelled"
, StringComparison.Ordinal) && timeout.IsCancellationRequested)
            {
                return CreateTestCancellationFailure(
                    cancellationToken,
                    timeout.Token);
            }

            return TestFailure(
                exception.StableCode,
                exception.Message,
                IsRetryableTestFailure(exception.StableCode));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return TestFailure(
                "mcp_test_failed",
                "The MCP server test failed safely.",
                retryable: true);
        }
        finally
        {
            if (entered)
            {
                _testGate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _testShutdown.Cancel();
        _catalog.Changed -= OnCatalogChanged;
        CancellationTokenSource catalogGeneration;
        CancellationTokenSource[] testProfileGenerations;
        lock (_catalogStateGate)
        {
            _catalogEventsStopped = true;
            catalogGeneration = _catalogGenerationSource;
            testProfileGenerations =
                [.. _testProfileGenerationSources.Values];
            _testProfileGenerationSources.Clear();
        }

        CancelWithoutThrow(catalogGeneration);
        foreach (var generation in testProfileGenerations)
        {
            CancelWithoutThrow(generation);
        }

        try
        {
            await _catalogCleanupShutdown.CancelAsync()
                .ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            // Cancellation remains effective even if a callback failed.
        }

        await _catalogCleanupWorker.ConfigureAwait(false);
        RunSession[] runs;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runs = [.. _runs.Values];
            _runs.Clear();
            foreach (var run in runs)
            {
                run.BeginClose();
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var run in runs)
        {
            await run.DisposeAsync().ConfigureAwait(false);
            if (run.CleanupUncertain)
            {
                MarkCleanupUncertain();
            }
        }

        await _testGate.WaitAsync().ConfigureAwait(false);
        _testGate.Release();
        await _diagnosticLoadGate.WaitAsync().ConfigureAwait(false);
        _diagnosticLoadGate.Release();
        _gate.Dispose();
        _testGate.Dispose();
        _diagnosticLoadGate.Dispose();
        _testShutdown.Dispose();
        catalogGeneration.Dispose();
        foreach (var generation in testProfileGenerations)
        {
            generation.Dispose();
        }

        _catalogCleanupShutdown.Dispose();
        _catalogCleanupSignal.Dispose();
    }

    private void OnCatalogChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        var snapshot = _catalog.Snapshot;
        var nextState = McpCatalogState.Capture(snapshot);
        var nextTestFingerprint =
            CaptureTestProfileFingerprint(snapshot);
        CancellationTokenSource? revokedRunGeneration = null;
        List<CancellationTokenSource> revokedTestGenerations = [];
        var runStateChanged = false;
        lock (_catalogStateGate)
        {
            if (_catalogEventsStopped)
            {
                return;
            }

            var profileIds = _mcpTestProfileFingerprint.Keys
                .Concat(nextTestFingerprint.Keys)
                .Distinct()
                .ToArray();
            foreach (var profileId in profileIds)
            {
                var hadPrevious = _mcpTestProfileFingerprint
                    .TryGetValue(
                        profileId,
                        out var previous);
                var hasCurrent = nextTestFingerprint.TryGetValue(
                    profileId,
                    out var current);
                if (hadPrevious == hasCurrent
                    && (!hadPrevious || previous == current))
                {
                    continue;
                }

                if (_testProfileGenerationSources.Remove(
                        profileId,
                        out var revokedTestGeneration))
                {
                    revokedTestGenerations.Add(
                        revokedTestGeneration);
                }

                if (hasCurrent)
                {
                    _testProfileGenerationSources.Add(
                        profileId,
                        new CancellationTokenSource());
                }
            }

            _mcpTestProfileFingerprint = nextTestFingerprint;
            runStateChanged =
                !_mcpCatalogState.HasSameAuthorityFingerprint(
                    nextState);
            if (runStateChanged)
            {
                _mcpCatalogState = nextState;
                revokedRunGeneration =
                    _catalogGenerationSource;
                _catalogGenerationSource =
                    new CancellationTokenSource();
            }
        }

        foreach (var generation in revokedTestGenerations)
        {
            CancelWithoutThrow(generation);
            generation.Dispose();
        }

        if (!runStateChanged)
        {
            return;
        }

        CancelWithoutThrow(revokedRunGeneration!);
        foreach (var run in _runs.Values)
        {
            if (!run.HasCurrentProfiles(nextState))
            {
                run.BeginClose();
            }
        }

        revokedRunGeneration!.Dispose();
        try
        {
            _catalogCleanupSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending reconciliation already covers the latest snapshot.
        }
        catch (ObjectDisposedException)
        {
            // Host shutdown already owns all remaining runs.
        }
    }

    private CancellationToken CaptureCatalogGenerationToken()
    {
        lock (_catalogStateGate)
        {
            return _catalogEventsStopped
                ? new CancellationToken(canceled: true)
                : _catalogGenerationSource.Token;
        }
    }

    private CancellationToken CaptureTestProfileGenerationToken(
        McpServerProfileId profileId)
    {
        lock (_catalogStateGate)
        {
            if (_catalogEventsStopped)
            {
                return new CancellationToken(canceled: true);
            }

            return _testProfileGenerationSources.TryGetValue(
                profileId,
                out var generation)
                    ? generation.Token
                    : CancellationToken.None;
        }
    }

    private static IReadOnlyDictionary<
        McpServerProfileId,
        McpTestProfileFingerprint> CaptureTestProfileFingerprint(
        DefinitionCatalogSnapshot snapshot) =>
        snapshot.McpServerProfiles.ToDictionary(
            stored => stored.Value.Id,
            stored => new McpTestProfileFingerprint(
                stored.Revision,
                stored.Value.IsEnabled));

    private async Task RunCatalogCleanupWorkerAsync()
    {
        try
        {
            while (true)
            {
                await _catalogCleanupSignal.WaitAsync(
                        _catalogCleanupShutdown.Token)
                    .ConfigureAwait(false);
                try
                {
                    await ReconcileCatalogRunsAsync(
                            _catalogCleanupShutdown.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (_catalogCleanupShutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                    when (_catalogEventsStopped)
                {
                    return;
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException)
                {
                    _ = exception;
                    MarkCleanupUncertain();
                }
            }
        }
        catch (OperationCanceledException)
            when (_catalogCleanupShutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
            when (_catalogEventsStopped)
        {
        }
    }

    private async Task ReconcileCatalogRunsAsync(
        CancellationToken cancellationToken)
    {
        McpCatalogState state;
        lock (_catalogStateGate)
        {
            state = _mcpCatalogState;
        }

        RunSession[] staleRuns;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var candidates = _runs.Values
                .Where(run => !run.HasCurrentProfiles(state))
                .ToArray();
            var removedRuns = new List<RunSession>(
                candidates.Length);
            foreach (var run in candidates)
            {
                if (_runs.TryRemove(
                        run.Manifest.RunId,
                        out var removed))
                {
                    removed.BeginClose();
                    removedRuns.Add(removed);
                }
            }

            staleRuns = [.. removedRuns];
        }
        finally
        {
            _gate.Release();
        }

        foreach (var run in staleRuns)
        {
            await run.DisposeAsync().ConfigureAwait(false);
            if (run.CleanupUncertain)
            {
                MarkCleanupUncertain();
            }
        }
    }

    private static void CancelWithoutThrow(
        CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Authority is revoked even when a consumer callback fails.
        }
        catch (ObjectDisposedException)
        {
            // A completed reconciliation may already have retired it.
        }
    }

    private async Task<RunSession> CreateRunAsync(
        AgentMcpOpenRunRequest request,
        IReadOnlyList<StoredDefinition<McpServerProfile>> profiles,
        AgentMcpRunAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        var sessions = new List<ProfileSession>(profiles.Count);
        var aggregateSchemaBytes = 0;
        try
        {
            foreach (var profile in profiles)
            {
                var diagnostic = BeginDiagnostic(
                    profile,
                    McpServerSessionKind.AgentRun,
                    McpServerLifecycleState.Starting,
                    "mcp_starting",
                    "Starting the MCP server and negotiating its protocol.");
                sessions.Add(await OpenProfileAsync(
                        profile,
                        diagnostic,
                        request.WorkspaceId,
                        cancellationToken)
                    .ConfigureAwait(false));
                if (sessions.Sum(session => session.DiscoveredToolCount)
                    > MaximumToolsPerRun)
                {
                    throw new McpHostFailureException(
                        "mcp_tool_capacity_exceeded",
                        "The enabled MCP tool set exceeds its run limit.");
                }

                foreach (var manifest in sessions[^1].Manifests)
                {
                    var schemaBytes = StrictUtf8.GetByteCount(
                        manifest.InputSchema.GetRawText());
                    if (aggregateSchemaBytes
                        > MaximumAggregateSchemaBytes - schemaBytes)
                    {
                        throw new McpHostFailureException(
                            "mcp_schema_capacity_exceeded",
                            "The enabled MCP schemas exceed their run budget.");
                    }

                    aggregateSchemaBytes += schemaBytes;
                }
            }

            var manifests = sessions
                .SelectMany(session => session.Manifests)
                .ToArray();
            var runManifest = new AgentMcpRunManifest(
                request.RunId,
                request.OpenedAtUtc,
                manifests);
            return new RunSession(
                runManifest,
                lease.Agent,
                request.WorkspaceId,
                lease.PolicyGeneration,
                lease.RevocationToken,
                sessions);
        }
        catch
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (sessions.Any(session => session.CleanupUncertain))
            {
                MarkCleanupUncertain();
                throw new McpHostFailureException(
                    "mcp_cleanup_uncertain",
                    "MCP process cleanup could not be confirmed.");
            }

            throw;
        }
    }

    private async Task<McpStdioServerLaunch> CreateStdioLaunchAsync(
        WorkspaceInstanceId? workspaceId,
        McpServerTransport.Stdio transport,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        if (workspaceId is null || _workspaceNetworkRoutes is null)
        {
            return new McpStdioServerLaunch(
                transport.Executable,
                transport.Arguments,
                transport.WorkingDirectory,
                environment);
        }

        var connector = RequireWorkspaceConnector(workspaceId.Value);
        if (_workspaceNetworkRoutes.IsolatedCommandRuntimeFor(workspaceId.Value)
            is not { } commandRuntime)
        {
            return new McpStdioServerLaunch(
                transport.Executable,
                transport.Arguments,
                transport.WorkingDirectory,
                AddProxyEnvironment(environment, connector));
        }

        var startupEnvironment = environment
            .Select(pair => new ConnectionEnvironmentVariable(
                pair.Key,
                new ConnectionEnvironmentValue.PlainText(pair.Value)))
            .ToArray();
        var connection = new ConnectionProfile(
            ConnectionId.New(),
            ConnectionProfile.CurrentSchemaVersion,
            "MCP workspace process",
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            new ConnectionStartup(
                transport.WorkingDirectory,
                startupEnvironment),
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var planned = await commandRuntime.PlanDuplexCommandAsync(
                connection,
                transport.Executable,
                transport.Arguments,
                cancellationToken)
            .ConfigureAwait(false);
        if (planned is not ConnectionRuntimeResult<TerminalLaunchRequest>.Success success
            || success.Value.Executable is not { } executable)
        {
            throw new McpHostFailureException(
                "mcp_workspace_route_unavailable",
                "The MCP server could not start in the workspace environment.");
        }

        return new McpStdioServerLaunch(
            executable,
            success.Value.Arguments,
            success.Value.WorkingDirectory,
            success.Value.Environment);
    }

    private HttpMessageHandler? CreateStreamableHttpHandler(
        WorkspaceInstanceId? workspaceId,
        Uri endpoint)
    {
        var injected = _streamableHttpHandlerFactory(endpoint);
        if (injected is not null || workspaceId is null)
        {
            return injected;
        }

        if (_workspaceNetworkRoutes is null)
        {
            return null;
        }

        var connector = RequireWorkspaceConnector(workspaceId.Value);
        var proxy = new WebProxy(connector.LocalProxyEndpoint);
        if (connector.LocalProxyCredentials is { } credentials)
        {
            proxy.Credentials = new NetworkCredential(
                credentials.Username,
                credentials.Password);
        }

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _clientOptions.InitializationTimeout,
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            Proxy = proxy,
            UseCookies = false,
            UseProxy = true,
        };
    }

    private IWorkspaceNetworkConnector RequireWorkspaceConnector(
        WorkspaceInstanceId workspaceId)
    {
        var connector = _workspaceNetworkRoutes?.ConnectorFor(workspaceId)
            ?? throw new McpHostFailureException(
                "mcp_workspace_route_unavailable",
                "The workspace network route is unavailable.");
        if (connector.Egress == WorkspaceNetworkEgress.Blocked)
        {
            throw new McpHostFailureException(
                "workspace_network_kill_switch_blocked",
                "The workspace network kill switch is blocking traffic.");
        }

        return connector;
    }

    private static IReadOnlyDictionary<string, string> AddProxyEnvironment(
        IReadOnlyDictionary<string, string> environment,
        IWorkspaceNetworkConnector connector) =>
        WorkspaceProxyEnvironment.Create(
            environment, connector.LocalProxyEndpoint, connector.LocalProxyCredentials,
            connector.BrowserProxyEndpoint);

    private async Task<ProfileSession> OpenProfileAsync(
        StoredDefinition<McpServerProfile> stored,
        McpDiagnosticSession diagnostic,
        WorkspaceInstanceId? workspaceId,
        CancellationToken cancellationToken)
    {
        var profile = stored.Value;
        ValidateLaunch(profile);
        if (workspaceId is not null && _workspaceNetworkRoutes is not null)
        {
            _ = RequireWorkspaceConnector(workspaceId.Value);
        }

        var secrets = await ResolveTransportSecretsAsync(
                profile,
                cancellationToken)
            .ConfigureAwait(false);
        byte[] toolIdentityKey;
        try
        {
            toolIdentityKey = RandomNumberGenerator.GetBytes(
                SHA256.HashSizeInBytes);
        }
        catch
        {
            secrets.Dispose();
            throw;
        }
        McpClientSession? client = null;
        try
        {
            McpResult<McpClientSession> connected;
            string transportTarget;
            string? workingDirectory;
            switch (profile.Transport)
            {
                case McpServerTransport.Stdio stdio:
                    var launch = await CreateStdioLaunchAsync(
                            workspaceId,
                            stdio,
                            secrets.Values,
                            cancellationToken)
                        .ConfigureAwait(false);
                    connected = await McpClientSession.ConnectStdioAsync(
                            launch,
                            new McpClientInfo("asura", "1.0.0"),
                            _clientOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    transportTarget = stdio.Executable;
                    workingDirectory = launch.WorkingDirectory;
                    break;
                case McpServerTransport.StreamableHttp http:
                    connected = await McpStreamableHttpClient.ConnectAsync(
                            http.Endpoint,
                            secrets.Values,
                            new McpClientInfo("asura", "1.0.0"),
                            _clientOptions,
                            CreateStreamableHttpHandler(
                                workspaceId,
                                http.Endpoint),
                            cancellationToken)
                        .ConfigureAwait(false);
                    transportTarget = http.Endpoint.AbsoluteUri;
                    workingDirectory = null;
                    break;
                default:
                    throw new McpHostFailureException(
                        "mcp_transport_unsupported",
                        "The MCP profile transport is unsupported.");
            }

            secrets.DropTransportValues();
            if (!connected.IsSuccess)
            {
                var error = connected.Error!;
                if (error.CleanupUncertain)
                {
                    MarkCleanupUncertain();
                    throw new McpHostFailureException(
                        "mcp_cleanup_uncertain",
                        "MCP process cleanup could not be confirmed.");
                }

                throw MapFailure(error);
            }

            client = connected.Value!;
            var listed = await client.ListToolsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                throw MapFailure(listed.Error!);
            }

            if (client.IsToolCatalogStale)
            {
                throw new McpHostFailureException(
                    "mcp_tool_catalog_changed",
                    "The MCP tool catalog changed during discovery.");
            }

            var listedTools = listed.Value!;
            var listedByName = listedTools.ToDictionary(
                tool => tool.Name,
                StringComparer.Ordinal);
            var manifests = new List<AgentMcpToolManifest>(
                profile.EnabledTools.Count);
            var protocolToolNames = new Dictionary<string, string>(
                profile.EnabledTools.Count,
                StringComparer.Ordinal);
            foreach (var enabledTool in profile.EnabledTools)
            {
                if (!listedByName.TryGetValue(
                        enabledTool,
                        out var tool))
                {
                    throw new McpHostFailureException(
                        "mcp_enabled_tool_missing",
                        "An enabled MCP tool is absent from the server catalog.");
                }

                var schema = McpAgentSchemaSanitizer.Sanitize(
                    tool.InputSchema,
                    secrets.Redactor);
                var serverName = secrets.Redactor.Redact(
                    client.ServerInfo.Name,
                    out _);
                var serverVersion = secrets.Redactor.Redact(
                    client.ServerInfo.Version,
                    out _);
                var toolIdentity = CreateOpaqueToolIdentity(
                    toolIdentityKey,
                    profile.Id,
                    tool.Name);
                var safeToolName = secrets.Redactor.Redact(
                    enabledTool,
                    out var toolNameRedacted);
                if (toolNameRedacted)
                {
                    safeToolName =
                        $"redacted_tool_{toolIdentity.Value[..16]}";
                }

                var manifest = new AgentMcpToolManifest(
                    profile.Id,
                    stored.Revision,
                    profile.Name,
                    profile.Transport.Kind,
                    transportTarget,
                    workingDirectory,
                    serverName,
                    serverVersion,
                    McpProtocol.Version,
                    safeToolName,
                    schema,
                    toolIdentity,
                    toolNameRedacted);
                manifests.Add(manifest);
                protocolToolNames.Add(
                    manifest.ProviderAlias,
                    tool.Name);
            }

            PublishDiagnostic(
                diagnostic,
                McpServerLifecycleState.Healthy,
                "mcp_healthy",
                "MCP initialization and bounded tool discovery succeeded.");
            return new ProfileSession(
                stored,
                client,
                secrets.DetachRedactor(),
                manifests,
                protocolToolNames,
                listedTools.Count,
                diagnostic,
                CompleteDiagnosticSessionAsync);
        }
        catch (Exception exception)
        {
            var cleanupUncertain = false;
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                if (client.CleanupUncertain)
                {
                    MarkCleanupUncertain();
                    cleanupUncertain = true;
                }
            }

            secrets.Dispose();
            if (cleanupUncertain
                && exception is not OutOfMemoryException)
            {
                PublishDiagnostic(
                    diagnostic,
                    McpServerLifecycleState.CleanupUncertain,
                    "mcp_cleanup_uncertain",
                    "MCP process cleanup could not be confirmed.");
                await PersistDiagnosticsAsync().ConfigureAwait(false);
                throw new McpHostFailureException(
                    "mcp_cleanup_uncertain",
                    "MCP process cleanup could not be confirmed.",
                    exception);
            }

            if (exception is not OutOfMemoryException)
            {
                var stableCode = exception is McpHostFailureException failure
                    ? failure.StableCode
                    : "mcp_discovery_failed";
                PublishDiagnostic(
                    diagnostic,
                    McpServerLifecycleState.Failed,
                    stableCode,
                    "The MCP server failed during bounded startup or discovery.");
                await PersistDiagnosticsAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(toolIdentityKey);
        }
    }

    private async ValueTask<
        AgentMcpHostResult<AgentMcpToolCallReceipt>> RunToolCoreAsync(
        RunSession run,
        AgentAuthorizationId authorizationId,
        AgentMcpToolCallAction action,
        CancellationToken callerCancellation,
        CancellationToken catalogGenerationToken)
    {
        if (!run.TryResolve(
                action.Request.Manifest.ProviderAlias,
                out var profileSession,
                out var currentManifest,
                out var protocolToolName)
            || currentManifest.ManifestDigest
                != action.Request.Manifest.ManifestDigest
            || !IsProfileCurrent(profileSession.Stored))
        {
            return Failure<AgentMcpToolCallReceipt>(
                "mcp_manifest_changed",
                "The frozen MCP manifest no longer matches configuration.");
        }

        AgentContextSnapshot context;
        try
        {
            context = await InspectTargetAsync(
                    action,
                    callerCancellation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (callerCancellation.IsCancellationRequested)
        {
            return Failure<AgentMcpToolCallReceipt>(
                "caller_cancelled",
                "The MCP call was cancelled before authorization consumption.");
        }
        catch (McpHostFailureException exception)
        {
            return Failure<AgentMcpToolCallReceipt>(
                exception.StableCode,
                exception.Message);
        }

        AgentActionExecutionBinding binding;
        try
        {
            binding = _composer.BindForExecution(
                action,
                context,
                currentManifest);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            _ = exception;
            return Failure<AgentMcpToolCallReceipt>(
                "mcp_action_invalid",
                "The prepared MCP call no longer matches its exact authority.");
        }

        AgentPermitResult permitResult;
        try
        {
            permitResult = await _authorizationConsumer.ConsumeAsync(
                    authorizationId,
                    binding,
                    callerCancellation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (callerCancellation.IsCancellationRequested)
        {
            return Failure<AgentMcpToolCallReceipt>(
                "caller_cancelled",
                "The MCP call was cancelled before dispatch.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return Failure<AgentMcpToolCallReceipt>(
                "mcp_authorization_unavailable",
                "The MCP authorization could not be consumed.");
        }

        if (permitResult is AgentPermitResult.Denied denied)
        {
            return Failure<AgentMcpToolCallReceipt>(
                StableAuthorizationCode(denied.Error.Code),
                "The MCP authorization was rejected.");
        }

        var permit = ((AgentPermitResult.Granted)permitResult).Permit;
        if (permit.Authorization.Source is not (
                AgentAuthorizationSource.HumanApproval
                or AgentAuthorizationSource.YoloPolicy))
        {
            return await CompleteFailureAsync(
                    permit,
                    "mcp_human_approval_required",
                    "MCP tools require exact human approval or current full access.")
                .ConfigureAwait(false);
        }

        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                permit.CancellationToken,
                run.ShutdownToken,
                catalogGenerationToken);
        if (operationCancellation.IsCancellationRequested)
        {
            return await CompleteCancelledAsync(permit)
                .ConfigureAwait(false);
        }

        if (!IsProfileCurrent(profileSession.Stored)
            || profileSession.Client.IsToolCatalogStale
            || run.IsClosing)
        {
            return await CompleteFailureAsync(
                    permit,
                    "mcp_manifest_changed",
                    "The MCP profile or tool catalog changed before dispatch.")
                .ConfigureAwait(false);
        }

        var called = await profileSession.Client.CallToolAsync(
                protocolToolName,
                action.Request.Arguments,
                operationCancellation.Token)
            .ConfigureAwait(false);
        if (!called.IsSuccess)
        {
            var error = called.Error!;
            if (error.Code == McpErrorCode.Cancelled
                && !error.OutcomeUncertain)
            {
                return await CompleteCancelledAsync(permit)
                    .ConfigureAwait(false);
            }

            var stableCode = error.OutcomeUncertain
                ? "mcp_tool_outcome_unknown"
                : StableMcpCode(error.Code);
            PublishDiagnostic(
                profileSession.Diagnostic,
                error.OutcomeUncertain
                    ? McpServerLifecycleState.Failed
                    : McpServerLifecycleState.Degraded,
                stableCode,
                error.OutcomeUncertain
                    ? "The live MCP session returned an outcome that could not be confirmed."
                    : "The live MCP session reported a bounded tool failure.");
            var completed = await CompleteAsync(
                    permit,
                    AgentActionOutcome.Failed,
                    stableCode)
                .ConfigureAwait(false);
            if (completed is not null)
            {
                return completed;
            }

            return new AgentMcpHostResult<
                AgentMcpToolCallReceipt>.Failure(
                new AgentMcpHostError(
                    stableCode,
                    error.OutcomeUncertain
                        ? "The MCP tool outcome could not be confirmed."
                        : "The MCP tool call failed safely.",
                    error.OutcomeUncertain));
        }

        AgentMcpToolCallReceipt receipt;
        try
        {
            receipt = McpProviderResultProjection.Project(
                called.Value!,
                profileSession.Redactor);
            if (!IsValidProviderReceipt(receipt))
            {
                throw new InvalidOperationException(
                    "The MCP result projection is invalid.");
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            _ = exception;
            receipt = new AgentMcpToolCallReceipt(
                called.Value!.IsError
                    ? """
                      {"ok":false,"content_origin":"untrusted_mcp","is_error":true,"content":[],"omitted_content_count":0,"redacted_content_count":0,"structured_content":null,"projection_notice":"The completed MCP result could not be projected."}
                      """
                    : """
                      {"ok":true,"content_origin":"untrusted_mcp","is_error":false,"content":[],"omitted_content_count":0,"redacted_content_count":0,"structured_content":null,"projection_notice":"The completed MCP result could not be projected."}
                      """,
                called.Value.IsError);
        }

        var outcome = receipt.IsError
            ? AgentActionOutcome.Failed
            : AgentActionOutcome.Succeeded;
        var resultCode = receipt.IsError
            ? "mcp_tool_error"
            : "mcp_tool_succeeded";
        var completionFailure = await CompleteAsync(
                permit,
                outcome,
                resultCode)
            .ConfigureAwait(false);
        return completionFailure
            ?? new AgentMcpHostResult<
                AgentMcpToolCallReceipt>.Success(receipt);
    }

    private async Task<AgentContextSnapshot> InspectTargetAsync(
        AgentMcpToolCallAction action,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var deadline = now + ContextDeadline;
        if (deadline > action.Proposal.DeadlineUtc)
        {
            deadline = action.Proposal.DeadlineUtc;
        }

        if (deadline <= now)
        {
            throw new McpHostFailureException(
                "mcp_action_expired",
                "The MCP action expired before target inspection.");
        }

        var maximumPanelCount = action.Proposal.Target is
            AgentTarget.Panel or AgentTarget.ConnectionSession
                ? 1
                : AgentTarget.SelectedPanels.MaximumPanelCount;
        var result = await _sessionHost.InspectAgentContextAsync(
                new AgentContextRequest(
                    action.Proposal.Target,
                    maximumPanelCount),
                new OperationContext(
                    RequestId.New(),
                    action.Proposal.Actor,
                    CancellationId: CancellationId.New(),
                    DeadlineUtc: deadline),
                cancellationToken)
            .ConfigureAwait(false);
        if (result
                is not HostResult<AgentContextSnapshot>.Success success
            || success.Value.Target != action.Proposal.Target)
        {
            throw new McpHostFailureException(
                "target_changed",
                "The exact agent target changed before MCP execution.");
        }

        return success.Value;
    }

    private async ValueTask<
        AgentMcpHostResult<AgentMcpToolCallReceipt>?> CompleteAsync(
        AgentActionPermit permit,
        AgentActionOutcome outcome,
        string stableCode)
    {
        try
        {
            var error = await _authorizationConsumer.CompleteAsync(
                    permit,
                    new AgentActionCompletion(
                        outcome,
                        stableCode,
                        _timeProvider.GetUtcNow().ToUniversalTime()),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return error is null
                ? null
                : Failure<AgentMcpToolCallReceipt>(
                    AgentActionFailureCodes.CompletionAuditUnavailable,
                    "The MCP completion audit could not be confirmed.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return Failure<AgentMcpToolCallReceipt>(
                AgentActionFailureCodes.CompletionAuditUnavailable,
                "The MCP completion audit could not be confirmed.");
        }
    }

    private async ValueTask<
        AgentMcpHostResult<AgentMcpToolCallReceipt>> CompleteFailureAsync(
        AgentActionPermit permit,
        string stableCode,
        string message)
    {
        var completion = await CompleteAsync(
                permit,
                AgentActionOutcome.Failed,
                stableCode)
            .ConfigureAwait(false);
        return completion
            ?? Failure<AgentMcpToolCallReceipt>(stableCode, message);
    }

    private async ValueTask<
        AgentMcpHostResult<AgentMcpToolCallReceipt>> CompleteCancelledAsync(
        AgentActionPermit permit)
    {
        var completion = await CompleteAsync(
                permit,
                AgentActionOutcome.Cancelled,
                "caller_cancelled")
            .ConfigureAwait(false);
        return completion
            ?? Failure<AgentMcpToolCallReceipt>(
                "caller_cancelled",
                "The MCP call was cancelled before dispatch.");
    }

    private async Task<ResolvedTransportSecrets> ResolveTransportSecretsAsync(
        McpServerProfile profile,
        CancellationToken cancellationToken)
    {
        var bindings = profile.Transport switch
        {
            McpServerTransport.Stdio stdio => stdio.Environment
                .Select(variable => new SecretBinding(
                    variable.Name,
                    variable.Reference,
                    SecretBindingKind.ProcessEnvironment))
                .ToArray(),
            McpServerTransport.StreamableHttp http => [.. http.Headers
                .Select(header => new SecretBinding(
                    header.Name,
                    header.Reference,
                    SecretBindingKind.HttpHeader))],
            _ => throw new McpHostFailureException(
                "mcp_transport_unsupported",
                "The MCP profile transport is unsupported."),
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var literals = new List<char[]>(bindings.Length);
        var totalBytes = 0;
        var windowsEnvironmentBlockCodeUnits = 1;
        try
        {
            foreach (var binding in bindings)
            {
                var resolved = await _secretVault.ResolveAsync(
                        new ResolveSecretRequest(
                            binding.Reference,
                            new SecretScope(
                                SecretScopeKind.McpServer,
                                profile.Id.Value),
                            new SecretUsePurpose(
                                binding.Kind switch
                                {
                                    SecretBindingKind.ProcessEnvironment =>
                                        SecretUseKind.McpServerEnvironment,
                                    SecretBindingKind.HttpHeader =>
                                        SecretUseKind.McpServerHttpHeader,
                                    _ => throw new InvalidOperationException(),
                                },
                                profile.Id.Value)),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolved
                    is not SecretVaultResult<SecretMaterial>.Success success)
                {
                    throw new McpHostFailureException(
                        "mcp_secret_unavailable",
                        "An MCP transport secret is unavailable.");
                }

                using var material = success.Value;
                if (material.Length > MaximumEnvironmentValueBytes
                    || totalBytes
                        > MaximumEnvironmentBytes - material.Length)
                {
                    throw new McpHostFailureException(
                        "mcp_secret_limit_exceeded",
                        "The MCP transport secrets exceed their byte budget.");
                }

                totalBytes += material.Length;
                var bytes = new byte[material.Length];
                try
                {
                    material.CopyTo(bytes);
                    var chars = StrictUtf8.GetChars(bytes);
                    if (chars.Contains('\0')
                        || binding.Kind == SecretBindingKind.HttpHeader
                        && chars.Any(char.IsControl))
                    {
                        CryptographicOperations.ZeroMemory(
                            System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                                chars.AsSpan()));
                        throw new McpHostFailureException(
                            "mcp_secret_invalid",
                            "An MCP transport secret is not a valid protocol value.");
                    }

                    if (binding.Kind == SecretBindingKind.ProcessEnvironment)
                    {
                        var windowsEntryCodeUnits = checked(
                            binding.Name.Length
                            + 1
                            + chars.Length
                            + 1);
                        if (windowsEnvironmentBlockCodeUnits
                            > MaximumWindowsEnvironmentBlockCodeUnits
                                - windowsEntryCodeUnits)
                        {
                            CryptographicOperations.ZeroMemory(
                                System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                                    chars.AsSpan()));
                            throw new McpHostFailureException(
                                "mcp_secret_limit_exceeded",
                                "The MCP environment secrets exceed the process-value budget.");
                        }

                        windowsEnvironmentBlockCodeUnits +=
                            windowsEntryCodeUnits;
                    }

                    literals.Add(chars);
                    values.Add(binding.Name, new string(chars));
                }
                catch (DecoderFallbackException exception)
                {
                    throw new McpHostFailureException(
                        "mcp_secret_invalid",
                        "An MCP transport secret is not valid UTF-8.",
                        exception);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            return new ResolvedTransportSecrets(values, literals);
        }
        catch
        {
            values.Clear();
            foreach (var literal in literals)
            {
                CryptographicOperations.ZeroMemory(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        literal.AsSpan()));
            }

            throw;
        }
    }

    private bool IsProfileCurrent(
        StoredDefinition<McpServerProfile> stored) =>
        IsProfileRevisionCurrent(stored)
        && stored.Value.IsEnabled;

    private bool IsProfileRevisionCurrent(
        StoredDefinition<McpServerProfile> stored) =>
        _catalog.Snapshot.McpServerProfiles.Any(current =>
            current.Value.Id == stored.Value.Id
            && current.Revision == stored.Revision);

    private static void ValidateLaunch(McpServerProfile profile)
    {
        if (profile.Transport is not McpServerTransport.Stdio stdio)
        {
            return;
        }

        if (!Path.IsPathFullyQualified(stdio.Executable)
            || stdio.WorkingDirectory is { } workingDirectory
            && !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new McpHostFailureException(
                "mcp_launch_path_invalid",
                "MCP executable and working-directory paths must be absolute.");
        }
    }

    private static McpHostFailureException MapFailure(McpError error) =>
        new(
            StableMcpCode(error.Code),
            error.Code switch
            {
                McpErrorCode.LaunchFailed =>
                    "The MCP server process could not be started.",
                McpErrorCode.UnsupportedProtocolVersion =>
                    "The MCP server does not support the pinned protocol.",
                McpErrorCode.MissingToolsCapability =>
                    "The MCP server does not advertise tools.",
                McpErrorCode.Cancelled =>
                    "The MCP operation was cancelled.",
                _ => "The MCP server returned an invalid or unavailable tool surface.",
            });

    private static string StableMcpCode(McpErrorCode code) =>
        code switch
        {
            McpErrorCode.Cancelled => "mcp_cancelled",
            McpErrorCode.Disposed => "mcp_server_closed",
            McpErrorCode.LaunchFailed => "mcp_server_launch_failed",
            McpErrorCode.TransportClosed => "mcp_transport_closed",
            McpErrorCode.ProcessExited => "mcp_server_exited",
            McpErrorCode.TransportFailed => "mcp_transport_failed",
            McpErrorCode.MessageTooLarge => "mcp_message_too_large",
            McpErrorCode.InvalidMessage => "mcp_message_invalid",
            McpErrorCode.UnsupportedProtocolVersion =>
                "mcp_protocol_unsupported",
            McpErrorCode.MissingToolsCapability =>
                "mcp_tools_unsupported",
            McpErrorCode.RemoteError => "mcp_remote_error",
            McpErrorCode.InvalidResult => "mcp_result_invalid",
            McpErrorCode.LimitExceeded => "mcp_limit_exceeded",
            McpErrorCode.InvalidArguments => "mcp_arguments_invalid",
            McpErrorCode.ToolNotListed => "mcp_tool_not_listed",
            McpErrorCode.ToolCatalogStale => "mcp_tool_catalog_changed",
            _ => "mcp_failed",
        };

    private static string StableAuthorizationCode(
        AgentAuthorizationErrorCode code) =>
        code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired =>
                "authorization_expired",
            AgentAuthorizationErrorCode.PolicyChanged => "policy_changed",
            AgentAuthorizationErrorCode.RunCancelled => "run_cancelled",
            AgentAuthorizationErrorCode.Cancelled => "caller_cancelled",
            _ => "authorization_rejected",
        };

    private static AgentMcpHostResult<T> Failure<T>(
        string stableCode,
        string message) =>
        new AgentMcpHostResult<T>.Failure(
            new AgentMcpHostError(stableCode, message));

    private static McpServerTestResult TestFailure(
        string stableCode,
        string message,
        bool retryable) =>
        new McpServerTestResult.Failure(
            new McpServerTestError(
                stableCode,
                message,
                retryable));

    private McpServerTestResult CreateTestCancellationFailure(
        CancellationToken callerCancellation,
        CancellationToken operationCancellation)
    {
        if (callerCancellation.IsCancellationRequested)
        {
            return TestFailure(
                "mcp_test_cancelled",
                "The MCP server test was cancelled.",
                retryable: true);
        }

        if (_testShutdown.IsCancellationRequested)
        {
            return TestFailure(
                "mcp_test_unavailable",
                "MCP diagnostics are no longer available.",
                retryable: false);
        }

        return operationCancellation.IsCancellationRequested
            ? TestFailure(
                "mcp_test_timed_out",
                "The MCP server did not finish bounded discovery in time.",
                retryable: true)
            : TestFailure(
                "mcp_test_failed",
                "The MCP server test failed safely.",
                retryable: true);
    }

    private static bool IsRetryableTestFailure(string stableCode) =>
        stableCode is
            "mcp_server_launch_failed"
            or "mcp_server_closed"
            or "mcp_server_exited"
            or "mcp_transport_closed"
            or "mcp_transport_failed"
            or "mcp_cancelled"
            or "mcp_discovery_failed";

    private McpDiagnosticSession BeginDiagnostic(
        StoredDefinition<McpServerProfile> stored,
        McpServerSessionKind sessionKind,
        McpServerLifecycleState state,
        string stableCode,
        string message)
    {
        var startedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var session = new McpDiagnosticSession(
            stored.Value.Id,
            stored.Revision,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant(),
            sessionKind,
            startedAtUtc);
        PublishDiagnostic(session, state, stableCode, message);
        return session;
    }

    private bool IsAuthenticatedHuman(OperationContext context)
    {
        var principal = _approvalPrincipal.Actor;
        return context.Actor.Kind == ActorKind.Human
            && context.Actor.ClientId is { } clientId
            && string.Equals(
                context.Actor.Id.Value,
                clientId.Value,
                StringComparison.Ordinal)
            && string.Equals(
                context.Actor.Id.Value,
                principal.Id.Value,
                StringComparison.Ordinal)
            && principal.ClientId is { } principalClientId
            && principalClientId == clientId;
    }

    private async ValueTask EnsureDiagnosticsLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (_diagnosticsLoaded)
        {
            return;
        }

        await _diagnosticLoadGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        McpServerDiagnosticsSnapshot? changed = null;
        try
        {
            if (_diagnosticsLoaded)
            {
                return;
            }

            if (_diagnosticStore is not null)
            {
                ApplicationRunResult<McpServerDiagnosticsSnapshot>? read = null;
                try
                {
                    read = await _diagnosticStore.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException
                    && (exception is not OperationCanceledException
                        || !cancellationToken.IsCancellationRequested))
                {
                    _ = exception;
                }

                if (read?.IsSuccess == true && read.Value is { } stored)
                {
                    var currentRevisions = _catalog.Snapshot.McpServerProfiles
                        .ToDictionary(
                            item => item.Value.Id,
                            item => item.Revision);
                    lock (_diagnosticGate)
                    {
                        foreach (var summary in stored.Summaries)
                        {
                            if (currentRevisions.TryGetValue(
                                    summary.ProfileId,
                                    out var revision)
                                && revision == summary.Revision
                                && summary.UpdatedAtUtc
                                    >= _timeProvider.GetUtcNow().ToUniversalTime()
                                        - MaximumDiagnosticEventAge)
                            {
                                _diagnosticSummaries[summary.ProfileId] = summary;
                            }
                        }

                        changed = CaptureDiagnosticSnapshotUnsafe();
                    }
                }
            }

            _diagnosticsLoaded = true;
        }
        finally
        {
            _diagnosticLoadGate.Release();
        }

        if (changed is not null)
        {
            Changed?.Invoke(
                this,
                new McpServerDiagnosticsChangedEventArgs(changed));
        }
    }

    private async ValueTask PersistDiagnosticsAsync()
    {
        if (_diagnosticStore is null)
        {
            return;
        }

        McpServerDiagnosticsSnapshot snapshot;
        lock (_diagnosticGate)
        {
            snapshot = new McpServerDiagnosticsSnapshot(
                CaptureDiagnosticSnapshotUnsafe().Summaries,
                cleanupUncertain: false,
                cleanupUncertainAtUtc: null);
        }

        try
        {
            _ = await _diagnosticStore.WriteAsync(
                    snapshot,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            // Diagnostics persistence cannot change MCP execution semantics.
        }
    }

    private void PublishDiagnostic(
        McpDiagnosticSession session,
        McpServerLifecycleState state,
        string stableCode,
        string message,
        McpStderrDiagnostics? stderr = null)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var diagnosticEvent = new McpServerDiagnosticEvent(
            now,
            state,
            stableCode,
            message,
            stderr?.ObservedByteCount ?? 0,
            stderr?.ObservedLineCount ?? 0,
            stderr?.WasTruncated ?? false);
        McpServerDiagnosticsSnapshot snapshot;
        lock (_diagnosticGate)
        {
            PruneDiagnosticsUnsafe(now);
            IReadOnlyList<McpServerDiagnosticEvent> events;
            if (_diagnosticSummaries.TryGetValue(
                    session.ProfileId,
                    out var current)
                && current.Revision == session.Revision
                && string.Equals(
                    current.SessionId,
                    session.SessionId,
                    StringComparison.Ordinal))
            {
                var oldest = now - MaximumDiagnosticEventAge;
                events = [.. current.Events
                    .Where(item => item.OccurredAtUtc >= oldest)
                    .Append(diagnosticEvent)
                    .TakeLast(McpServerDiagnosticSummary.MaximumRetainedEvents)];
            }
            else
            {
                events = [diagnosticEvent];
            }

            _diagnosticSummaries[session.ProfileId] =
                new McpServerDiagnosticSummary(
                    session.ProfileId,
                    session.Revision,
                    session.SessionId,
                    session.SessionKind,
                    state,
                    session.StartedAtUtc,
                    now,
                    events);
            EnforceDiagnosticProfileLimitUnsafe();
            snapshot = CaptureDiagnosticSnapshotUnsafe();
        }

        Changed?.Invoke(
            this,
            new McpServerDiagnosticsChangedEventArgs(snapshot));
    }

    private async ValueTask CompleteDiagnosticSessionAsync(
        McpDiagnosticSession session,
        McpStderrDiagnostics stderr,
        bool cleanupUncertain)
    {
        if (cleanupUncertain)
        {
            MarkCleanupUncertain();
            PublishDiagnostic(
                session,
                McpServerLifecycleState.CleanupUncertain,
                "mcp_cleanup_uncertain",
                "MCP process cleanup could not be confirmed.",
                stderr);
            await PersistDiagnosticsAsync().ConfigureAwait(false);
            return;
        }

        PublishDiagnostic(
            session,
            McpServerLifecycleState.Stopped,
            "mcp_stopped",
            stderr.ObservedByteCount == 0 && !stderr.ReadFailed
                ? "The MCP process stopped cleanly."
                : "The MCP process stopped; bounded stderr shape metadata was retained.",
            stderr);
        await PersistDiagnosticsAsync().ConfigureAwait(false);
    }

    private McpServerDiagnosticsSnapshot CaptureDiagnosticSnapshotUnsafe() =>
        new(
            [.. _diagnosticSummaries.Values
                .OrderByDescending(summary => summary.UpdatedAtUtc)
                .ThenBy(summary => summary.ProfileId.Value, StringComparer.Ordinal)],
            IsCleanupUncertain,
            _cleanupUncertainAtUtc);

    private void PruneDiagnosticsUnsafe(DateTimeOffset now)
    {
        var oldest = now - MaximumDiagnosticEventAge;
        foreach (var profileId in (McpServerProfileId[])[.. _diagnosticSummaries
                     .Where(item => item.Value.UpdatedAtUtc < oldest)
                     .Select(item => item.Key)])
        {
            _diagnosticSummaries.Remove(profileId);
        }
    }

    private void EnforceDiagnosticProfileLimitUnsafe()
    {
        while (_diagnosticSummaries.Count
               > McpServerDiagnosticsSnapshot.MaximumRetainedProfiles)
        {
            var oldest = _diagnosticSummaries
                .OrderBy(item => item.Value.UpdatedAtUtc)
                .ThenBy(item => item.Key.Value, StringComparer.Ordinal)
                .First();
            _diagnosticSummaries.Remove(oldest.Key);
        }
    }

    private bool IsCleanupUncertain =>
        Volatile.Read(ref _cleanupUncertain) != 0;

    private void MarkCleanupUncertain()
    {
        if (Interlocked.Exchange(ref _cleanupUncertain, 1) != 0)
        {
            return;
        }

        McpServerDiagnosticsSnapshot snapshot;
        lock (_diagnosticGate)
        {
            _cleanupUncertainAtUtc =
                _timeProvider.GetUtcNow().ToUniversalTime();
            snapshot = CaptureDiagnosticSnapshotUnsafe();
        }

        Changed?.Invoke(
            this,
            new McpServerDiagnosticsChangedEventArgs(snapshot));
    }

    private static McpSessionOptions CreateDefaultOptions() =>
        new()
        {
            MaxTools = MaximumToolsPerRun,
            MaxToolSchemaBytes =
                AgentMcpToolManifest.MaximumInputSchemaBytes,
            MaxToolArgumentsBytes =
                AgentMcpToolCallRequest.MaximumArgumentsBytes,
            MaxToolResultBytes =
                AgentMcpToolCallReceipt.MaximumProviderJsonBytes,
            MaxJsonDepth = AgentMcpToolCallRequest.MaximumJsonDepth,
            MaxJsonNodes = AgentMcpToolCallRequest.MaximumJsonNodes,
        };

    private static AgentActionDigest CreateOpaqueToolIdentity(
        ReadOnlySpan<byte> key,
        McpServerProfileId profileId,
        string toolName)
    {
        var profileBytes = StrictUtf8.GetBytes(profileId.Value);
        var toolBytes = StrictUtf8.GetBytes(toolName);
        try
        {
            using var hash = IncrementalHash.CreateHMAC(
                HashAlgorithmName.SHA256,
                key);
            hash.AppendData("asura.mcp-tool-identity.v1"u8);
            hash.AppendData([0]);
            hash.AppendData(profileBytes);
            hash.AppendData([0]);
            hash.AppendData(toolBytes);
            return new AgentActionDigest(
                Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(profileBytes);
            CryptographicOperations.ZeroMemory(toolBytes);
        }
    }

    private bool IsValidProviderReceipt(
        AgentMcpToolCallReceipt receipt)
    {
        var bytes = Encoding.UTF8.GetBytes(receipt.ProviderJson);
        try
        {
            if (!McpJsonBudget.TryValidateDocument(
                    bytes,
                    _clientOptions.MaxJsonDepth,
                    _clientOptions.MaxJsonNodes,
                    out var document))
            {
                return false;
            }

            var validDocument = document!;
            using (validDocument)
            {
                return validDocument.RootElement.ValueKind
                    == System.Text.Json.JsonValueKind.Object;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class McpCatalogState
    {
        private readonly IReadOnlyDictionary<
            McpServerProfileId,
            long> _profiles;

        private McpCatalogState(
            IReadOnlyDictionary<
                McpServerProfileId,
                long> profiles)
        {
            _profiles = profiles;
        }

        public static McpCatalogState Capture(
            DefinitionCatalogSnapshot snapshot) =>
            new(
                snapshot.McpServerProfiles
                    .Where(stored =>
                        stored.Value.IsEnabled
                        && stored.Value.IsTrusted
                        && stored.Value.EnabledTools.Count > 0)
                    .ToDictionary(
                    stored => stored.Value.Id,
                    stored => stored.Revision));

        public bool HasSameAuthorityFingerprint(
            McpCatalogState other)
        {
            if (_profiles.Count != other._profiles.Count)
            {
                return false;
            }

            foreach (var pair in _profiles)
            {
                if (!other._profiles.TryGetValue(
                        pair.Key,
                        out var candidate)
                    || candidate != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        public bool Matches(
            IReadOnlyList<ProfileSession> profiles)
        {
            if (profiles.Count != _profiles.Count)
            {
                return false;
            }

            return profiles.All(profile =>
                _profiles.TryGetValue(
                    profile.Stored.Value.Id,
                    out var current)
                && current == profile.Stored.Revision);
        }
    }

    private readonly record struct McpTestProfileFingerprint(
        long Revision,
        bool IsEnabled);

    private readonly record struct McpDiagnosticSession(
        McpServerProfileId ProfileId,
        long Revision,
        string SessionId,
        McpServerSessionKind SessionKind,
        DateTimeOffset StartedAtUtc);

    private sealed class RunSession : IAsyncDisposable
    {
        private readonly IReadOnlyDictionary<
            string,
            ProfileToolBinding> _tools;
        private readonly IReadOnlyList<ProfileSession> _profiles;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly CancellationTokenSource _shutdown = new();
        private int _closing;

        public RunSession(
            AgentMcpRunManifest manifest,
            ActorDescriptor agent,
            WorkspaceInstanceId workspaceId,
            long policyGeneration,
            CancellationToken authorityRevocationToken,
            IReadOnlyList<ProfileSession> profiles)
        {
            Manifest = manifest;
            Agent = agent;
            WorkspaceId = workspaceId;
            PolicyGeneration = policyGeneration;
            AuthorityRevocationToken = authorityRevocationToken;
            _profiles = new ReadOnlyCollection<ProfileSession>(
                [.. profiles]);
            _tools = profiles
                .SelectMany(profile => profile.Manifests.Select(
                    tool => new ProfileToolBinding(
                        profile,
                        tool,
                        profile.ProtocolToolNames[tool.ProviderAlias])))
                .ToDictionary(
                    binding => binding.Manifest.ProviderAlias,
                    StringComparer.Ordinal);
        }

        public AgentMcpRunManifest Manifest { get; }

        public ActorDescriptor Agent { get; }

        public WorkspaceInstanceId WorkspaceId { get; }

        public long PolicyGeneration { get; }

        public CancellationToken AuthorityRevocationToken { get; }

        public CancellationToken ShutdownToken => _shutdown.Token;

        public bool IsClosing => Volatile.Read(ref _closing) != 0;

        public bool CleanupUncertain { get; private set; }

        public async ValueTask<bool> TryEnterAsync(
            CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (IsClosing)
            {
                _operationGate.Release();
                return false;
            }

            return true;
        }

        public void Exit() => _operationGate.Release();

        public bool TryResolve(
            string providerAlias,
            out ProfileSession profile,
            out AgentMcpToolManifest manifest,
            out string protocolToolName)
        {
            if (_tools.TryGetValue(providerAlias, out var binding))
            {
                profile = binding.Profile;
                manifest = binding.Manifest;
                protocolToolName = binding.ProtocolToolName;
                return true;
            }

            profile = null!;
            manifest = null!;
            protocolToolName = null!;
            return false;
        }

        public bool ReferencesSecret(SecretRef reference) =>
            _profiles.Any(profile =>
                profile.Stored.Value.Transport switch
                {
                    McpServerTransport.Stdio stdio =>
                        stdio.Environment.Any(variable =>
                            variable.Reference == reference),
                    McpServerTransport.StreamableHttp http =>
                        http.Headers.Any(header =>
                            header.Reference == reference),
                    _ => throw new InvalidOperationException(
                        "The MCP transport is unsupported."),
                });

        public bool HasCurrentProfiles(
            McpCatalogState state) =>
            state.Matches(_profiles);

        public void BeginClose()
        {
            if (Interlocked.Exchange(ref _closing, 1) == 0)
            {
                try
                {
                    _shutdown.Cancel();
                }
                catch (AggregateException)
                {
                    // Closing remains effective when a callback fails.
                }
                catch (ObjectDisposedException)
                {
                    // Disposal already completed.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            BeginClose();
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var profile in _profiles)
                {
                    await profile.DisposeAsync().ConfigureAwait(false);
                    CleanupUncertain |= profile.CleanupUncertain;
                }
            }
            finally
            {
                _operationGate.Release();
                _operationGate.Dispose();
                _shutdown.Dispose();
            }
        }
    }

    private sealed record ProfileToolBinding(
        ProfileSession Profile,
        AgentMcpToolManifest Manifest,
        string ProtocolToolName);

    private sealed class ProfileSession(
        StoredDefinition<McpServerProfile> stored,
        McpClientSession client,
        McpSecretRedactor redactor,
        IReadOnlyList<AgentMcpToolManifest> manifests,
        IReadOnlyDictionary<string, string> protocolToolNames,
        int discoveredToolCount,
        McpDiagnosticSession diagnostic,
        Func<McpDiagnosticSession, McpStderrDiagnostics, bool, ValueTask>
            diagnosticCompletion) : IAsyncDisposable
    {
        private int _disposeStarted;

        public StoredDefinition<McpServerProfile> Stored { get; } = stored;

        public McpClientSession Client { get; } = client;

        public McpSecretRedactor Redactor { get; } = redactor;

        public IReadOnlyList<AgentMcpToolManifest> Manifests { get; } =
            new ReadOnlyCollection<AgentMcpToolManifest>(
                [.. manifests]);

        public IReadOnlyDictionary<string, string> ProtocolToolNames { get; } =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    protocolToolNames,
                    StringComparer.Ordinal));

        public int DiscoveredToolCount { get; } = discoveredToolCount;

        public McpDiagnosticSession Diagnostic { get; } = diagnostic;

        public bool CleanupUncertain { get; private set; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            try
            {
                await Client.DisposeAsync().ConfigureAwait(false);
                CleanupUncertain = Client.CleanupUncertain;
            }
            finally
            {
                try
                {
                    await diagnosticCompletion(
                            Diagnostic,
                            Client.StandardErrorDiagnostics,
                            CleanupUncertain)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Redactor.Dispose();
                }
            }
        }
    }

    private readonly record struct SecretBinding(
        string Name,
        SecretRef Reference,
        SecretBindingKind Kind);

    private enum SecretBindingKind
    {
        ProcessEnvironment,
        HttpHeader,
    }

    private sealed class ResolvedTransportSecrets(
        Dictionary<string, string> values,
        List<char[]> literals) : IDisposable
    {
        private bool _redactorDetached;

        public IReadOnlyDictionary<string, string> Values { get; } =
            new ReadOnlyDictionary<string, string>(values);

        public McpSecretRedactor Redactor { get; } = new(literals);

        public void DropTransportValues() => values.Clear();

        public McpSecretRedactor DetachRedactor()
        {
            _redactorDetached = true;
            return Redactor;
        }

        public void Dispose()
        {
            values.Clear();
            if (!_redactorDetached)
            {
                Redactor.Dispose();
            }
        }
    }

    private sealed class McpHostFailureException(
        string stableCode,
        string message,
        Exception? innerException = null) : Exception(message, innerException)
    {
        public string StableCode { get; } = stableCode;

    }
}
