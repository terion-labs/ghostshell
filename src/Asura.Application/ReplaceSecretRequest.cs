using Asura.Core;

namespace Asura.Application;

public sealed record ReplaceSecretRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
