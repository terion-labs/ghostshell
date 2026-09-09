using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteSecretAccessAuditSinkTests
{
    [Fact]
    public async Task PersistsOnlyOpaqueAndAllowlistedVaultMetadata()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        using var sink = new SqliteSecretAccessAuditSink(store);
        var correlationId = RequestId.New();
        var reference = SecretRef.New();
        const string targetIdCanary = "user-controlled-purpose-target-must-not-be-persisted";
        var record = new SecretAccessAuditRecord(
            correlationId,
            reference,
            SecretVaultOperation.Resolve,
            new SecretUsePurpose(SecretUseKind.AiProviderAuthentication, targetIdCanary),
            SecretAccessAuditOutcome.Denied,
            SecretVaultErrorCode.AccessDenied,
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));

        var append = await sink.AppendAsync(record, CancellationToken.None);
        var trail = await store.ListByCorrelationAsync(
            correlationId.Value,
            CancellationToken.None);

        Assert.True(append.IsSuccess);
        Assert.True(trail.IsSuccess, trail.Error?.Message);
        var auditEvent = Assert.Single(trail.Value!);
        Assert.Equal("secret.resolve", auditEvent.Action);
        Assert.Equal("secret", auditEvent.Target!.Kind);
        Assert.StartsWith("secret-ref-", auditEvent.Target.Id, StringComparison.Ordinal);
        Assert.NotEqual(reference.Value, auditEvent.Target.Id, StringComparer.Ordinal);
        Assert.Equal(AuditOutcome.Denied, auditEvent.Outcome);
        var details = Assert.IsType<AuditDetails.SecretAccessDetails>(auditEvent.Details);
        Assert.Equal(SecretUseKind.AiProviderAuthentication, details.PurposeKind);
        Assert.Equal(SecretVaultErrorCode.AccessDenied, details.ErrorCode);

        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id || '|' || correlation_id || '|' || actor_kind || '|' || actor_id
                || '|' || action || '|' || COALESCE(target_kind, '') || '|'
                || COALESCE(target_id, '') || '|' || outcome || '|' || details_json || '|'
                || occurred_utc
            FROM audit_events
            WHERE correlation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", correlationId.Value);
        var storedRow = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain(targetIdCanary, storedRow, StringComparison.Ordinal);
        Assert.DoesNotContain(reference.Value, storedRow, StringComparison.Ordinal);
        Assert.DoesNotContain("password", storedRow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretValue", storedRow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAnUnknownVaultOperationWithoutWriting()
    {
        await using var temporary = TemporaryDatabase.Create();
        using var sink = new SqliteSecretAccessAuditSink(
            new SqliteAuditStore(temporary.Database));
        var record = new SecretAccessAuditRecord(
            RequestId.New(),
            SecretRef.New(),
            (SecretVaultOperation)999,
            SecretUsePurpose.ManageGlobal(),
            SecretAccessAuditOutcome.Requested,
            null,
            DateTimeOffset.UtcNow);

        var result = await sink.AppendAsync(record, CancellationToken.None);

        Assert.False(result.IsSuccess);
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PseudonymizesLegacyFreeFormReferencesWithoutBlockingTheAudit()
    {
        await using var temporary = TemporaryDatabase.Create();
        using var sink = new SqliteSecretAccessAuditSink(
            new SqliteAuditStore(temporary.Database));
        using var vault = new AuditedSecretVault(new InMemorySecretVault(), sink);
        const string legacyReferenceCanary = "valid-legacy-reference-must-not-be-logged";
        var reference = new SecretRef(legacyReferenceCanary);
        var scope = new SecretScope(SecretScopeKind.AiProvider, "provider-1");
        var purpose = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            "provider-1");
        using var material = SecretMaterial.CopyFrom([1, 2, 3]);

        var result = await vault.CreateAsync(
            new CreateSecretRequest(reference, "Provider key", SecretKind.ApiKey, scope, purpose),
            material,
            CancellationToken.None);

        Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(result);
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT GROUP_CONCAT(target_id, '|')
            FROM audit_events;
            """;
        var stored = Assert.IsType<string>(await command.ExecuteScalarAsync());
        var storedReferences = stored.Split('|');
        Assert.Equal(2, storedReferences.Length);
        Assert.Single(storedReferences.Distinct(StringComparer.Ordinal));
        Assert.StartsWith("secret-ref-", storedReferences[0], StringComparison.Ordinal);
        Assert.DoesNotContain(legacyReferenceCanary, stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsInvalidCorrelationAndContradictoryMetadataWithoutWriting()
    {
        await using var temporary = TemporaryDatabase.Create();
        using var sink = new SqliteSecretAccessAuditSink(
            new SqliteAuditStore(temporary.Database));
        var purpose = SecretUsePurpose.ManageGlobal();
        var canonicalCorrelation = RequestId.New();
        var canonicalReference = SecretRef.New();
        SecretAccessAuditRecord[] invalidRecords =
        [
            new(
                new RequestId("raw-correlation-canary"),
                canonicalReference,
                SecretVaultOperation.Resolve,
                purpose,
                SecretAccessAuditOutcome.Requested,
                null,
                DateTimeOffset.UtcNow),
            new(
                canonicalCorrelation,
                null,
                SecretVaultOperation.Resolve,
                purpose,
                SecretAccessAuditOutcome.Requested,
                null,
                DateTimeOffset.UtcNow),
            new(
                canonicalCorrelation,
                canonicalReference,
                SecretVaultOperation.ListMetadata,
                purpose,
                SecretAccessAuditOutcome.Requested,
                null,
                DateTimeOffset.UtcNow),
            new(
                canonicalCorrelation,
                canonicalReference,
                SecretVaultOperation.Resolve,
                purpose,
                SecretAccessAuditOutcome.Succeeded,
                SecretVaultErrorCode.AccessDenied,
                DateTimeOffset.UtcNow),
            new(
                canonicalCorrelation,
                canonicalReference,
                SecretVaultOperation.Resolve,
                purpose,
                SecretAccessAuditOutcome.Denied,
                null,
                DateTimeOffset.UtcNow),
        ];

        foreach (var record in invalidRecords)
        {
            Assert.False((await sink.AppendAsync(record, CancellationToken.None)).IsSuccess);
        }

        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
