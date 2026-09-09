using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class ConnectionCommandExecutorTests
{
    [Fact]
    public void Ssh_control_connections_belong_to_the_workspace_route()
    {
        var profile = SshProfile();
        var first = ConnectionCommandExecutor.SshControlPath(profile, "socks5://127.0.0.1:1234");
        var second = ConnectionCommandExecutor.SshControlPath(profile, "socks5://127.0.0.1:5678");
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(first);
            Assert.Null(second);
            return;
        }

        Assert.NotEqual(first, second, StringComparer.Ordinal);
        Assert.Equal(first, ConnectionCommandExecutor.SshControlPath(profile, "socks5://127.0.0.1:1234"));
    }

    [Fact]
    public async Task TextCommandsReportTruncationWithoutAllocatingTheEntireLimitUpFront()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var profile = ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local());
        var executor = new ConnectionCommandExecutor(
            new FixedRuntime(profile),
            new PathConnectionExecutableLocator());

        var result = await executor.ExecuteAsync(
            new ConnectionCommand(
                profile,
                "sh",
                ["-c", "printf abcdef"],
                TimeSpan.FromSeconds(5),
                4),
            CancellationToken.None);

        Assert.Equal("abcd", result.StandardOutput);
        Assert.True(result.OutputTruncated);
    }

    [Fact]
    public void TextCommandsAllowBoundedStructuredOutputUpToSixteenMegabytes()
    {
        var profile = ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local());

        var command = new ConnectionCommand(
            profile,
            "docker",
            ["volume", "ls"],
            TimeSpan.FromSeconds(5),
            16 * 1024 * 1024);

        Assert.Equal(16 * 1024 * 1024, command.MaximumOutputCharacters);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionCommand(
            profile,
            "docker",
            ["volume", "ls"],
            TimeSpan.FromSeconds(5),
            (16 * 1024 * 1024) + 1));
    }

    [Fact]
    public async Task BinaryCommandsPreserveNullBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var profile = ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local());
        var executor = new ConnectionCommandExecutor(
            new FixedRuntime(profile),
            new PathConnectionExecutableLocator());

        var result = await executor.ExecuteBinaryAsync(
            new ConnectionBinaryCommand(
                profile,
                "sh",
                ["-c", "printf '\\000\\001ABC'"],
                TimeSpan.FromSeconds(5),
                1024),
            CancellationToken.None);

        Assert.Equal(ConnectionCommandOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal([0, 1, 65, 66, 67], result.StandardOutput.ToArray());
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public void SshCommandsReuseAnAuthenticatedControlConnection()
    {
        var profile = SshProfile();
        var request = new ConnectionCommand(
            profile,
            "ps",
            ["-A"],
            TimeSpan.FromSeconds(5),
            1024);
        var arguments = ConnectionCommandExecutor.SshArguments(
            ["-p", "22", "-tt", "--", "host.example"],
            request,
            "/tmp/asura-test-%C");

        Assert.DoesNotContain("-tt", arguments, StringComparer.Ordinal);
        AssertOption(arguments, "ControlMaster=auto");
        AssertOption(arguments, "ControlPersist=15");
        AssertOption(arguments, "ControlPath=/tmp/asura-test-%C");
        Assert.Equal("--", arguments[^3]);
        Assert.Equal("host.example", arguments[^2]);
        Assert.Equal("'ps' '-A'", arguments[^1]);
    }

    [Fact]
    public async Task Local_commands_use_supplemental_executable_directories()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory(
            "asura-command-executor-");
        try
        {
            var docker = Path.Combine(directory.FullName, "docker");
            await File.WriteAllTextAsync(docker, "#!/bin/sh\nprintf supplemental");
            File.SetUnixFileMode(
                docker,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var profile = ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local());
            var locator = new PathConnectionExecutableLocator(
                "/usr/bin:/bin:/usr/sbin:/sbin",
                [directory.FullName]);
            var executor = new ConnectionCommandExecutor(new FixedRuntime(profile), locator);

            var result = await executor.ExecuteAsync(
                new ConnectionCommand(
                    profile,
                    "docker",
                    ["version"],
                    TimeSpan.FromSeconds(5),
                    1024),
                CancellationToken.None);

            Assert.Equal(ConnectionCommandOutcome.Exited, result.Outcome);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("supplemental", result.StandardOutput);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ControlConnectionIdentityChangesWithAuthenticationConfiguration()
    {
        var systemConfiguration = SshProfile();
        var sshAgent = new ConnectionProfile(
            systemConfiguration.Id,
            systemConfiguration.SchemaVersion,
            systemConfiguration.Name,
            systemConfiguration.Endpoint,
            new ConnectionAuthentication.SshAgent(),
            systemConfiguration.Startup,
            systemConfiguration.KeepAlive,
            systemConfiguration.HostKeyPolicy);

        var first = ConnectionCommandExecutor.SshControlPath(systemConfiguration);
        var repeated = ConnectionCommandExecutor.SshControlPath(systemConfiguration);
        var changed = ConnectionCommandExecutor.SshControlPath(sshAgent);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert.NotNull(first);
            Assert.Equal(first, repeated);
            Assert.NotEqual(first, changed, StringComparer.Ordinal);
            Assert.EndsWith("-%C", first, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(first);
            Assert.Null(repeated);
            Assert.Null(changed);
        }
    }

    private static ConnectionProfile SshProfile() =>
        ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.None(),
            hostKeyPolicy: SshHostKeyPolicy.Strict);

    private static void AssertOption(
        IReadOnlyList<string> arguments,
        string expectedValue)
    {
        var valueIndex = -1;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index - 1], "-o", StringComparison.Ordinal) && string.Equals(arguments[index], expectedValue, StringComparison.Ordinal))
            {
                valueIndex = index;
                break;
            }
        }

        Assert.True(valueIndex >= 0, $"Missing SSH option: {expectedValue}");
        Assert.True(valueIndex < arguments.IndexOf("--"));
    }

    private sealed class FixedRuntime(ConnectionProfile profile) : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile requestedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(profile.Id, requestedProfile.Id);
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(null),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile requestedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
