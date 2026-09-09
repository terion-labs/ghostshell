using Asura.Application;

namespace Asura.Infrastructure;

public sealed record SecretVaultFactoryOptions
{
    public string ServiceName { get; init; } = ApplicationStorageIdentity.SecretServiceName;

    public string? DataDirectory { get; init; }

    public string? LinuxSecretToolPath { get; init; }

    public SecretVaultPlatform Platform { get; init; } = SecretVaultPlatform.Automatic;

    public ISecretAccessPolicy? AccessPolicy { get; init; }

    public ISecretAccessAuditSink? AuditSink { get; init; }
}
