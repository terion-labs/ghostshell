using Asura.Core;

namespace Asura.Application;

public sealed record ResolveSecretRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
