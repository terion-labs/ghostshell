using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Records requested and completed vault operations without ever receiving or serializing secret bytes.
/// A failed initial audit write prevents the underlying vault operation.
/// </summary>
public sealed class AuditedSecretVault : ISecretVault
{
    private readonly ISecretVault _inner;
    private readonly ISecretAccessAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;

    public AuditedSecretVault(
        ISecretVault inner,
        ISecretAccessAuditSink auditSink,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SecretVaultAvailability Availability => _inner.Availability;

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            request.Reference,
            SecretVaultOperation.Create,
            request.Purpose,
            () => _inner.CreateAsync(request, material, cancellationToken));

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            request.Reference,
            SecretVaultOperation.Resolve,
            request.Purpose,
            () => _inner.ResolveAsync(request, cancellationToken));

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            request.Reference,
            SecretVaultOperation.Replace,
            request.Purpose,
            () => _inner.ReplaceAsync(request, material, cancellationToken));

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            request.Reference,
            SecretVaultOperation.Relabel,
            request.Purpose,
            () => _inner.RelabelAsync(request, cancellationToken));

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            request.Reference,
            SecretVaultOperation.Delete,
            request.Purpose,
            () => _inner.DeleteAsync(request, cancellationToken));

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            request.Reference,
            SecretVaultOperation.GetMetadata,
            request.Purpose,
            () => _inner.GetMetadataAsync(request, cancellationToken));

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            null,
            SecretVaultOperation.ListMetadata,
            request.Purpose,
            () => _inner.ListMetadataAsync(request, cancellationToken));

    public void Dispose() => _inner.Dispose();

    private async ValueTask<SecretVaultResult<T>> ExecuteAsync<T>(
        SecretRef? reference,
        SecretVaultOperation operation,
        SecretUsePurpose purpose,
        Func<ValueTask<SecretVaultResult<T>>> execute)
    {
        var correlationId = RequestId.New();
        var requested = await AppendAuditAsync(
                correlationId,
                reference,
                operation,
                purpose,
                SecretAccessAuditOutcome.Requested,
                null)
            .ConfigureAwait(false);
        if (!requested)
        {
            return SecretVaultFailures.PlatformFailure<T>();
        }

        SecretVaultResult<T> result;
        try
        {
            result = await execute().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = SecretVaultFailures.Cancelled<T>();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            result = SecretVaultFailures.PlatformFailure<T>();
        }

        var (outcome, errorCode) = Outcome(result);
        var completed = await AppendAuditAsync(
                correlationId,
                reference,
                operation,
                purpose,
                outcome,
                errorCode)
            .ConfigureAwait(false);
        if (completed)
        {
            return result;
        }

        if (result is SecretVaultResult<T>.Success { Value: SecretMaterial material })
        {
            material.Dispose();
        }

        return SecretVaultFailures.AuditPersistenceFailure<T>();
    }

    private async ValueTask<bool> AppendAuditAsync(
        RequestId correlationId,
        SecretRef? reference,
        SecretVaultOperation operation,
        SecretUsePurpose purpose,
        SecretAccessAuditOutcome outcome,
        SecretVaultErrorCode? errorCode)
    {
        try
        {
            var result = await _auditSink.AppendAsync(
                    new SecretAccessAuditRecord(
                        correlationId,
                        reference,
                        operation,
                        purpose,
                        outcome,
                        errorCode,
                        _timeProvider.GetUtcNow()),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return result.IsSuccess;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    private static (SecretAccessAuditOutcome Outcome, SecretVaultErrorCode? ErrorCode) Outcome<T>(
        SecretVaultResult<T> result) => result switch
        {
            SecretVaultResult<T>.Success => (SecretAccessAuditOutcome.Succeeded, null),
            SecretVaultResult<T>.Failure { Error.Code: SecretVaultErrorCode.AccessDenied } failure =>
                (SecretAccessAuditOutcome.Denied, failure.Error.Code),
            SecretVaultResult<T>.Failure { Error.Code: SecretVaultErrorCode.Cancelled } failure =>
                (SecretAccessAuditOutcome.Cancelled, failure.Error.Code),
            SecretVaultResult<T>.Failure failure =>
                (SecretAccessAuditOutcome.Failed, failure.Error.Code),
            _ => (SecretAccessAuditOutcome.Failed, SecretVaultErrorCode.PlatformFailure),
        };
}
