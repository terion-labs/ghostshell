using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class ConnectionSecurityRuntimeTests : IDisposable
{
    private readonly string _knownHostsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-known-hosts-{Guid.NewGuid():N}");

    [Fact]
    public async Task Unknown_key_can_be_trusted_and_is_then_reported_as_trusted()
    {
        var candidate = Candidate(1);
        var store = new SshKnownHostStore(_knownHostsDirectory);
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(store, vault, new FixedHostKeyScanner(candidate));
        var profile = SshProfile();

        var unknown = Success(await runtime.InspectSshHostKeyAsync(
            profile,
            null,
            CancellationToken.None));
        var trusted = Success(await runtime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(
                unknown.Id,
                profile.Id,
                SshHostKeyTrustAction.TrustNew),
            CancellationToken.None));
        var inspectedAgain = Success(await runtime.InspectSshHostKeyAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(SshHostKeyDisposition.Unknown, unknown.Disposition);
        Assert.Equal(SshHostKeyDisposition.Trusted, trusted.Disposition);
        Assert.Equal(SshHostKeyDisposition.Trusted, inspectedAgain.Disposition);
        Assert.Equal(candidate.Identity, inspectedAgain.Trusted);
        Assert.True(File.Exists(store.Binding(profile.Id).FilePath));
    }

    [Fact]
    public async Task Matching_user_open_ssh_key_bootstraps_the_connection_pin()
    {
        var candidate = Candidate(9);
        var openSshFile = Path.Combine(_knownHostsDirectory, "user-known-hosts");
        Directory.CreateDirectory(_knownHostsDirectory);
        await File.WriteAllTextAsync(
            openSshFile,
            $"host.example {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}\n");
        var store = new SshKnownHostStore(_knownHostsDirectory);
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(
            store,
            vault,
            new FixedHostKeyScanner(candidate),
            openSshKnownHosts: new OpenSshKnownHostTrustSource([openSshFile]));
        var profile = SshProfile("open-ssh-bootstrap");

        var review = Success(await runtime.InspectSshHostKeyAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(SshHostKeyDisposition.Trusted, review.Disposition);
        Assert.Equal(candidate.Identity, review.Trusted);
        Assert.Equal(candidate, await store.ReadAsync(profile.Id, CancellationToken.None));
        Assert.True(File.Exists(store.Binding(profile.Id).FilePath));
    }

    [Fact]
    public async Task Different_user_open_ssh_key_does_not_bootstrap_trust()
    {
        var candidate = Candidate(10);
        var different = Candidate(11);
        var openSshFile = Path.Combine(_knownHostsDirectory, "user-known-hosts");
        Directory.CreateDirectory(_knownHostsDirectory);
        await File.WriteAllTextAsync(
            openSshFile,
            $"host.example {different.Identity.Algorithm} {different.PublicKeyBase64}\n");
        var store = new SshKnownHostStore(_knownHostsDirectory);
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(
            store,
            vault,
            new FixedHostKeyScanner(candidate),
            openSshKnownHosts: new OpenSshKnownHostTrustSource([openSshFile]));
        var profile = SshProfile("open-ssh-mismatch");

        var review = Success(await runtime.InspectSshHostKeyAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(SshHostKeyDisposition.Unknown, review.Disposition);
        Assert.Null(await store.ReadAsync(profile.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Existing_connection_pin_remains_authoritative_over_user_open_ssh_key()
    {
        var pinned = Candidate(12);
        var presented = Candidate(13);
        var openSshFile = Path.Combine(_knownHostsDirectory, "user-known-hosts");
        Directory.CreateDirectory(_knownHostsDirectory);
        await File.WriteAllTextAsync(
            openSshFile,
            $"host.example {presented.Identity.Algorithm} {presented.PublicKeyBase64}\n");
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var profile = SshProfile("existing-pin");
        Assert.Equal(
            SshKnownHostWriteResult.Stored,
            await store.WriteAsync(profile.Id, pinned, null, CancellationToken.None));
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(
            store,
            vault,
            new FixedHostKeyScanner(presented),
            openSshKnownHosts: new OpenSshKnownHostTrustSource([openSshFile]));

        var review = Success(await runtime.InspectSshHostKeyAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(SshHostKeyDisposition.Changed, review.Disposition);
        Assert.Equal(pinned.Identity, review.Trusted);
        Assert.Equal(pinned, await store.ReadAsync(profile.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Preparing_a_pinned_connection_does_not_open_a_redundant_scanner_session()
    {
        var pinned = Candidate(14);
        var scanner = new FixedHostKeyScanner(pinned);
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var profile = SshProfile("prepared-pin");
        Assert.Equal(
            SshKnownHostWriteResult.Stored,
            await store.WriteAsync(profile.Id, pinned, null, CancellationToken.None));
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(store, vault, scanner);

        var review = Success(await runtime.PrepareSshHostKeyAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(SshHostKeyDisposition.Trusted, review.Disposition);
        Assert.Equal(pinned.Identity, review.Presented);
        Assert.Equal(pinned.Identity, review.Trusted);
        Assert.Equal(0, scanner.CallCount);
    }

    [Fact]
    public async Task Changed_key_requires_the_explicit_replacement_action()
    {
        var first = Candidate(1);
        var changed = Candidate(2);
        var scanner = new FixedHostKeyScanner(first);
        var store = new SshKnownHostStore(_knownHostsDirectory);
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(store, vault, scanner);
        var profile = SshProfile();
        var initial = Success(await runtime.InspectSshHostKeyAsync(profile, null, CancellationToken.None));
        _ = Success(await runtime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(initial.Id, profile.Id, SshHostKeyTrustAction.TrustNew),
            CancellationToken.None));
        scanner.Candidate = changed;

        var review = Success(await runtime.InspectSshHostKeyAsync(profile, null, CancellationToken.None));
        var wrongAction = Failure(await runtime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(review.Id, profile.Id, SshHostKeyTrustAction.TrustNew),
            CancellationToken.None));
        var replaced = Success(await runtime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(review.Id, profile.Id, SshHostKeyTrustAction.ReplaceChanged),
            CancellationToken.None));

        Assert.Equal(SshHostKeyDisposition.Changed, review.Disposition);
        Assert.Equal(first.Identity, review.Trusted);
        Assert.Equal(ConnectionRuntimeErrorCode.HostKeyChanged, wrongAction.Code);
        Assert.Equal(changed.Identity, replaced.Trusted);
    }

    [Fact]
    public async Task Trust_is_compare_and_swap_and_rejects_a_stale_review()
    {
        var reviewed = Candidate(1);
        var concurrentlyStored = Candidate(2);
        var store = new SshKnownHostStore(_knownHostsDirectory);
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(store, vault, new FixedHostKeyScanner(reviewed));
        var profile = SshProfile();
        var review = Success(await runtime.InspectSshHostKeyAsync(profile, null, CancellationToken.None));
        Assert.Equal(
            SshKnownHostWriteResult.Stored,
            await store.WriteAsync(profile.Id, concurrentlyStored, null, CancellationToken.None));

        var failure = Failure(await runtime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(review.Id, profile.Id, SshHostKeyTrustAction.TrustNew),
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.HostKeyChanged, failure.Code);
        Assert.Equal(concurrentlyStored.Identity, (await store.ReadAsync(profile.Id, CancellationToken.None))!.Identity);
    }

    [Fact]
    public async Task Expired_review_cannot_be_replayed()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new SshKnownHostStore(_knownHostsDirectory);
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("ssh", "/usr/bin/ssh");
        var connectionRuntime = new ConnectionRuntime([
            new SshConnectionRuntimeAdapter(vault, locator, new RecordingCommandRunner(), store),
        ]);
        var runtime = new ConnectionSecurityRuntime(
            connectionRuntime,
            store,
            new FixedHostKeyScanner(Candidate(1)),
            new FixedAuthenticationProbe(),
            clock);
        var profile = SshProfile();
        var review = Success(await runtime.InspectSshHostKeyAsync(profile, null, CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(6));

        var failure = Failure(await runtime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(review.Id, profile.Id, SshHostKeyTrustAction.TrustNew),
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.HostKeyReviewExpired, failure.Code);
        Assert.Null(await store.ReadAsync(profile.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Diagnostics_keep_unknown_host_key_distinct_from_authentication()
    {
        var store = new SshKnownHostStore(_knownHostsDirectory);
        using var vault = new RecordingSecretVault();
        var authentication = new FixedAuthenticationProbe();
        var runtime = Runtime(store, vault, new FixedHostKeyScanner(Candidate(1)), authentication);

        var result = Success(await runtime.DiagnoseAsync(
            SshProfile(),
            null,
            CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.Equal(ConnectionRuntimeErrorCode.UnknownHostKey, result.Failure!.Code);
        Assert.Equal(SshHostKeyDisposition.Unknown, result.HostKeyReview!.Disposition);
        Assert.Contains(result.Items, item =>
            item.Stage == ConnectionDiagnosticStage.HostKey
            && item.Status == ConnectionDiagnosticStatus.Failed);
        Assert.Equal(0, authentication.CallCount);
    }

    [Fact]
    public async Task Repeated_diagnostics_reuse_the_pinned_key_instead_of_opening_a_second_handshake()
    {
        var candidate = Candidate(1);
        var scanner = new FixedHostKeyScanner(candidate);
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var profile = SshProfile(id: "repeat-diagnostics");
        Assert.Equal(
            SshKnownHostWriteResult.Stored,
            await store.WriteAsync(profile.Id, candidate, null, CancellationToken.None));
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("ssh", "/usr/bin/ssh");
        var commandRunner = new RecordingCommandRunner
        {
            Result = new ConnectionProbeResult(
                ConnectionProbeOutcome.Exited,
                255,
                "Permission denied (publickey)."),
        };
        var connectionRuntime = new ConnectionRuntime([
            new SshConnectionRuntimeAdapter(vault, locator, commandRunner, store),
        ]);
        var runtime = new ConnectionSecurityRuntime(
            connectionRuntime,
            store,
            scanner,
            new FixedAuthenticationProbe(),
            TimeProvider.System);

        var first = Success(await runtime.DiagnoseAsync(profile, null, CancellationToken.None));
        commandRunner.Result = ConnectionProbeResult.Success;
        var retry = Success(await runtime.DiagnoseAsync(profile, null, CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.AuthenticationFailed, first.Failure?.Code);
        Assert.True(retry.Succeeded);
        Assert.Equal(0, scanner.CallCount);
        Assert.Equal(2, commandRunner.Commands.Count);
        Assert.All(
            [first, retry],
            report => Assert.Contains(report.Items, item =>
                item.Stage == ConnectionDiagnosticStage.HostKey
                && item.Status == ConnectionDiagnosticStatus.Passed));
    }

    [Fact]
    public async Task Authentication_host_key_change_triggers_a_review_scan()
    {
        var pinned = Candidate(1);
        var changed = Candidate(2);
        var scanner = new FixedHostKeyScanner(changed);
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var password = new SecretRef("password");
        var profile = SshProfile(
            id: "changed-during-authentication",
            authentication: new ConnectionAuthentication.Password(password));
        Assert.Equal(
            SshKnownHostWriteResult.Stored,
            await store.WriteAsync(profile.Id, pinned, null, CancellationToken.None));
        using var vault = new RecordingSecretVault();
        vault.Add(password, profile.Id.Value);
        var authentication = new FixedAuthenticationProbe
        {
            FailureCode = ConnectionRuntimeErrorCode.HostKeyChanged,
        };
        var runtime = Runtime(store, vault, scanner, authentication);

        var report = Success(await runtime.DiagnoseAsync(profile, null, CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.HostKeyChanged, report.Failure?.Code);
        Assert.Equal(SshHostKeyDisposition.Changed, report.HostKeyReview?.Disposition);
        Assert.Equal(changed.Identity, report.HostKeyReview?.Presented);
        Assert.Equal(pinned.Identity, report.HostKeyReview?.Trusted);
        Assert.Equal(1, scanner.CallCount);
        Assert.DoesNotContain(report.Items, item =>
            item.Stage == ConnectionDiagnosticStage.Authentication);
    }

    [Fact]
    public async Task Trusted_password_diagnostics_report_reference_availability_before_authentication()
    {
        var candidate = Candidate(1);
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var password = new SecretRef("password-ref");
        using var vault = new RecordingSecretVault();
        vault.Add(password, "secure-ssh");
        var authentication = new FixedAuthenticationProbe();
        var runtime = Runtime(store, vault, new FixedHostKeyScanner(candidate), authentication);
        var profile = SshProfile(
            id: "secure-ssh",
            authentication: new ConnectionAuthentication.Password(password));
        var review = Success(await runtime.InspectSshHostKeyAsync(profile, null, CancellationToken.None));
        _ = Success(await runtime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(review.Id, profile.Id, SshHostKeyTrustAction.TrustNew),
            CancellationToken.None));

        var report = Success(await runtime.DiagnoseAsync(profile, null, CancellationToken.None));

        Assert.True(report.Succeeded);
        Assert.Equal(ConnectionTestVerification.EndpointAuthenticated, report.Verification);
        Assert.Equal(1, authentication.CallCount);
        Assert.Equal(profile, authentication.LastProfile);
        Assert.Contains(report.Items, item =>
            item.Stage == ConnectionDiagnosticStage.Credentials
            && item.Status == ConnectionDiagnosticStatus.Passed
            && string.Equals(item.StableCode, "connection_credential_references_available"
, StringComparison.Ordinal) && string.Equals(item.Message, "Every credential reference is available for this connection scope.", StringComparison.Ordinal));
        Assert.Empty(vault.ResolveRequests);
        var metadataRequest = Assert.Single(vault.MetadataRequests);
        Assert.Equal(new SecretScope(SecretScopeKind.Connection, "secure-ssh"), metadataRequest.Scope);
        Assert.Equal(SecretUseKind.ConnectionAuthentication, metadataRequest.Purpose.Kind);
    }

    [Fact]
    public async Task Trusted_key_file_is_bound_to_the_open_ssh_launch_plan()
    {
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var profile = SshProfile();
        var candidate = Candidate(1);
        Assert.Equal(
            SshKnownHostWriteResult.Stored,
            await store.WriteAsync(profile.Id, candidate, null, CancellationToken.None));
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("ssh", "/usr/bin/ssh");
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            store);

        var plan = Success(await adapter.PlanOpenAsync(profile, null, CancellationToken.None));
        var binding = store.Binding(profile.Id);

        Assert.Contains($"UserKnownHostsFile={binding.FilePath}", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains($"HostKeyAlias={binding.Alias}", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            $"GlobalKnownHostsFile={(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")}",
            plan.Launch.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Production_authentication_probe_resolves_and_releases_scoped_password_before_failure()
    {
        var password = new SecretRef("probe-password");
        using var vault = new RecordingSecretVault();
        vault.Add(password, "probe-ssh");
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("127.0.0.1", port: 1, username: "deploy"),
            new ConnectionAuthentication.Password(password),
            hostKeyPolicy: SshHostKeyPolicy.Strict,
            id: "probe-ssh");
        _ = await store.WriteAsync(profile.Id, Candidate(1), null, CancellationToken.None);
        var probe = new SshNetAuthenticationProbe(vault, store);

        var result = await probe.AuthenticateAsync(profile, CancellationToken.None);

        var failure = Failure(result);
        Assert.Contains(
            failure.Code,
            new[] { ConnectionRuntimeErrorCode.Offline, ConnectionRuntimeErrorCode.ProcessFailed });
        var request = Assert.Single(vault.ResolveRequests);
        Assert.Equal(new SecretScope(SecretScopeKind.Connection, profile.Id.Value), request.Scope);
        Assert.Equal(
            new SecretUsePurpose(SecretUseKind.ConnectionAuthentication, profile.Id.Value),
            request.Purpose);
        Assert.True(vault.LastMaterial!.IsDisposed);
        Assert.DoesNotContain("probe-password", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-leak", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_private_key_is_a_typed_secret_failure_and_material_is_released()
    {
        var key = new SecretRef("invalid-key");
        using var vault = new RecordingSecretVault();
        vault.Add(key, "invalid-key-ssh");
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("127.0.0.1", port: 1, username: "deploy"),
            new ConnectionAuthentication.PrivateKey(key),
            hostKeyPolicy: SshHostKeyPolicy.Strict,
            id: "invalid-key-ssh");
        _ = await store.WriteAsync(profile.Id, Candidate(1), null, CancellationToken.None);
        var probe = new SshNetAuthenticationProbe(vault, store);

        var failure = Failure(await probe.AuthenticateAsync(profile, CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.SecretInvalid, failure.Code);
        Assert.True(vault.LastMaterial!.IsDisposed);
        Assert.DoesNotContain(key.Value, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedTrustedHostFileFailsInspectionWithoutLeakingParserFailure()
    {
        var store = new SshKnownHostStore(_knownHostsDirectory);
        var profile = SshProfile("malformed-review");
        var binding = store.Binding(profile.Id);
        Directory.CreateDirectory(_knownHostsDirectory);
        await File.WriteAllTextAsync(binding.FilePath, "malformed trust binding\n");
        using var vault = new RecordingSecretVault();
        var runtime = Runtime(store, vault, new FixedHostKeyScanner(Candidate(1)));

        var failure = Failure(await runtime.InspectSshHostKeyAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.ProcessFailed, failure.Code);
        Assert.DoesNotContain("malformed", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_knownHostsDirectory))
        {
            Directory.Delete(_knownHostsDirectory, recursive: true);
        }
    }

    private static ConnectionSecurityRuntime Runtime(
        SshKnownHostStore store,
        RecordingSecretVault vault,
        ISshHostKeyScanner scanner,
        ISshAuthenticationProbe? authentication = null,
        OpenSshKnownHostTrustSource? openSshKnownHosts = null)
    {
        var locator = new RecordingExecutableLocator();
        locator.Add("ssh", "/usr/bin/ssh");
        var connectionRuntime = new ConnectionRuntime([
            new SshConnectionRuntimeAdapter(vault, locator, new RecordingCommandRunner(), store),
        ]);
        return new ConnectionSecurityRuntime(
            connectionRuntime,
            store,
            scanner,
            authentication ?? new FixedAuthenticationProbe(),
            TimeProvider.System,
            openSshKnownHosts);
    }

    private static ConnectionProfile SshProfile(
        string id = "ssh-security",
        ConnectionAuthentication? authentication = null) =>
        ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            authentication ?? new ConnectionAuthentication.SshAgent(),
            hostKeyPolicy: SshHostKeyPolicy.Strict,
            id: id);

    private static SshHostKeyCandidate Candidate(byte marker) =>
        new("ssh-ed25519", Convert.ToBase64String(Enumerable.Repeat(marker, 32).ToArray()));

    private static T Success<T>(ConnectionRuntimeResult<T> result) =>
        Assert.IsType<ConnectionRuntimeResult<T>.Success>(result).Value;

    private static ConnectionRuntimeError Failure<T>(ConnectionRuntimeResult<T> result) =>
        Assert.IsType<ConnectionRuntimeResult<T>.Failure>(result).Error;

    private sealed class FixedHostKeyScanner(SshHostKeyCandidate candidate) : ISshHostKeyScanner
    {
        public SshHostKeyCandidate Candidate { get; set; } = candidate;

        public int CallCount { get; private set; }

        public ValueTask<ConnectionRuntimeResult<SshHostKeyCandidate>> ScanAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            _ = profile;
            CallCount++;
            return ValueTask.FromResult(cancellationToken.IsCancellationRequested
                ? ConnectionRuntimeResult<SshHostKeyCandidate>.Fail(
                    ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled))
                : ConnectionRuntimeResult<SshHostKeyCandidate>.Succeed(Candidate));
        }
    }

    private sealed class FixedAuthenticationProbe : ISshAuthenticationProbe
    {
        public int CallCount { get; private set; }

        public ConnectionProfile? LastProfile { get; private set; }

        public ConnectionRuntimeErrorCode? FailureCode { get; set; }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> AuthenticateAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastProfile = profile;
            if (FailureCode is { } failureCode)
            {
                return ValueTask.FromResult(
                    ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                        ConnectionRuntimeError.Create(failureCode)));
            }

            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                new ConnectionTestReport(
                    profile.Id,
                    ConnectionKind.Ssh,
                    ConnectionTestVerification.EndpointAuthenticated,
                    endpointReached: true)));
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
