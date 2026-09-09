using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class DockerConnectionRuntimeAdapterTests
{
    [Fact]
    public async Task Plan_keeps_context_container_and_environment_as_structured_arguments()
    {
        var secret = new SecretRef("docker-secret-env");
        using var vault = new RecordingSecretVault();
        vault.Add(secret, "docker-test");
        var locator = new RecordingExecutableLocator();
        locator.Add("docker", "/usr/local/bin/docker");
        var adapter = new DockerConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Docker(
                "container; touch /tmp/not-run",
                "context with spaces"),
            startup: new ConnectionStartup(
                "/workspace with spaces",
                [
                    new ConnectionEnvironmentVariable(
                        "PLAIN",
                        new ConnectionEnvironmentValue.PlainText("value; still one argument")),
                    new ConnectionEnvironmentVariable(
                        "SECRET",
                        new ConnectionEnvironmentValue.Secret(secret)),
                ]),
            id: "docker-test");

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal("/usr/local/bin/docker", plan.Launch.Executable);
        Assert.Null(plan.Launch.WorkingDirectory);
        Assert.Equal(
            "Docker: context with spaces/container; touch /tmp/not-run",
            plan.Launch.ConnectionMetadata?.ConnectionBoundary);
        Assert.Equal(
            "/workspace with spaces",
            plan.Launch.ConnectionMetadata?.InitialWorkingDirectory);
        Assert.Equal("value; still one argument", Assert.Single(plan.Launch.Environment).Value);
        Assert.Equal(ConnectionReconnectMode.BoundedBackoff, plan.ReconnectMode);
        Assert.Contains("context with spaces", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("container; touch /tmp/not-run", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("/workspace with spaces", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("PLAIN", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("PLAIN=value; still one argument", plan.Launch.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(
            plan.Launch.Arguments,
            argument => argument.Contains("do-not-leak", StringComparison.Ordinal));
        var requirement = Assert.Single(plan.SecretRequirements);
        Assert.Equal(ConnectionSecretRole.EnvironmentVariable, requirement.Role);
        Assert.Equal("SECRET", requirement.EnvironmentVariableName);
    }

    [Fact]
    public async Task Test_uses_docker_exec_without_a_shell_wrapper()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("docker", "/usr/local/bin/docker");
        var runner = new RecordingCommandRunner();
        var adapter = new DockerConnectionRuntimeAdapter(vault, locator, runner);
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Docker("app", "desktop-linux"));

        var report = ConnectionRuntimeTestSupport.Success(await adapter.TestAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionTestVerification.ContainerReachable, report.Verification);
        var command = Assert.Single(runner.Commands);
        Assert.Equal("/usr/local/bin/docker", command.Executable);
        Assert.Equal(
            ["--context", "desktop-linux", "exec", "--", "app", "/bin/true"],
            command.Arguments);
    }

    [Fact]
    public async Task Missing_docker_runtime_is_distinct_from_a_missing_container()
    {
        using var vault = new RecordingSecretVault();
        var missingRuntime = new DockerConnectionRuntimeAdapter(
            vault,
            new RecordingExecutableLocator(),
            new RecordingCommandRunner());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Docker("app"));

        var runtimeError = ConnectionRuntimeTestSupport.Failure(await missingRuntime.TestAsync(
            profile,
            null,
            CancellationToken.None));

        var locator = new RecordingExecutableLocator();
        locator.Add("docker", "/usr/bin/docker");
        var runner = new RecordingCommandRunner
        {
            Result = new ConnectionProbeResult(
                ConnectionProbeOutcome.Exited,
                1,
                "Error response from daemon: No such container: app"),
        };
        var missingContainer = new DockerConnectionRuntimeAdapter(vault, locator, runner);
        var containerError = ConnectionRuntimeTestSupport.Failure(await missingContainer.TestAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.RuntimeMissing, runtimeError.Code);
        Assert.Equal(ConnectionRuntimeErrorCode.ContainerNotFound, containerError.Code);
        Assert.Equal(ConnectionRecoveryAction.SelectContainer, containerError.RecoveryAction);
    }
}
