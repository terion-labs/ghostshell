using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class LocalConnectionRuntimeAdapterTests
{
    [Fact]
    public async Task Plan_uses_a_resolved_shell_and_keeps_secret_environment_out_of_launch_data()
    {
        var secret = new SecretRef("secret-local-env");
        using var vault = new RecordingSecretVault();
        vault.Add(secret, "local-test");
        var locator = new RecordingExecutableLocator();
        locator.Add("/bin/zsh", "/resolved/bin/zsh");
        var runner = new RecordingCommandRunner();
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            runner,
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.MacOs,
                "/bin/zsh",
                "/Users/test"));
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Local(),
            startup: new ConnectionStartup(
                "/work tree",
                [
                    new ConnectionEnvironmentVariable(
                        "PLAIN_VALUE",
                        new ConnectionEnvironmentValue.PlainText("amber value")),
                    new ConnectionEnvironmentVariable(
                        "SECRET_VALUE",
                        new ConnectionEnvironmentValue.Secret(secret)),
                ],
                command: "npm run dev"),
            id: "local-test");
        var progress = new RecordingConnectionProgress();

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            progress,
            CancellationToken.None));

        Assert.Equal("/resolved/bin/zsh", plan.Launch.Executable);
        Assert.Equal("/work tree", plan.Launch.WorkingDirectory);
        Assert.Equal(new ConnectionId("local-test"), plan.Launch.ConnectionId);
        Assert.Equal(
            "Local: Test connection",
            plan.Launch.ConnectionMetadata?.ConnectionBoundary);
        Assert.Equal(
            "/work tree",
            plan.Launch.ConnectionMetadata?.InitialWorkingDirectory);
        Assert.Equal(["-l"], plan.Launch.Arguments);
        Assert.Equal("npm run dev", plan.Launch.InitialCommand);
        Assert.Equal("amber value", Assert.Single(plan.Launch.Environment).Value);
        Assert.DoesNotContain("SECRET_VALUE", plan.Launch.Environment.Keys, StringComparer.Ordinal);
        var requirement = Assert.Single(plan.SecretRequirements);
        Assert.Equal(ConnectionSecretRole.EnvironmentVariable, requirement.Role);
        Assert.Equal(secret, requirement.Reference);
        Assert.Equal("SECRET_VALUE", requirement.EnvironmentVariableName);
        Assert.Contains(ConnectionPlanWarning.SecretBrokerRequired, plan.Warnings);
        Assert.Null(vault.LastMaterial);
        Assert.Empty(vault.ResolveRequests);

        var request = Assert.Single(vault.MetadataRequests);
        Assert.Equal(new SecretScope(SecretScopeKind.Connection, "local-test"), request.Scope);
        Assert.Equal(
            new SecretUsePurpose(SecretUseKind.ConnectionEnvironment, "local-test"),
            request.Purpose);
        Assert.Equal(
            [
                ConnectionProgressStage.ValidatingProfile,
                ConnectionProgressStage.DetectingRuntime,
                ConnectionProgressStage.ResolvingCredentials,
                ConnectionProgressStage.BuildingLaunchPlan,
                ConnectionProgressStage.Completed,
            ],
            progress.Updates.Select(update => update.Stage));
        Assert.DoesNotContain(
            progress.Updates,
            update => update.Message.Contains("do-not-leak", StringComparison.Ordinal));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Explicit_shell_is_resolved_without_invoking_a_shell_command()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("custom shell", "/tools/custom shell");
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Linux,
                "/bin/sh",
                "/home/test"));
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Local("custom shell"));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(["custom shell"], locator.Requests);
        Assert.Equal("/tools/custom shell", plan.Launch.Executable);
        Assert.Equal(["-l"], plan.Launch.Arguments);
    }

    [Theory]
    [InlineData(ConnectionHostPlatform.MacOs, "/Users/test")]
    [InlineData(ConnectionHostPlatform.Linux, "/home/test")]
    public async Task Default_posix_launch_uses_the_user_profile_and_a_login_shell(
        ConnectionHostPlatform platform,
        string userProfileDirectory)
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("default-shell", "/resolved/default-shell");
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(platform, "default-shell", userProfileDirectory));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local()),
            null,
            CancellationToken.None));

        Assert.Equal(userProfileDirectory, plan.Launch.WorkingDirectory);
        Assert.Equal(["-l"], plan.Launch.Arguments);
        Assert.Equal(
            userProfileDirectory,
            plan.Launch.ConnectionMetadata?.InitialWorkingDirectory);
    }

    [Fact]
    public async Task Windows_launch_uses_the_user_profile_without_posix_login_arguments()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("cmd.exe", "C:\\Windows\\System32\\cmd.exe");
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Windows,
                "cmd.exe",
                "C:\\Users\\test"));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local()),
            null,
            CancellationToken.None));

        Assert.Equal("C:\\Users\\test", plan.Launch.WorkingDirectory);
        Assert.Empty(plan.Launch.Arguments);
    }

    [Fact]
    public async Task Missing_shell_returns_a_typed_install_runtime_error()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            new RecordingExecutableLocator(),
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Linux,
                "/missing/sh",
                "/home/test"));

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local()),
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.RuntimeMissing, error.Code);
        Assert.Equal(ConnectionRecoveryAction.InstallRuntime, error.RecoveryAction);
    }

    [Fact]
    public async Task Cancelled_plan_returns_a_typed_result_before_runtime_or_vault_access()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("/bin/sh", "/bin/sh");
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Linux,
                "/bin/sh",
                "/home/test"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local()),
            null,
            cancellation.Token));

        Assert.Equal(ConnectionRuntimeErrorCode.Cancelled, error.Code);
        Assert.Empty(locator.Requests);
        Assert.Empty(vault.ResolveRequests);
        Assert.Empty(vault.MetadataRequests);
    }
}
