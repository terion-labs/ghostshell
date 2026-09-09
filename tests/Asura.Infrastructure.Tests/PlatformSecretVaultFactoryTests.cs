using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class PlatformSecretVaultFactoryTests
{
    [Fact]
    public void Unsupported_platform_returns_a_diagnostic_fail_closed_vault()
    {
        var result = PlatformSecretVaultFactory.Create(new SecretVaultFactoryOptions
        {
            Platform = SecretVaultPlatform.Unsupported,
        });
        using var vault = result.Vault;

        Assert.Equal(SecretVaultPlatform.Unsupported, result.Diagnostic.Platform);
        Assert.Equal("platform_not_supported", result.Diagnostic.StableCode);
        Assert.Equal(SecretVaultAvailabilityState.Unavailable, result.Diagnostic.Availability.State);
        Assert.False(vault.Availability.CanPersist);
    }

    [Fact]
    public void Missing_linux_helper_is_reported_without_falling_back_to_plaintext_storage()
    {
        var result = PlatformSecretVaultFactory.Create(new SecretVaultFactoryOptions
        {
            Platform = SecretVaultPlatform.Linux,
            LinuxSecretToolPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "secret-tool"),
        });
        using var vault = result.Vault;

        Assert.Equal("linux_secret_service_tool_missing", result.Diagnostic.StableCode);
        Assert.Equal(SecretVaultPersistenceKind.None, vault.Availability.Persistence);
        Assert.Equal(SecretVaultCapabilities.None, vault.Availability.Capabilities);
    }

    [Fact]
    public void Automatic_selection_always_exposes_non_secret_diagnostics()
    {
        var result = PlatformSecretVaultFactory.Create();
        using var vault = result.Vault;

        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic.Adapter));
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic.StableCode));
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic.Message));
        Assert.Equal(vault.Availability, result.Diagnostic.Availability);
    }
}
