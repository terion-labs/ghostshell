using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class WslConnectionRuntimeAdapterTests
{
    [Fact]
    public async Task Non_windows_host_returns_unsupported_without_probing_path()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("wsl.exe", "C:\\Windows\\System32\\wsl.exe");
        var adapter = new WslConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Linux,
                "/bin/sh",
                "/home/test"));

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Wsl("Ubuntu")),
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.UnsupportedPlatform, error.Code);
        Assert.Empty(locator.Requests);
    }

    [Fact]
    public async Task Plan_preserves_distribution_user_directory_and_wslenv_boundaries()
    {
        var secret = new SecretRef("wsl-secret-env");
        using var vault = new RecordingSecretVault();
        vault.Add(secret, "test-connection");
        var locator = LocatorWithWsl();
        var adapter = new WslConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            WindowsOptions());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Wsl("-hostile distro", "user name"),
            startup: new ConnectionStartup(
                "/home/user/work tree",
                [
                    new ConnectionEnvironmentVariable(
                        "COLOR",
                        new ConnectionEnvironmentValue.PlainText("amber")),
                    new ConnectionEnvironmentVariable(
                        "MODE",
                        new ConnectionEnvironmentValue.PlainText("safe value")),
                    new ConnectionEnvironmentVariable(
                        "SECRET_MODE",
                        new ConnectionEnvironmentValue.Secret(secret)),
                ]));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal("C:\\Windows\\System32\\wsl.exe", plan.Launch.Executable);
        Assert.Equal(
            "WSL: user name@-hostile distro",
            plan.Launch.ConnectionMetadata?.ConnectionBoundary);
        Assert.Equal(
            "/home/user/work tree",
            plan.Launch.ConnectionMetadata?.InitialWorkingDirectory);
        Assert.Equal(
            [
                "--distribution",
                "-hostile distro",
                "--user",
                "user name",
                "--cd",
                "/home/user/work tree",
            ],
            plan.Launch.Arguments);
        Assert.Equal("amber", plan.Launch.Environment["COLOR"]);
        Assert.Equal("safe value", plan.Launch.Environment["MODE"]);
        Assert.Equal("COLOR:MODE", plan.Launch.Environment["WSLENV"]);
        Assert.DoesNotContain("SECRET_MODE", plan.Launch.Environment.Keys, StringComparer.Ordinal);
        var requirement = Assert.Single(plan.SecretRequirements);
        Assert.Equal(ConnectionSecretRole.EnvironmentVariable, requirement.Role);
        Assert.Equal(secret, requirement.Reference);
    }

    [Fact]
    public async Task Test_runs_a_structured_distribution_probe()
    {
        using var vault = new RecordingSecretVault();
        var runner = new RecordingCommandRunner();
        var adapter = new WslConnectionRuntimeAdapter(
            vault,
            LocatorWithWsl(),
            runner,
            WindowsOptions());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Wsl("Ubuntu-24.04", "operator"));

        var report = ConnectionRuntimeTestSupport.Success(await adapter.TestAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionTestVerification.DistributionReachable, report.Verification);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(
            [
                "--distribution",
                "Ubuntu-24.04",
                "--user",
                "operator",
                "--exec",
                "/bin/true",
            ],
            command.Arguments);
    }

    [Fact]
    public async Task Missing_distribution_has_a_distinct_repair_action()
    {
        using var vault = new RecordingSecretVault();
        var runner = new RecordingCommandRunner
        {
            Result = new ConnectionProbeResult(
                ConnectionProbeOutcome.Exited,
                -1,
                "There is no distribution with the supplied name."),
        };
        var adapter = new WslConnectionRuntimeAdapter(
            vault,
            LocatorWithWsl(),
            runner,
            WindowsOptions());

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.TestAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Wsl("Removed")),
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.DistributionNotFound, error.Code);
        Assert.Equal(ConnectionRecoveryAction.SelectDistribution, error.RecoveryAction);
    }

    private static RecordingExecutableLocator LocatorWithWsl()
    {
        var locator = new RecordingExecutableLocator();
        locator.Add("wsl.exe", "C:\\Windows\\System32\\wsl.exe");
        return locator;
    }

    private static ConnectionRuntimeOptions WindowsOptions() =>
        new(ConnectionHostPlatform.Windows, "cmd.exe", "C:\\Users\\test");
}
