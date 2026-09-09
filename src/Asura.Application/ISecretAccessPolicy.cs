namespace Asura.Application;

public interface ISecretAccessPolicy
{
    bool IsAllowed(
        SecretVaultOperation operation,
        SecretScope? scope,
        SecretUsePurpose purpose);
}
