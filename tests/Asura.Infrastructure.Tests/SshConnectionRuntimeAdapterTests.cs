using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SshConnectionRuntimeAdapterTests
{
    [Fact]
    public async Task SystemConfigurationPublishesSuccessfulIdentityForSdkChannels()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.None(),
            hostKeyPolicy: SshHostKeyPolicy.Strict);

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionAuthenticationMode.None, plan.Authentication);
        Assert.Contains("AddKeysToAgent=yes", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(
            plan.Launch.Arguments,
            argument => argument.StartsWith(
                "PreferredAuthentications=",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_preserves_host_key_agent_keepalive_and_argument_boundaries()
    {
        using var vault = new RecordingSecretVault();
        var locator = LocatorWithSsh();
        var knownHosts = KnownHosts();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            knownHosts);
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh(
                "-oProxyCommand=touch /tmp/not-run",
                2202,
                "operator name"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("/remote/work"),
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(17), 4),
            SshHostKeyPolicy.Strict);

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal("/usr/bin/ssh", plan.Launch.Executable);
        Assert.Null(plan.Launch.WorkingDirectory);
        Assert.Equal(
            "SSH: operator name@-oProxyCommand=touch /tmp/not-run:2202",
            plan.Launch.ConnectionMetadata?.ConnectionBoundary);
        Assert.Equal(
            "/remote/work",
            plan.Launch.ConnectionMetadata?.InitialWorkingDirectory);
        Assert.Equal(ConnectionAuthenticationMode.SshAgent, plan.Authentication);
        Assert.Equal(SshHostKeyPolicy.Strict, plan.HostKeyPolicy);
        Assert.Equal(ConnectionReconnectMode.BoundedBackoff, plan.ReconnectMode);
        Assert.Contains("StrictHostKeyChecking=yes", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("ConnectTimeout=12", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("ServerAliveInterval=17", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("ServerAliveCountMax=4", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("PreferredAuthentications=publickey", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("AddKeysToAgent=yes", plan.Launch.Arguments, StringComparer.Ordinal);
        var binding = knownHosts.Binding(profile.Id);
        Assert.False(File.Exists(binding.FilePath));
        Assert.Contains($"UserKnownHostsFile={binding.FilePath}", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            $"GlobalKnownHostsFile={(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")}",
            plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains($"HostKeyAlias={binding.Alias}", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(
            ConnectionPlanWarning.RemoteEnvironmentRequiresServerAcceptance,
            plan.Warnings);
        Assert.Contains(
            ConnectionPlanWarning.SshStartupDirectoryRequiresPosixShell,
            plan.Warnings);

        var separator = plan.Launch.Arguments.IndexOf("--");
        Assert.True(separator >= 0);
        Assert.Equal("-oProxyCommand=touch /tmp/not-run", plan.Launch.Arguments[separator + 1]);
        Assert.Equal(
            "exec /bin/sh -c 'cd \"$1\" && exec \"${SHELL:-/bin/sh}\" -l' asura-startup '/remote/work'",
            plan.Launch.Arguments[separator + 2]);
        Assert.Contains("operator name", plan.Launch.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Known_host_path_is_quoted_for_open_ssh_config_parsing()
    {
        using var vault = new RecordingSecretVault();
        var knownHosts = new SshKnownHostStore(Path.Combine(
            Path.GetTempPath(),
            "Asura Data",
            $"known-hosts-{Guid.NewGuid():N}"));
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            knownHosts);
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.SshAgent(),
            hostKeyPolicy: SshHostKeyPolicy.Strict);

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        var binding = knownHosts.Binding(profile.Id);
        Assert.Contains(
            $"UserKnownHostsFile=\"{binding.FilePath}\"",
            plan.Launch.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RemoteStartupDirectoryIsPosixQuotedAsDataNotShellSyntax()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("/srv/it's; $(touch /tmp/not-run)"),
            hostKeyPolicy: SshHostKeyPolicy.Strict);

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        var destination = plan.Launch.Arguments.IndexOf("host.example");
        Assert.True(destination >= 0);
        Assert.Equal(
            "exec /bin/sh -c 'cd \"$1\" && exec \"${SHELL:-/bin/sh}\" -l' asura-startup '/srv/it'\"'\"'s; $(touch /tmp/not-run)'",
            plan.Launch.Arguments[destination + 1]);
        Assert.Null(plan.Launch.WorkingDirectory);
        Assert.Contains(
            ConnectionPlanWarning.SshStartupDirectoryRequiresPosixShell,
            plan.Warnings);
    }

    [Fact]
    public async Task RemoteStartupDirectoryPreservesNewlinesInsideThePositionalArgument()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("/srv/line-one\nline-two; exit 99"));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(
            "exec /bin/sh -c 'cd \"$1\" && exec \"${SHELL:-/bin/sh}\" -l' asura-startup '/srv/line-one\nline-two; exit 99'",
            plan.Launch.Arguments[^1]);
        Assert.Equal(
            @"/srv/line-one\nline-two; exit 99",
            plan.Launch.ConnectionMetadata?.InitialWorkingDirectory);
    }

    [Fact]
    public async Task OptionLookingRemoteStartupDirectoryIsForcedToRemainAPath()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("-P"));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(
            "exec /bin/sh -c 'cd \"$1\" && exec \"${SHELL:-/bin/sh}\" -l' asura-startup './-P'",
            plan.Launch.Arguments[^1]);
    }

    [Fact]
    public async Task RemoteEnvironmentWarningAccuratelyDescribesAcceptEnvDependency()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup(environment:
            [
                new ConnectionEnvironmentVariable(
                    "DEPLOY_ENV",
                    new ConnectionEnvironmentValue.PlainText("production")),
            ]));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Contains(
            ConnectionPlanWarning.RemoteEnvironmentRequiresServerAcceptance,
            plan.Warnings);
        Assert.Contains("SendEnv=DEPLOY_ENV", plan.Launch.Arguments, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(SshHostKeyPolicy.Strict, "StrictHostKeyChecking=yes", false)]
    [InlineData(SshHostKeyPolicy.AcceptNew, "StrictHostKeyChecking=accept-new", false)]
    [InlineData(SshHostKeyPolicy.InsecureIgnore, "StrictHostKeyChecking=no", true)]
    public async Task Host_key_policy_is_not_flattened(
        SshHostKeyPolicy policy,
        string expectedOption,
        bool warningExpected)
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example"),
            new ConnectionAuthentication.SshAgent(),
            hostKeyPolicy: policy);

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(policy, plan.HostKeyPolicy);
        Assert.Contains(expectedOption, plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Equal(
            warningExpected,
            plan.Warnings.Contains(ConnectionPlanWarning.HostKeyVerificationDisabled));
    }

    [Fact]
    public async Task Password_is_resolved_by_scoped_reference_but_never_added_to_process_data()
    {
        var password = new SecretRef("ssh-password-ref");
        using var vault = new RecordingSecretVault();
        vault.Add(password, "password-ssh");
        var runner = new RecordingCommandRunner();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            runner,
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.Password(password),
            id: "password-ssh");

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));
        var report = ConnectionRuntimeTestSupport.Success(await adapter.TestAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionAuthenticationMode.Password, plan.Authentication);
        var requirement = Assert.Single(plan.SecretRequirements);
        Assert.Equal(ConnectionSecretRole.Password, requirement.Role);
        Assert.Equal(password, requirement.Reference);
        Assert.DoesNotContain(
            plan.Launch.Arguments,
            argument => argument.Contains("do-not-leak", StringComparison.Ordinal));
        Assert.DoesNotContain(
            plan.Launch.Environment.Values,
            value => value.Contains("do-not-leak", StringComparison.Ordinal));
        Assert.Equal(ConnectionTestVerification.ConfigurationValidated, report.Verification);
        Assert.False(report.EndpointReached);
        Assert.Empty(runner.Commands);
        Assert.Empty(vault.ResolveRequests);
        Assert.All(vault.MetadataRequests, request =>
        {
            Assert.Equal(
                new SecretScope(SecretScopeKind.Connection, "password-ssh"),
                request.Scope);
            Assert.Equal(
                new SecretUsePurpose(
                    SecretUseKind.ConnectionAuthentication,
                    "password-ssh"),
                request.Purpose);
        });
        Assert.Null(vault.LastMaterial);
    }

    [Fact]
    public async Task Private_key_and_passphrase_remain_distinct_opaque_requirements()
    {
        var privateKey = new SecretRef("private-key-ref");
        var passphrase = new SecretRef("passphrase-ref");
        using var vault = new RecordingSecretVault();
        vault.Add(privateKey, "key-ssh");
        vault.Add(passphrase, "key-ssh");
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example"),
            new ConnectionAuthentication.PrivateKey(privateKey, passphrase),
            id: "key-ssh");

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionAuthenticationMode.PrivateKeyWithPassphrase, plan.Authentication);
        Assert.Contains("PreferredAuthentications=publickey", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("PubkeyAuthentication=yes", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("PasswordAuthentication=no", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("KbdInteractiveAuthentication=no", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("IdentitiesOnly=yes", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Collection(
            plan.SecretRequirements,
            requirement =>
            {
                Assert.Equal(ConnectionSecretRole.PrivateKey, requirement.Role);
                Assert.Equal(privateKey, requirement.Reference);
            },
            requirement =>
            {
                Assert.Equal(ConnectionSecretRole.PrivateKeyPassphrase, requirement.Role);
                Assert.Equal(passphrase, requirement.Reference);
            });
    }

    [Fact]
    public async Task Missing_password_is_a_typed_authentication_setup_failure()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example"),
            new ConnectionAuthentication.Password(new SecretRef("sensitive-ref-872")));

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.SecretNotFound, error.Code);
        Assert.Equal(ConnectionRecoveryAction.ProvideAuthentication, error.RecoveryAction);
        Assert.DoesNotContain("sensitive-ref-872", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_test_runs_a_bounded_noninteractive_probe()
    {
        using var vault = new RecordingSecretVault();
        var runner = new RecordingCommandRunner();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            runner,
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.SshAgent());

        var report = ConnectionRuntimeTestSupport.Success(await adapter.TestAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionTestVerification.EndpointAuthenticated, report.Verification);
        Assert.True(report.EndpointReached);
        var command = Assert.Single(runner.Commands);
        Assert.Equal("/usr/bin/ssh", command.Executable);
        Assert.Contains("BatchMode=yes", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("ConnectTimeout=10", command.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("-tt", command.Arguments, StringComparer.Ordinal);
        Assert.Equal("true", command.Arguments[^1]);
        Assert.Equal(TimeSpan.FromSeconds(12), command.Timeout);
    }

    [Fact]
    public async Task NewMultiplexerSessionPrefersConfiguredTmuxAndFallsBackToScreen()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("/srv/work"));
        var session = new TerminalMultiplexerSession(
            TerminalMultiplexingMode.Automatic,
            "asura-1234abcd");

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            session,
            null,
            CancellationToken.None));

        Assert.Equal(session, plan.Launch.MultiplexerSession);
        var command = plan.Launch.Arguments[^1];
        Assert.Contains("tmux -L asura -u -2 start-server", command, StringComparison.Ordinal);
        Assert.Contains("set-option -s default-terminal tmux-256color", command, StringComparison.Ordinal);
        Assert.Contains("set-option -s terminal-features \"xterm*:RGB\"", command, StringComparison.Ordinal);
        Assert.Contains("new-session -d -s \"$1\" -c \"$2\"", command, StringComparison.Ordinal);
        Assert.Contains("set-option -t \"$1\" status off", command, StringComparison.Ordinal);
        Assert.Contains("set-option -t \"$1\" mouse on", command, StringComparison.Ordinal);
        Assert.Contains("exec screen -A -U -D -RR -S \"$1\"", command, StringComparison.Ordinal);
        Assert.True(
            command.IndexOf("command -v tmux", StringComparison.Ordinal)
            < command.IndexOf("command -v screen", StringComparison.Ordinal));
        Assert.EndsWith(
            "asura-multiplexer 'asura-1234abcd' '/srv/work'",
            command,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EstablishedMultiplexerSessionResumesTmuxOrLegacyScreenWithoutCreating()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            LocatorWithSsh(),
            new RecordingCommandRunner(),
            KnownHosts());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.SshAgent());
        var session = new TerminalMultiplexerSession(
            TerminalMultiplexingMode.Automatic,
            "asura-1234abcd",
            isEstablished: true);

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            session,
            null,
            CancellationToken.None));

        var command = plan.Launch.Arguments[^1];
        Assert.Contains("tmux -L asura has-session", command, StringComparison.Ordinal);
        Assert.Contains(
            "exec tmux -L asura -u -2 attach-session -d",
            command,
            StringComparison.Ordinal);
        Assert.Contains(
            "set-option -t \"$1\" mouse on",
            command,
            StringComparison.Ordinal);
        Assert.Contains("exec screen -A -U -D -r \"$1\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("new-session", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-RR", command, StringComparison.Ordinal);
        Assert.EndsWith(
            "asura-multiplexer 'asura-1234abcd'",
            command,
            StringComparison.Ordinal);
    }

    private static RecordingExecutableLocator LocatorWithSsh()
    {
        var locator = new RecordingExecutableLocator();
        locator.Add("ssh", "/usr/bin/ssh");
        return locator;
    }

    private static SshKnownHostStore KnownHosts() =>
        new(Path.Combine(
            Path.GetTempPath(),
            $"asura-known-hosts-test-{Guid.NewGuid():N}"));
}

internal static class ReadOnlyListTestExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(values[index], value))
            {
                return index;
            }
        }

        return -1;
    }
}
