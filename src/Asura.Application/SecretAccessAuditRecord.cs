using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Contains only an opaque reference and non-secret access metadata. Secret material is structurally absent.
/// </summary>
public sealed record SecretAccessAuditRecord(
    RequestId CorrelationId,
    SecretRef? Reference,
    SecretVaultOperation Operation,
    SecretUsePurpose Purpose,
    SecretAccessAuditOutcome Outcome,
    SecretVaultErrorCode? ErrorCode,
    DateTimeOffset OccurredAt);
