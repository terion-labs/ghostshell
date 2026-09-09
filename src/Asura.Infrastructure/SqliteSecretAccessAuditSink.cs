using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Projects vault-access records into the durable audit trail. User-controlled purpose target IDs
/// are omitted, and secret references are replaced by process-keyed pseudonyms. The raw reference
/// never reaches SQLite, even when a caller constructed a SecretRef from credential-shaped text.
/// </summary>
public sealed class SqliteSecretAccessAuditSink : ISecretAccessAuditSink, IDisposable
{
    private const string ReferencePseudonymPrefix = "secret-ref-";
    private const string SecretTargetKind = "secret";
    private static readonly ActorDescriptor VaultActor = new(
        new ActorId("asura-secret-vault"),
        ActorKind.System,
        "Asura secret vault");
    private readonly IAuditStore _auditStore;
    private readonly byte[] _referencePseudonymKey = RandomNumberGenerator.GetBytes(32);
    private bool _disposed;

    public SqliteSecretAccessAuditSink(IAuditStore auditStore)
    {
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
    }

    public async ValueTask<SecretAccessAuditResult> AppendAsync(
        SecretAccessAuditRecord record,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        if (!TryMap(record, out var auditEvent))
        {
            return SecretAccessAuditResult.Failed;
        }

        var result = await _auditStore.AppendAsync(auditEvent!, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? SecretAccessAuditResult.Succeeded
            : SecretAccessAuditResult.Failed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_referencePseudonymKey);
    }

    private bool TryMap(
        SecretAccessAuditRecord record,
        out AuditEventRecord? auditEvent)
    {
        auditEvent = null;
        var action = MapAction(record.Operation);
        var outcome = MapOutcome(record.Outcome);
        var requiresReference = record.Operation != SecretVaultOperation.ListMetadata;
        if (!TryGetCanonicalOpaqueId(record.CorrelationId.Value, out var correlationId)
            || !TryGetAuditReference(record.Reference, requiresReference, out var referenceValue)
            || record.Purpose is null
            || !Enum.IsDefined(record.Purpose.Kind)
            || !HasConsistentOutcome(record.Outcome, record.ErrorCode)
            || action is null
            || outcome is null)
        {
            return false;
        }

        auditEvent = new AuditEventRecord(
            RequestId.New().Value,
            correlationId,
            VaultActor,
            action,
            referenceValue is null ? null : new AuditTarget(SecretTargetKind, referenceValue),
            outcome.Value,
            AuditDetails.ForSecretAccess(record.Purpose.Kind, record.ErrorCode),
            record.OccurredAt);
        return true;
    }

    private bool TryGetAuditReference(
        SecretRef? reference,
        bool required,
        out string? auditReference)
    {
        auditReference = null;
        if (reference is null)
        {
            return !required;
        }

        if (!required || string.IsNullOrWhiteSpace(reference.Value.Value))
        {
            return false;
        }

        var encodedReference = Encoding.UTF8.GetBytes(reference.Value.Value);
        try
        {
            var pseudonym = HMACSHA256.HashData(_referencePseudonymKey, encodedReference);
            auditReference = $"{ReferencePseudonymPrefix}{Convert.ToHexString(pseudonym)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedReference);
        }

        return true;
    }

    private static bool TryGetCanonicalOpaqueId(string? value, out string canonicalValue)
    {
        canonicalValue = string.Empty;
        if (!Guid.TryParseExact(value, "N", out var parsed))
        {
            return false;
        }

        canonicalValue = parsed.ToString("N");
        return string.Equals(value, canonicalValue, StringComparison.Ordinal);
    }

    private static bool HasConsistentOutcome(
        SecretAccessAuditOutcome outcome,
        SecretVaultErrorCode? errorCode)
    {
        if (errorCode is { } code && !Enum.IsDefined(code))
        {
            return false;
        }

        return outcome switch
        {
            SecretAccessAuditOutcome.Requested or SecretAccessAuditOutcome.Succeeded =>
                errorCode is null,
            SecretAccessAuditOutcome.Denied => errorCode == SecretVaultErrorCode.AccessDenied,
            SecretAccessAuditOutcome.Cancelled => errorCode == SecretVaultErrorCode.Cancelled,
            SecretAccessAuditOutcome.Failed => errorCode is not null
                and not SecretVaultErrorCode.AccessDenied
                and not SecretVaultErrorCode.Cancelled,
            _ => false,
        };
    }

    private static string? MapAction(SecretVaultOperation operation) => operation switch
    {
        SecretVaultOperation.Create => "secret.create",
        SecretVaultOperation.Resolve => "secret.resolve",
        SecretVaultOperation.Replace => "secret.replace",
        SecretVaultOperation.Relabel => "secret.relabel",
        SecretVaultOperation.Delete => "secret.delete",
        SecretVaultOperation.GetMetadata => "secret.get-metadata",
        SecretVaultOperation.ListMetadata => "secret.list-metadata",
        _ => null,
    };

    private static AuditOutcome? MapOutcome(SecretAccessAuditOutcome outcome) => outcome switch
    {
        SecretAccessAuditOutcome.Requested => AuditOutcome.Started,
        SecretAccessAuditOutcome.Succeeded => AuditOutcome.Succeeded,
        SecretAccessAuditOutcome.Denied => AuditOutcome.Denied,
        SecretAccessAuditOutcome.Failed => AuditOutcome.Failed,
        SecretAccessAuditOutcome.Cancelled => AuditOutcome.Cancelled,
        _ => null,
    };
}
