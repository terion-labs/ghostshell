using Asura.Core;

namespace Asura.Application;

public sealed record GetSecretMetadataRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
