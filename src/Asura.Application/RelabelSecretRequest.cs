using Asura.Core;

namespace Asura.Application;

public sealed record RelabelSecretRequest
{
    public RelabelSecretRequest(
        SecretRef reference,
        SecretScope scope,
        string label,
        SecretUsePurpose purpose)
    {
        Reference = reference;
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Label = SecretContract.RequireLabel(label);
        Purpose = purpose ?? throw new ArgumentNullException(nameof(purpose));
    }

    public SecretRef Reference { get; }

    public SecretScope Scope { get; }

    public string Label { get; }

    public SecretUsePurpose Purpose { get; }
}
