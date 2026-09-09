using Asura.Application;

namespace Asura.Infrastructure;

public sealed record SecretVaultFactoryDiagnostic(
    SecretVaultPlatform Platform,
    string Adapter,
    string StableCode,
    string Message,
    SecretVaultAvailability Availability);
