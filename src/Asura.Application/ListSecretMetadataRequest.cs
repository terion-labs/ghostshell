namespace Asura.Application;

public sealed record ListSecretMetadataRequest(SecretScope? Scope, SecretUsePurpose Purpose);
