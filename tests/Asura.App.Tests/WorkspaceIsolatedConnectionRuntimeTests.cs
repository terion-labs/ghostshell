using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class WorkspaceIsolatedConnectionRuntimeTests
{
    private static readonly WorkspaceIsolationProviderDescriptor TestProvider = new(
        new WorkspaceIsolationProviderId("test-isolation"),
        "Test isolation",
        WorkspaceIsolationCapability.StructuredProcessExecution);

    [Fact]
    public void TransferScopeUsesProviderAndPersistentResourceIdentity()
    {
        var hostA = RuntimeWorkspace("host-a", isolationBinding: null);
        var hostB = RuntimeWorkspace("host-b", isolationBinding: null);
        var binding = Binding(
            new WorkspaceId("shared-workspace"),
            TestProvider.Id,
            "shared-resource");
        var sameResourceOtherLease = Binding(
            new WorkspaceId("shared-workspace"),
            TestProvider.Id,
            "shared-resource");
        var differentResource = Binding(
            new WorkspaceId("different-workspace"),
            TestProvider.Id,
            "different-resource");
        var isolatedA = RuntimeWorkspace("isolated-a", binding);
        var isolatedSame = RuntimeWorkspace("isolated-same", sameResourceOtherLease);
        var isolatedDifferent = RuntimeWorkspace("isolated-different", differentResource);

        Assert.True(MainWindowViewModel.SharesExecutionScope(hostA, hostB));
        Assert.True(MainWindowViewModel.SharesExecutionScope(isolatedA, isolatedSame));
        Assert.False(MainWindowViewModel.SharesExecutionScope(hostA, isolatedA));
        Assert.False(MainWindowViewModel.SharesExecutionScope(isolatedA, isolatedDifferent));
    }

    [Fact]
    public async Task ExplicitLocalStartupDirectoryIsPreservedWithTerminalMetadata()
    {
        var profile = LocalConnection("/host/home/project");
        var multiplexer = TerminalMultiplexerSession.CreateAutomatic();
        var originalLaunch = new TerminalLaunchRequest(
            "/host/home/project",
            "/bin/zsh",
            ["-l"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TERM_PROGRAM"] = "Asura",
            },
            connectionId: profile.Id,
            initialCommand: "pwd",
            shellActivityFallback: TerminalShellActivityFallback.PromptShape,
            multiplexerSession: multiplexer);
        var originalPlan = new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Local,
            originalLaunch,
            ConnectionAuthenticationMode.None,
            SshHostKeyPolicy.NotApplicable,
            ConnectionReconnectMode.NotApplicable,
            warnings: [ConnectionPlanWarning.RemoteEnvironmentRequiresServerAcceptance]);
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec", "workspace", "/bin/sh"],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OUTER"] = "1",
                    },
                    "/host/home")));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            new FixedConnectionRuntime(originalPlan),
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanOpenAsync(
            profile,
            multiplexer,
            progress: null,
            CancellationToken.None);

        var request = Assert.IsType<WorkspaceIsolationProcessRequest>(provider.Request);
        Assert.Equal(ConnectionKind.Local, request.ConnectionKind);
        Assert.Equal("/bin/zsh", request.HostExecutable);
        Assert.Equal(["-l"], request.Arguments);
        Assert.Equal("Asura", request.Environment["TERM_PROGRAM"]);
        Assert.Equal("/host/home/project", request.HostWorkingDirectory);
        Assert.Equal(
            WorkspaceProcessMode.Interactive | WorkspaceProcessMode.AllocateTerminal,
            request.Mode);
        Assert.False(request.UsesHostCredentialBroker);

        var rewritten = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(
            result).Value;
        Assert.Equal(originalPlan.ConnectionId, rewritten.ConnectionId);
        Assert.Equal(originalPlan.Kind, rewritten.Kind);
        Assert.Equal(originalPlan.Authentication, rewritten.Authentication);
        Assert.Equal(originalPlan.HostKeyPolicy, rewritten.HostKeyPolicy);
        Assert.Equal(originalPlan.ReconnectMode, rewritten.ReconnectMode);
        Assert.Equal(originalPlan.Warnings, rewritten.Warnings);
        Assert.Equal("container", rewritten.Launch.Executable);
        Assert.Equal(["exec", "workspace", "/bin/sh"], rewritten.Launch.Arguments);
        Assert.Equal("1", rewritten.Launch.Environment["OUTER"]);
        Assert.Equal("/host/home", rewritten.Launch.WorkingDirectory);
        Assert.Equal(originalLaunch.ConnectionId, rewritten.Launch.ConnectionId);
        Assert.Equal("pwd", rewritten.Launch.InitialCommand);
        Assert.Equal(
            TerminalShellActivityFallback.PromptShape,
            rewritten.Launch.ShellActivityFallback);
        Assert.Same(multiplexer, rewritten.Launch.MultiplexerSession);
    }

    [Fact]
    public async Task ImplicitLocalStartupDirectoryUsesTheGuestDefault()
    {
        var profile = LocalConnection();
        var plan = new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Local,
            new TerminalLaunchRequest("/host/home", "/bin/zsh", ["-l"]),
            ConnectionAuthenticationMode.None,
            SshHostKeyPolicy.NotApplicable,
            ConnectionReconnectMode.NotApplicable);
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec", "workspace", "/bin/sh"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    hostWorkingDirectory: null)));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            new FixedConnectionRuntime(plan),
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanOpenAsync(
            profile,
            progress: null,
            CancellationToken.None);

        var rewritten = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(
            result).Value;
        Assert.Null(provider.Request!.HostWorkingDirectory);
        Assert.Equal(
            TerminalShellActivityFallback.PromptShape,
            rewritten.Launch.ShellActivityFallback);
    }

    [Fact]
    public async Task StructuredLocalCommandIsPlannedInsideTheWorkspaceEnvironment()
    {
        var profile = LocalConnection();
        var plan = new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Local,
            new TerminalLaunchRequest("/host/home", "/bin/zsh", ["-l"]),
            ConnectionAuthenticationMode.None,
            SshHostKeyPolicy.NotApplicable,
            ConnectionReconnectMode.NotApplicable);
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec", "workspace", "git", "status"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    hostWorkingDirectory: null)));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            new FixedConnectionRuntime(plan),
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanCommandAsync(
            profile,
            "git",
            ["status"],
            CancellationToken.None);

        var request = Assert.IsType<WorkspaceIsolationProcessRequest>(provider.Request);
        Assert.Equal("git", request.HostExecutable);
        Assert.Equal(["status"], request.Arguments);
        Assert.Equal(WorkspaceProcessMode.None, request.Mode);
        var launch = Assert.IsType<ConnectionRuntimeResult<TerminalLaunchRequest>.Success>(
            result).Value;
        Assert.Equal("container", launch.Executable);
        Assert.Equal(["exec", "workspace", "git", "status"], launch.Arguments);
    }

    [Fact]
    public async Task DuplexLocalCommandPreservesStdinWithoutAllocatingATerminal()
    {
        var profile = LocalConnection();
        var plan = new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Local,
            new TerminalLaunchRequest("/host/home", "/bin/zsh", ["-l"]),
            ConnectionAuthenticationMode.None,
            SshHostKeyPolicy.NotApplicable,
            ConnectionReconnectMode.NotApplicable);
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec", "--interactive", "workspace", "nc", "example.com", "443"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    hostWorkingDirectory: null)));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            new FixedConnectionRuntime(plan),
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanDuplexCommandAsync(
            profile,
            "nc",
            ["example.com", "443"],
            CancellationToken.None);

        var request = Assert.IsType<WorkspaceIsolationProcessRequest>(provider.Request);
        Assert.Equal(WorkspaceProcessMode.Interactive, request.Mode);
        Assert.IsType<ConnectionRuntimeResult<TerminalLaunchRequest>.Success>(result);
    }

    [Fact]
    public async Task DuplexSshCommandDoesNotAllocateARemoteTerminalForBinaryTraffic()
    {
        var profile = new ConnectionProfile(
            new ConnectionId("workspace-isolation-ssh-command"),
            ConnectionProfile.CurrentSchemaVersion,
            "SSH",
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var plan = new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Ssh,
            new TerminalLaunchRequest(
                null,
                "/usr/bin/ssh",
                ["-p", "22", "-tt", "--", "host.example", "old terminal command"]),
            ConnectionAuthenticationMode.None,
            SshHostKeyPolicy.Strict,
            ConnectionReconnectMode.Manual);
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    hostWorkingDirectory: null)));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            new FixedConnectionRuntime(plan),
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanDuplexCommandAsync(
            profile,
            "/bin/sh",
            ["-c", "printf '%s' \"$1\"", "asura-command", "O'Brien"],
            CancellationToken.None);

        var request = Assert.IsType<WorkspaceIsolationProcessRequest>(provider.Request);
        Assert.Equal("/usr/bin/ssh", request.HostExecutable);
        Assert.Equal(
            [
                "-p",
                "22",
                "--",
                "host.example",
                "'/bin/sh' '-c' 'printf '\"'\"'%s'\"'\"' \"$1\"' 'asura-command' 'O'\"'\"'Brien'",
            ],
            request.Arguments);
        Assert.Equal(WorkspaceProcessMode.Interactive, request.Mode);
        Assert.IsType<ConnectionRuntimeResult<TerminalLaunchRequest>.Success>(result);
    }

    [Fact]
    public async Task IsolationLaunchFailureDoesNotFallBackToTheHostPlan()
    {
        var profile = LocalConnection("/outside");
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            new FixedConnectionRuntime(new ConnectionOpenPlan(
                profile.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest("/outside", "/bin/sh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)),
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanOpenAsync(
            profile,
            progress: null,
            CancellationToken.None);

        var error = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Failure>(
            result).Error;
        Assert.Equal("workspace_isolation_directory_not_mounted", error.StableCode);
        Assert.Equal(ConnectionRuntimeErrorCode.InvalidProfile, error.Code);
        Assert.Equal(ConnectionRecoveryAction.EditProfile, error.RecoveryAction);
        Assert.NotNull(provider.Request);
    }

    [Fact]
    public async Task PreparedHostCredentialBrokerIsRejectedInsideTheIsolate()
    {
        var profile = LocalConnection();
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            new FixedConnectionRuntime(new ConnectionOpenPlan(
                profile.Id,
                ConnectionKind.Ssh,
                new TerminalLaunchRequest(null, "/usr/bin/ssh", ["host.example"]),
                ConnectionAuthenticationMode.Password,
                SshHostKeyPolicy.Strict,
                ConnectionReconnectMode.Manual,
                [new ConnectionSecretRequirement(
                    ConnectionSecretRole.Password,
                    new SecretRef("workspace-isolation-password"))],
                isSecretBrokerPrepared: true)),
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanOpenAsync(
            profile,
            progress: null,
            CancellationToken.None);

        Assert.True(provider.Request!.UsesHostCredentialBroker);
        var error = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Failure>(
            result).Error;
        Assert.Equal(
            "workspace_isolation_credential_broker_unavailable",
            error.StableCode);
        Assert.Equal(ConnectionRuntimeErrorCode.AuthenticationRequired, error.Code);
    }

    [Fact]
    public async Task BrokeredAuthenticationIsRejectedBeforeHostPlanning()
    {
        var profile = new ConnectionProfile(
            new ConnectionId("workspace-isolation-password"),
            ConnectionProfile.CurrentSchemaVersion,
            "Password SSH",
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.Password(new SecretRef("password-secret")),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var inner = new FixedConnectionRuntime(new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Ssh,
            new TerminalLaunchRequest(null, "/usr/bin/ssh", ["host.example"]),
            ConnectionAuthenticationMode.Password,
            SshHostKeyPolicy.Strict,
            ConnectionReconnectMode.Manual));
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    hostWorkingDirectory: null)));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            inner,
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanOpenAsync(
            profile,
            progress: null,
            CancellationToken.None);

        var error = Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Failure>(
            result).Error;
        Assert.Equal("workspace_isolation_credential_broker_unavailable", error.StableCode);
        Assert.Equal(0, inner.PlanCount);
        Assert.Null(provider.Request);
    }

    [Fact]
    public async Task SshAgentAuthenticationUsesTheIsolationProvidersForwardedAgent()
    {
        var profile = new ConnectionProfile(
            new ConnectionId("workspace-isolation-agent"),
            ConnectionProfile.CurrentSchemaVersion,
            "Agent SSH",
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var inner = new FixedConnectionRuntime(new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Ssh,
            new TerminalLaunchRequest(
                null,
                "/usr/bin/ssh",
                ["-tt", "--", "host.example"]),
            ConnectionAuthenticationMode.SshAgent,
            SshHostKeyPolicy.Strict,
            ConnectionReconnectMode.Manual));
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    hostWorkingDirectory: null)));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            inner,
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanOpenAsync(
            profile,
            progress: null,
            CancellationToken.None);

        Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(result);
        Assert.Equal(1, inner.PlanCount);
        Assert.False(provider.Request!.UsesHostCredentialBroker);
        Assert.Contains("-tt", provider.Request.Arguments, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(SshHostKeyPolicy.Strict)]
    [InlineData(SshHostKeyPolicy.AcceptNew)]
    public async Task VerifiedSshTrustIsDelegatedToTheIsolationProvider(
        SshHostKeyPolicy hostKeyPolicy)
    {
        var profile = new ConnectionProfile(
            new ConnectionId($"workspace-isolation-{hostKeyPolicy}"),
            ConnectionProfile.CurrentSchemaVersion,
            "Verified SSH",
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            hostKeyPolicy);
        var inner = new FixedConnectionRuntime(new ConnectionOpenPlan(
            profile.Id,
            ConnectionKind.Ssh,
            new TerminalLaunchRequest(null, "/usr/bin/ssh", ["host.example"]),
            ConnectionAuthenticationMode.None,
            hostKeyPolicy,
            ConnectionReconnectMode.Manual));
        var provider = new RecordingIsolationProvider(
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "container",
                    ["exec"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    hostWorkingDirectory: null)));
        var runtime = new WorkspaceIsolatedConnectionRuntime(
            inner,
            provider,
            Binding(profile.Id));

        var result = await runtime.PlanOpenAsync(
            profile,
            progress: null,
            CancellationToken.None);

        Assert.IsType<ConnectionRuntimeResult<ConnectionOpenPlan>.Success>(result);
        Assert.Equal(1, inner.PlanCount);
        Assert.NotNull(provider.Request);
    }

    private static ConnectionProfile LocalConnection(string? startupDirectory = null) => new(
        new ConnectionId("workspace-isolation-local"),
        ConnectionProfile.CurrentSchemaVersion,
        "Local",
        new ConnectionEndpoint.Local("/bin/sh"),
        new ConnectionAuthentication.None(),
        new ConnectionStartup(startupDirectory),
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private static WorkspaceIsolationBinding Binding(ConnectionId connectionId) =>
        Binding(
            new WorkspaceId($"workspace-for-{connectionId.Value}"),
            TestProvider.Id,
            "asura-test-workspace");

    private static WorkspaceIsolationBinding Binding(
        WorkspaceId workspaceId,
        WorkspaceIsolationProviderId provider,
        string resourceName) => new(
        workspaceId,
        provider,
        WorkspaceIsolationCapability.StructuredProcessExecution,
        resourceName,
        [new WorkspaceIsolationMount("/host/home", "/workspace", isReadOnly: false)],
        Guid.NewGuid());

    private static RuntimeWorkspaceViewModel RuntimeWorkspace(
        string id,
        WorkspaceIsolationBinding? isolationBinding) =>
        new(
            new WorkspaceInstanceId(id),
            id,
            string.Empty,
            [],
            isolationBinding: isolationBinding);

    private sealed class FixedConnectionRuntime(ConnectionOpenPlan plan) : IConnectionRuntime
    {
        public int PlanCount { get; private set; }

        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanCount++;
            return ValueTask.FromResult(
                ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(plan));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingIsolationProvider(
        WorkspaceIsolationResult<WorkspaceProcessLaunch> result)
        : IWorkspaceIsolationProvider
    {
        public WorkspaceIsolationProcessRequest? Request { get; private set; }

        public WorkspaceIsolationProviderDescriptor Descriptor => TestProvider;

        public WorkspaceIsolationCapability Capabilities =>
            WorkspaceIsolationCapability.StructuredProcessExecution;

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
            WorkspaceIsolationPrepareRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
            WorkspaceIsolationBinding binding,
            WorkspaceIsolationProcessRequest request)
        {
            Request = request;
            return result;
        }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
            WorkspaceIsolationBinding binding,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
