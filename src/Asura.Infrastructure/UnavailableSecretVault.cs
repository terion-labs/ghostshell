using Asura.Application;

namespace Asura.Infrastructure;

public sealed class UnavailableSecretVault : ISecretVault
{
    private readonly ISecretAccessPolicy _accessPolicy;

    public UnavailableSecretVault(
        string diagnosticCode,
        string message,
        string adapter = "unavailable",
        ISecretAccessPolicy? accessPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        _accessPolicy = accessPolicy ?? SecretScopeAccessPolicy.Default;

        Availability = new SecretVaultAvailability(
            SecretVaultAvailabilityState.Unavailable,
            SecretVaultPersistenceKind.None,
            SecretVaultCapabilities.None,
            adapter,
            diagnosticCode,
            message);
    }

    public SecretVaultAvailability Availability { get; }

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<SecretMetadata>(
            SecretVaultOperation.Create,
            request.Scope,
            request.Purpose,
            cancellationToken));

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<SecretMaterial>(
            SecretVaultOperation.Resolve,
            request.Scope,
            request.Purpose,
            cancellationToken));

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<SecretMetadata>(
            SecretVaultOperation.Replace,
            request.Scope,
            request.Purpose,
            cancellationToken));

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<SecretMetadata>(
            SecretVaultOperation.Relabel,
            request.Scope,
            request.Purpose,
            cancellationToken));

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<Unit>(
            SecretVaultOperation.Delete,
            request.Scope,
            request.Purpose,
            cancellationToken));

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<SecretMetadata>(
            SecretVaultOperation.GetMetadata,
            request.Scope,
            request.Purpose,
            cancellationToken));

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<IReadOnlyList<SecretMetadata>>(
            SecretVaultOperation.ListMetadata,
            request.Scope,
            request.Purpose,
            cancellationToken));

    public void Dispose()
    {
    }

    private SecretVaultResult<T> Result<T>(
        SecretVaultOperation operation,
        SecretScope? scope,
        SecretUsePurpose purpose,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<T>(
            _accessPolicy,
            operation,
            scope,
            purpose);
        if (denied is not null)
        {
            return denied;
        }

        return cancellationToken.IsCancellationRequested
            ? SecretVaultFailures.Cancelled<T>()
            : SecretVaultFailures.Unavailable<T>();
    }
}
