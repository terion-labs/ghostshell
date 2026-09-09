using Asura.Core;

namespace Asura.Application;

public sealed record CreateSecretRequest
{
    public CreateSecretRequest(
        SecretRef reference,
        string label,
        SecretKind kind,
        SecretScope scope,
        SecretUsePurpose purpose)
    {
        Reference = reference;
        Label = SecretContract.RequireLabel(label);
        Kind = kind;
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Purpose = purpose ?? throw new ArgumentNullException(nameof(purpose));
    }

    public SecretRef Reference { get; }

    public string Label { get; }

    public SecretKind Kind { get; }

    public SecretScope Scope { get; }

    public SecretUsePurpose Purpose { get; }
}
