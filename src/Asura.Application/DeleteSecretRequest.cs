using Asura.Core;

namespace Asura.Application;

public sealed record DeleteSecretRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
