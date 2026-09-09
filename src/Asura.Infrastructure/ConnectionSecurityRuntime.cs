using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Owns SSH identity review, atomic trust, and credential-backed diagnostics. Candidate public-key
/// bytes and credential material never cross this Infrastructure boundary.
/// </summary>
public sealed class ConnectionSecurityRuntime : IConnectionSecurityRuntime
{
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(5);
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly SshKnownHostStore _knownHosts;
    private readonly OpenSshKnownHostTrustSource _openSshKnownHosts;
    private readonly ISshHostKeyScanner _scanner;
    private readonly ISshAuthenticationProbe _authenticationProbe;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<SshHostKeyReviewId, PendingReview> _pendingReviews = new();

    public ConnectionSecurityRuntime(
        IConnectionRuntime connectionRuntime,
        ISecretVault secretVault,
        SshKnownHostStore knownHosts,
        TimeProvider timeProvider,
        IWorkspaceNetworkConnector? networkConnector = null)
        : this(
            connectionRuntime,
            knownHosts,
            new SshNetHostKeyScanner(networkConnector),
            new SshNetAuthenticationProbe(secretVault, knownHosts, networkConnector),
            timeProvider,
            OpenSshKnownHostTrustSource.CreateDefault())
    {
    }

    internal ConnectionSecurityRuntime(
        IConnectionRuntime connectionRuntime,
        SshKnownHostStore knownHosts,
        ISshHostKeyScanner scanner,
        ISshAuthenticationProbe authenticationProbe,
        TimeProvider timeProvider,
        OpenSshKnownHostTrustSource? openSshKnownHosts = null)
    {
        _connectionRuntime = connectionRuntime ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _knownHosts = knownHosts ?? throw new ArgumentNullException(nameof(knownHosts));
        _openSshKnownHosts = openSshKnownHosts ?? new OpenSshKnownHostTrustSource([]);
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _authenticationProbe = authenticationProbe ?? throw new ArgumentNullException(nameof(authenticationProbe));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> PrepareSshHostKeyAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Endpoint is not ConnectionEndpoint.Ssh)
        {
            return FailReview(ConnectionRuntimeErrorCode.InvalidProfile);
        }

        SshHostKeyCandidate? pinned;
        try
        {
            pinned = await _knownHosts.ReadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FailReview(ConnectionRuntimeErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is
            IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return FailReview(ConnectionRuntimeErrorCode.ProcessFailed);
        }

        // The OpenSSH launch receives this exact per-connection known-host file and applies strict
        // checking. Re-scanning here would create a second connection with different SSH behavior
        // before the authoritative client is even allowed to start.
        return pinned is not null
            ? ConnectionRuntimeResult<SshHostKeyReview>.Succeed(CreatePinnedReview(profile, pinned))
            : await InspectSshHostKeyAsync(profile, progress, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Endpoint is not ConnectionEndpoint.Ssh endpoint)
        {
            return FailReview(ConnectionRuntimeErrorCode.InvalidProfile);
        }

        Report(progress, ConnectionProgressStage.InspectingHostKey);
        var scanned = await _scanner.ScanAsync(profile, cancellationToken).ConfigureAwait(false);
        if (scanned is ConnectionRuntimeResult<SshHostKeyCandidate>.Failure scanFailure)
        {
            return ConnectionRuntimeResult<SshHostKeyReview>.Fail(scanFailure.Error);
        }

        var candidate = ((ConnectionRuntimeResult<SshHostKeyCandidate>.Success)scanned).Value;
        SshHostKeyCandidate? trusted;
        try
        {
            trusted = await _knownHosts.ReadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            if (trusted is null
                && profile.HostKeyPolicy != SshHostKeyPolicy.InsecureIgnore
                && await _openSshKnownHosts.ContainsAsync(endpoint, candidate, cancellationToken)
                    .ConfigureAwait(false))
            {
                var imported = await _knownHosts.WriteAsync(
                        profile.Id,
                        candidate,
                        expectedCurrent: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                trusted = imported == SshKnownHostWriteResult.ChangedSinceReview
                    ? await _knownHosts.ReadAsync(profile.Id, cancellationToken).ConfigureAwait(false)
                    : candidate;
            }
        }
        catch (OperationCanceledException)
        {
            return FailReview(ConnectionRuntimeErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return FailReview(ConnectionRuntimeErrorCode.ProcessFailed);
        }

        var disposition = profile.HostKeyPolicy switch
        {
            SshHostKeyPolicy.InsecureIgnore => SshHostKeyDisposition.VerificationDisabled,
            _ when trusted is null => SshHostKeyDisposition.Unknown,
            _ when trusted == candidate => SshHostKeyDisposition.Trusted,
            _ => SshHostKeyDisposition.Changed,
        };
        var now = _timeProvider.GetUtcNow();
        var review = new SshHostKeyReview(
            SshHostKeyReviewId.New(),
            profile.Id,
            FormatEndpoint(endpoint),
            disposition,
            candidate.Identity,
            disposition is SshHostKeyDisposition.Trusted or SshHostKeyDisposition.Changed
                ? trusted!.Identity
                : null,
            now + ReviewLifetime);
        if (disposition is SshHostKeyDisposition.Unknown or SshHostKeyDisposition.Changed)
        {
            _pendingReviews[review.Id] = new PendingReview(review, candidate, trusted);
        }
        RemoveExpiredReviews(now);
        return ConnectionRuntimeResult<SshHostKeyReview>.Succeed(review);
    }

    public async ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
        SshHostKeyTrustRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_pendingReviews.TryGetValue(request.ReviewId, out var pending)
            || pending.Review.ConnectionId != request.ConnectionId
            || pending.Review.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            _pendingReviews.TryRemove(request.ReviewId, out _);
            return FailReview(ConnectionRuntimeErrorCode.HostKeyReviewExpired);
        }

        var validAction = (pending.Review.Disposition, request.Action) switch
        {
            (SshHostKeyDisposition.Unknown, SshHostKeyTrustAction.TrustNew) => true,
            (SshHostKeyDisposition.Changed, SshHostKeyTrustAction.ReplaceChanged) => true,
            _ => false,
        };
        if (!validAction)
        {
            return FailReview(pending.Review.Disposition == SshHostKeyDisposition.Changed
                ? ConnectionRuntimeErrorCode.HostKeyChanged
                : ConnectionRuntimeErrorCode.InvalidProfile);
        }

        SshKnownHostWriteResult writeResult;
        try
        {
            writeResult = await _knownHosts.WriteAsync(
                    request.ConnectionId,
                    pending.Candidate,
                    pending.TrustedCandidate,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FailReview(ConnectionRuntimeErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return FailReview(ConnectionRuntimeErrorCode.ProcessFailed);
        }

        if (writeResult == SshKnownHostWriteResult.ChangedSinceReview)
        {
            _pendingReviews.TryRemove(request.ReviewId, out _);
            return FailReview(ConnectionRuntimeErrorCode.HostKeyChanged);
        }

        _pendingReviews.TryRemove(request.ReviewId, out _);
        var now = _timeProvider.GetUtcNow();
        var trustedReview = new SshHostKeyReview(
            SshHostKeyReviewId.New(),
            pending.Review.ConnectionId,
            pending.Review.Endpoint,
            SshHostKeyDisposition.Trusted,
            pending.Candidate.Identity,
            pending.Candidate.Identity,
            now + ReviewLifetime);
        return ConnectionRuntimeResult<SshHostKeyReview>.Succeed(trustedReview);
    }

    public async ValueTask<ConnectionRuntimeResult<ConnectionDiagnosticsReport>> DiagnoseAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var items = new List<ConnectionDiagnosticItem>
        {
            Passed(
                ConnectionDiagnosticStage.Profile,
                "connection_profile_valid",
                "The durable connection profile is valid."),
        };

        var planResult = await _connectionRuntime.PlanOpenAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        if (planResult is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure planFailure)
        {
            items.Add(Failed(StageFor(planFailure.Error), planFailure.Error));
            return ReportResult(profile, items, failure: planFailure.Error);
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)planResult).Value;
        items.Add(Passed(
            ConnectionDiagnosticStage.Runtime,
            "connection_runtime_available",
            "The required connection runtime is available."));
        items.Add(Passed(
            ConnectionDiagnosticStage.Credentials,
            plan.SecretRequirements.Count == 0
                ? "connection_credentials_not_required"
                : "connection_credential_references_available",
            plan.SecretRequirements.Count == 0
                ? "This profile does not require a stored credential."
                : "Every credential reference is available for this connection scope."));

        SshHostKeyReview? hostKeyReview = null;
        if (profile.ConnectionKind == ConnectionKind.Ssh)
        {
            SshHostKeyCandidate? pinnedHostKey = null;
            if (profile.HostKeyPolicy != SshHostKeyPolicy.InsecureIgnore)
            {
                try
                {
                    pinnedHostKey = await _knownHosts.ReadAsync(profile.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var error = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled);
                    items.Add(Failed(ConnectionDiagnosticStage.HostKey, error));
                    return ReportResult(profile, items, failure: error);
                }
                catch (Exception exception) when (exception is
                    IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    var error = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed);
                    items.Add(Failed(ConnectionDiagnosticStage.HostKey, error));
                    return ReportResult(profile, items, failure: error);
                }
            }

            if (pinnedHostKey is not null)
            {
                hostKeyReview = CreatePinnedReview(profile, pinnedHostKey);
            }
            else
            {
                var inspection = await InspectSshHostKeyAsync(profile, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (inspection is ConnectionRuntimeResult<SshHostKeyReview>.Failure inspectionFailure)
                {
                    items.Add(Failed(ConnectionDiagnosticStage.HostKey, inspectionFailure.Error));
                    return ReportResult(profile, items, failure: inspectionFailure.Error);
                }

                hostKeyReview = ((ConnectionRuntimeResult<SshHostKeyReview>.Success)inspection).Value;
            }

            switch (hostKeyReview.Disposition)
            {
                case SshHostKeyDisposition.Unknown:
                    {
                        var error = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.UnknownHostKey);
                        items.Add(Failed(ConnectionDiagnosticStage.HostKey, error));
                        return ReportResult(profile, items, failure: error, hostKeyReview: hostKeyReview);
                    }
                case SshHostKeyDisposition.Changed:
                    {
                        var error = ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.HostKeyChanged);
                        items.Add(Failed(ConnectionDiagnosticStage.HostKey, error));
                        return ReportResult(profile, items, failure: error, hostKeyReview: hostKeyReview);
                    }
                case SshHostKeyDisposition.VerificationDisabled:
                case SshHostKeyDisposition.Trusted:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        Report(progress, ConnectionProgressStage.Authenticating);
        var testResult = profile.Authentication is ConnectionAuthentication.Password
            or ConnectionAuthentication.PrivateKey
            ? await _authenticationProbe.AuthenticateAsync(profile, cancellationToken).ConfigureAwait(false)
            : await _connectionRuntime.TestAsync(profile, progress, cancellationToken).ConfigureAwait(false);
        if (testResult is ConnectionRuntimeResult<ConnectionTestReport>.Failure testFailure)
        {
            if (profile.ConnectionKind == ConnectionKind.Ssh
                && testFailure.Error.Code is
                    ConnectionRuntimeErrorCode.HostKeyChanged or
                    ConnectionRuntimeErrorCode.UnknownHostKey)
            {
                var inspection = await InspectSshHostKeyAsync(profile, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (inspection is ConnectionRuntimeResult<SshHostKeyReview>.Success inspected)
                {
                    hostKeyReview = inspected.Value;
                }

                items.Add(Failed(ConnectionDiagnosticStage.HostKey, testFailure.Error));
                return ReportResult(
                    profile,
                    items,
                    failure: testFailure.Error,
                    hostKeyReview: hostKeyReview);
            }

            AddHostKeyDiagnostic(items, hostKeyReview);
            items.Add(Failed(
                profile.ConnectionKind == ConnectionKind.Ssh
                    ? ConnectionDiagnosticStage.Authentication
                    : ConnectionDiagnosticStage.Endpoint,
                testFailure.Error));
            return ReportResult(
                profile,
                items,
                failure: testFailure.Error,
                hostKeyReview: hostKeyReview);
        }

        var test = ((ConnectionRuntimeResult<ConnectionTestReport>.Success)testResult).Value;
        AddHostKeyDiagnostic(items, hostKeyReview);
        items.Add(Passed(
            profile.ConnectionKind == ConnectionKind.Ssh
                ? ConnectionDiagnosticStage.Authentication
                : ConnectionDiagnosticStage.Endpoint,
            "connection_endpoint_verified",
            VerificationMessage(test.Verification)));
        return ReportResult(
            profile,
            items,
            verification: test.Verification,
            hostKeyReview: hostKeyReview);
    }

    internal SshKnownHostBinding KnownHostBinding(ConnectionId connectionId) =>
        _knownHosts.Binding(connectionId);

    private ConnectionRuntimeResult<ConnectionDiagnosticsReport> ReportResult(
        ConnectionProfile profile,
        IReadOnlyList<ConnectionDiagnosticItem> items,
        ConnectionTestVerification? verification = null,
        ConnectionRuntimeError? failure = null,
        SshHostKeyReview? hostKeyReview = null) =>
        ConnectionRuntimeResult<ConnectionDiagnosticsReport>.Succeed(new ConnectionDiagnosticsReport(
            profile.Id,
            profile.ConnectionKind,
            _timeProvider.GetUtcNow(),
            items,
            verification,
            failure,
            hostKeyReview));

    private void RemoveExpiredReviews(DateTimeOffset now)
    {
        foreach (var (id, pending) in _pendingReviews)
        {
            if (pending.Review.ExpiresAtUtc <= now)
            {
                _pendingReviews.TryRemove(id, out _);
            }
        }
    }

    private SshHostKeyReview CreatePinnedReview(
        ConnectionProfile profile,
        SshHostKeyCandidate pinned)
    {
        var endpoint = (ConnectionEndpoint.Ssh)profile.Endpoint;
        return new SshHostKeyReview(
            SshHostKeyReviewId.New(),
            profile.Id,
            FormatEndpoint(endpoint),
            SshHostKeyDisposition.Trusted,
            pinned.Identity,
            pinned.Identity,
            _timeProvider.GetUtcNow() + ReviewLifetime);
    }

    private static void AddHostKeyDiagnostic(
        ICollection<ConnectionDiagnosticItem> items,
        SshHostKeyReview? review)
    {
        switch (review?.Disposition)
        {
            case null:
                break;
            case SshHostKeyDisposition.VerificationDisabled:
                items.Add(new ConnectionDiagnosticItem(
                    ConnectionDiagnosticStage.HostKey,
                    ConnectionDiagnosticStatus.Warning,
                    "connection_host_key_verification_disabled",
                    "Host-key verification is explicitly disabled for this profile."));
                break;
            case SshHostKeyDisposition.Trusted:
                items.Add(Passed(
                    ConnectionDiagnosticStage.HostKey,
                    "connection_host_key_trusted",
                    "The trusted SSH host key is pinned and enforced for this connection."));
                break;
            case SshHostKeyDisposition.Unknown:
            case SshHostKeyDisposition.Changed:
                throw new InvalidOperationException(
                    "Untrusted SSH host keys must stop diagnostics before authentication.");
            default:
                throw new ArgumentOutOfRangeException(nameof(review), review.Disposition, null);
        }
    }

    private static ConnectionDiagnosticStage StageFor(ConnectionRuntimeError error) => error.Code switch
    {
        ConnectionRuntimeErrorCode.SecretVaultUnavailable or
            ConnectionRuntimeErrorCode.SecretNotFound or
            ConnectionRuntimeErrorCode.SecretAccessDenied or
            ConnectionRuntimeErrorCode.SecretInvalid or
            ConnectionRuntimeErrorCode.SecretVaultFailure or
            ConnectionRuntimeErrorCode.AuthenticationRequired => ConnectionDiagnosticStage.Credentials,
        _ => ConnectionDiagnosticStage.Runtime,
    };

    private static ConnectionDiagnosticItem Passed(
        ConnectionDiagnosticStage stage,
        string stableCode,
        string message) =>
        new(stage, ConnectionDiagnosticStatus.Passed, stableCode, message);

    private static ConnectionDiagnosticItem Failed(
        ConnectionDiagnosticStage stage,
        ConnectionRuntimeError error) =>
        new(stage, ConnectionDiagnosticStatus.Failed, error.StableCode, error.Message);

    private static string VerificationMessage(ConnectionTestVerification verification) => verification switch
    {
        ConnectionTestVerification.RuntimeAvailable => "The required runtime is available.",
        ConnectionTestVerification.ConfigurationValidated => "The connection configuration is valid.",
        ConnectionTestVerification.EndpointAuthenticated => "The SSH endpoint accepted authentication.",
        ConnectionTestVerification.ContainerReachable => "The Docker container is reachable.",
        ConnectionTestVerification.DistributionReachable => "The WSL distribution is reachable.",
        _ => throw new ArgumentOutOfRangeException(nameof(verification), verification, null),
    };

    private static string FormatEndpoint(ConnectionEndpoint.Ssh endpoint) =>
        $"{endpoint.Host}:{endpoint.Port}";

    private static void Report(
        IProgress<ConnectionProgress>? progress,
        ConnectionProgressStage stage)
    {
        var update = stage switch
        {
            ConnectionProgressStage.InspectingHostKey => new ConnectionProgress(
                stage,
                "connection_inspecting_host_key",
                "Inspecting the remote SSH host key."),
            ConnectionProgressStage.Authenticating => new ConnectionProgress(
                stage,
                "connection_authenticating",
                "Authenticating to the connection endpoint."),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };
        progress?.Report(update);
    }

    private static ConnectionRuntimeResult<SshHostKeyReview> FailReview(
        ConnectionRuntimeErrorCode code) =>
        ConnectionRuntimeResult<SshHostKeyReview>.Fail(ConnectionRuntimeError.Create(code));

    private sealed record PendingReview(
        SshHostKeyReview Review,
        SshHostKeyCandidate Candidate,
        SshHostKeyCandidate? TrustedCandidate);
}
