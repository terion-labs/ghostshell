using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class ConnectionRuntimeTests
{
    [Fact]
    public async Task Router_selects_the_adapter_by_durable_connection_kind()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("/bin/sh", "/bin/sh");
        var local = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Linux,
                "/bin/sh",
                "/home/test"));
        IConnectionRuntime runtime = new ConnectionRuntime([local]);

        var plan = ConnectionRuntimeTestSupport.Success(await runtime.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local()),
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionKind.Local, plan.Kind);
    }

    [Fact]
    public async Task Missing_adapter_returns_a_typed_capability_error()
    {
        IConnectionRuntime runtime = new ConnectionRuntime([]);

        var error = ConnectionRuntimeTestSupport.Failure(await runtime.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Docker("app")),
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.AdapterUnavailable, error.Code);
    }

    [Fact]
    public void Duplicate_adapter_registration_is_rejected_at_composition_time()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        var runner = new RecordingCommandRunner();
        var options = new ConnectionRuntimeOptions(
            ConnectionHostPlatform.Linux,
            "/bin/sh",
            "/home/test");
        var first = new LocalConnectionRuntimeAdapter(vault, locator, runner, options);
        var second = new LocalConnectionRuntimeAdapter(vault, locator, runner, options);

        Assert.Throws<ArgumentException>(() => new ConnectionRuntime([first, second]));
    }

    [Theory]
    [InlineData(SecretVaultErrorCode.InvalidRequest, ConnectionRuntimeErrorCode.InvalidProfile)]
    [InlineData(SecretVaultErrorCode.Unavailable, ConnectionRuntimeErrorCode.SecretVaultUnavailable)]
    [InlineData(SecretVaultErrorCode.NotFound, ConnectionRuntimeErrorCode.SecretNotFound)]
    [InlineData(SecretVaultErrorCode.AlreadyExists, ConnectionRuntimeErrorCode.SecretVaultFailure)]
    [InlineData(SecretVaultErrorCode.AccessDenied, ConnectionRuntimeErrorCode.SecretAccessDenied)]
    [InlineData(SecretVaultErrorCode.CorruptEntry, ConnectionRuntimeErrorCode.SecretInvalid)]
    [InlineData(SecretVaultErrorCode.AuthenticationRequired, ConnectionRuntimeErrorCode.AuthenticationRequired)]
    [InlineData(SecretVaultErrorCode.UserCancelled, ConnectionRuntimeErrorCode.Cancelled)]
    [InlineData(SecretVaultErrorCode.Cancelled, ConnectionRuntimeErrorCode.Cancelled)]
    [InlineData(SecretVaultErrorCode.PlatformFailure, ConnectionRuntimeErrorCode.SecretVaultFailure)]
    [InlineData(SecretVaultErrorCode.AuditPersistenceFailure, ConnectionRuntimeErrorCode.SecretVaultFailure)]
    public async Task Vault_failures_are_mapped_to_fixed_connection_errors(
        SecretVaultErrorCode vaultCode,
        ConnectionRuntimeErrorCode expectedCode)
    {
        var secret = new SecretRef("sensitive-reference");
        using var vault = new RecordingSecretVault { ForcedError = vaultCode };
        var locator = new RecordingExecutableLocator();
        locator.Add("ssh", "/usr/bin/ssh");
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner());
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example"),
            new ConnectionAuthentication.Password(secret));

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.DoesNotContain(secret.Value, error.Message, StringComparison.Ordinal);
    }
}
