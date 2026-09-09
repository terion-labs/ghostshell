using Asura.Application;

namespace Asura.Infrastructure;

public sealed record SecretVaultFactoryResult(
    ISecretVault Vault,
    SecretVaultFactoryDiagnostic Diagnostic);
