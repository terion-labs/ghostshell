using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class ConnectionEditorViewModelTests
{
    [Fact]
    public void SystemConfigurationIsTheExplicitUnmanagedSshAuthenticationChoice()
    {
        var editor = new ConnectionEditorViewModel(new StubConnectionRuntime())
        {
            Name = "Configured SSH",
            Kind = ConnectionKind.Ssh,
            Host = "host.example",
            Username = "deploy",
            Authentication = ConnectionAuthenticationChoice.SystemConfiguration,
            HostKeyPolicy = SshHostKeyPolicy.Strict,
        };

        var request = editor.CreateSaveRequest();

        Assert.IsType<ConnectionAuthentication.None>(request.Profile.Authentication);
        Assert.Contains(
            ConnectionAuthenticationChoice.SystemConfiguration,
            editor.AuthenticationChoices);
    }

    [Fact]
    public void SaveRequestBuildsStructuredSshProfile()
    {
        var editor = new ConnectionEditorViewModel(new StubConnectionRuntime())
        {
            Name = "Production",
            Kind = ConnectionKind.Ssh,
            Host = "server.example.com",
            Port = 2202,
            Username = "deploy",
            Authentication = ConnectionAuthenticationChoice.SshAgent,
            HostKeyPolicy = SshHostKeyPolicy.AcceptNew,
            StartupDirectory = "/srv/app",
            StartupCommand = "tail -f logs/app.log",
            KeepAliveEnabled = true,
            KeepAliveSeconds = 20,
            KeepAliveFailures = 4,
        };

        var request = editor.CreateSaveRequest();

        Assert.Null(request.ExpectedRevision);
        Assert.Equal("Production", request.Profile.Name);
        var endpoint = Assert.IsType<ConnectionEndpoint.Ssh>(request.Profile.Endpoint);
        Assert.Equal("server.example.com", endpoint.Host);
        Assert.Equal(2202, endpoint.Port);
        Assert.Equal("deploy", endpoint.Username);
        Assert.IsType<ConnectionAuthentication.SshAgent>(request.Profile.Authentication);
        Assert.Equal(SshHostKeyPolicy.AcceptNew, request.Profile.HostKeyPolicy);
        Assert.Equal("/srv/app", request.Profile.Startup.Directory);
        Assert.Equal("tail -f logs/app.log", request.Profile.Startup.Command);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Profile.KeepAlive.Interval);
        Assert.Equal(4, request.Profile.KeepAlive.MaximumFailures);
    }

    [Fact]
    public async Task CredentialTestReportsConfigurationValidationHonestly()
    {
        var runtime = new StubConnectionRuntime
        {
            TestResult = ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                new ConnectionTestReport(
                    new ConnectionId("unused"),
                    ConnectionKind.Ssh,
                    ConnectionTestVerification.ConfigurationValidated,
                    false)),
        };
        var editor = new ConnectionEditorViewModel(runtime)
        {
            Name = "Password SSH",
            Kind = ConnectionKind.Ssh,
            Host = "host.example",
            Authentication = ConnectionAuthenticationChoice.Password,
            SecretReference = "secret-password",
            HostKeyPolicy = SshHostKeyPolicy.Strict,
        };

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Configuration validated", editor.TestStatus);
        Assert.Contains("terminal starts", editor.TestDetail, StringComparison.OrdinalIgnoreCase);
        Assert.False(editor.IsTesting);
    }

    [Fact]
    public void MissingRequiredEndpointValueFailsBeforePersistence()
    {
        var editor = new ConnectionEditorViewModel(new StubConnectionRuntime())
        {
            Name = "Broken Docker",
            Kind = ConnectionKind.Docker,
        };

        var error = Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        Assert.Equal("Docker container is required.", error.Message);
    }

    [Fact]
    public async Task Diagnostics_surface_unknown_host_key_as_a_reviewable_identity()
    {
        var security = new StubConnectionSecurityRuntime();
        var editor = SshEditor(security);

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Unknown host key", editor.TestStatus);
        Assert.Equal(SshHostKeyDisposition.Unknown, editor.HostKeyReview!.Disposition);
        Assert.True(editor.CanTrustHostKey);
        Assert.Contains(editor.Diagnostics, item => string.Equals(item.Stage, "HostKey", StringComparison.Ordinal) && string.Equals(item.Status, "Failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changed_host_key_requires_the_replace_action()
    {
        var security = new StubConnectionSecurityRuntime { Changed = true };
        var editor = SshEditor(security);
        await editor.TestAsync(CancellationToken.None);

        await editor.TrustHostKeyAsync(CancellationToken.None);

        Assert.Equal(SshHostKeyTrustAction.ReplaceChanged, security.LastTrustRequest!.Action);
        Assert.Equal("Host key trusted", editor.TestStatus);
        Assert.False(editor.CanTrustHostKey);
    }

    [Fact]
    public async Task Trusted_host_key_with_rejected_credentials_reports_authentication_failure()
    {
        var security = new StubConnectionSecurityRuntime
        {
            FailureCode = ConnectionRuntimeErrorCode.AuthenticationFailed,
        };
        var editor = SshEditor(security);

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Authentication failed", editor.TestStatus);
        Assert.Equal(SshHostKeyDisposition.Trusted, editor.HostKeyReview!.Disposition);
        Assert.Contains(editor.Diagnostics, item => string.Equals(item.Stage, "HostKey", StringComparison.Ordinal) && string.Equals(item.Status, "Passed", StringComparison.Ordinal));
        Assert.Contains(editor.Diagnostics, item => string.Equals(item.Stage, "Authentication", StringComparison.Ordinal) && string.Equals(item.Status, "Failed", StringComparison.Ordinal));
    }

    private static ConnectionEditorViewModel SshEditor(IConnectionSecurityRuntime security) => new(
        new StubConnectionRuntime(),
        securityRuntime: security)
    {
        Name = "Secure SSH",
        Kind = ConnectionKind.Ssh,
        Host = "host.example",
        Username = "deploy",
        Authentication = ConnectionAuthenticationChoice.SshAgent,
        HostKeyPolicy = SshHostKeyPolicy.Strict,
    };

    private sealed class StubConnectionRuntime : IConnectionRuntime
    {
        public ConnectionRuntimeResult<ConnectionTestReport>? TestResult { get; init; }

        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(TestResult
                ?? ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                    new ConnectionTestReport(
                        profile.Id,
                        profile.ConnectionKind,
                        ConnectionTestVerification.RuntimeAvailable,
                        false)));
    }

    private sealed class StubConnectionSecurityRuntime : IConnectionSecurityRuntime
    {
        public bool Changed { get; init; }

        public ConnectionRuntimeErrorCode? FailureCode { get; init; }

        public SshHostKeyTrustRequest? LastTrustRequest { get; private set; }

        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> PrepareSshHostKeyAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
            SshHostKeyTrustRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTrustRequest = request;
            var presented = Identity('A');
            return ValueTask.FromResult(ConnectionRuntimeResult<SshHostKeyReview>.Succeed(
                new SshHostKeyReview(
                    SshHostKeyReviewId.New(),
                    request.ConnectionId,
                    "host.example:22",
                    SshHostKeyDisposition.Trusted,
                    presented,
                    presented,
                    DateTimeOffset.UtcNow.AddMinutes(5))));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionDiagnosticsReport>> DiagnoseAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailureCode is { } failureCode)
            {
                var trusted = Identity('A');
                var trustedReview = new SshHostKeyReview(
                    SshHostKeyReviewId.New(),
                    profile.Id,
                    "host.example:22",
                    SshHostKeyDisposition.Trusted,
                    trusted,
                    trusted,
                    DateTimeOffset.UtcNow.AddMinutes(5));
                var authenticationError = ConnectionRuntimeError.Create(failureCode);
                var authenticationReport = new ConnectionDiagnosticsReport(
                    profile.Id,
                    profile.ConnectionKind,
                    DateTimeOffset.UtcNow,
                    [
                        new ConnectionDiagnosticItem(
                            ConnectionDiagnosticStage.HostKey,
                            ConnectionDiagnosticStatus.Passed,
                            "connection_host_key_trusted",
                            "The presented SSH host key matches the trusted identity."),
                        new ConnectionDiagnosticItem(
                            ConnectionDiagnosticStage.Authentication,
                            ConnectionDiagnosticStatus.Failed,
                            authenticationError.StableCode,
                            authenticationError.Message),
                    ],
                    failure: authenticationError,
                    hostKeyReview: trustedReview);
                return ValueTask.FromResult(
                    ConnectionRuntimeResult<ConnectionDiagnosticsReport>.Succeed(authenticationReport));
            }

            var disposition = Changed
                ? SshHostKeyDisposition.Changed
                : SshHostKeyDisposition.Unknown;
            var review = new SshHostKeyReview(
                SshHostKeyReviewId.New(),
                profile.Id,
                "host.example:22",
                disposition,
                Identity('A'),
                Changed ? Identity('B') : null,
                DateTimeOffset.UtcNow.AddMinutes(5));
            var error = ConnectionRuntimeError.Create(Changed
                ? ConnectionRuntimeErrorCode.HostKeyChanged
                : ConnectionRuntimeErrorCode.UnknownHostKey);
            var report = new ConnectionDiagnosticsReport(
                profile.Id,
                profile.ConnectionKind,
                DateTimeOffset.UtcNow,
                [
                    new ConnectionDiagnosticItem(
                        ConnectionDiagnosticStage.HostKey,
                        ConnectionDiagnosticStatus.Failed,
                        error.StableCode,
                        error.Message),
                ],
                failure: error,
                hostKeyReview: review);
            return ValueTask.FromResult(
                ConnectionRuntimeResult<ConnectionDiagnosticsReport>.Succeed(report));
        }

        private static SshHostKeyIdentity Identity(char marker) =>
            new("ssh-ed25519", $"SHA256:{new string(marker, 43)}");
    }
}
