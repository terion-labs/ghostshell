using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// The one keystore accessor for application-security material — encryption
/// keys and the startup-protection pepper. Deliberately separate from the
/// audited credential vault: its consumers run before the audit store can
/// open, and nothing of the user's credentials goes through it.
/// </summary>
public sealed class ApplicationSecurityVault(ISecretVault vault) : IDisposable
{
    public ISecretVault Vault { get; } =
        vault ?? throw new ArgumentNullException(nameof(vault));

    public void Dispose() => Vault.Dispose();
}
