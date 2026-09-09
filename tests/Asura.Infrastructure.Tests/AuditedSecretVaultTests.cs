using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class AuditedSecretVaultTests
{
    private static readonly SecretScope Scope = new(SecretScopeKind.AiProvider, "provider-1");
    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.AiProviderAuthentication,
        "provider-1");

    [Fact]
    public async Task Audit_contains_only_reference_purpose_operation_and_outcome()
    {
        var sink = new RecordingAuditSink();
        using var vault = new AuditedSecretVault(new InMemorySecretVault(), sink);
        var reference = SecretRef.New();
        const string sentinel = "secret-canary-that-must-not-be-audited";
        using var material = SecretMaterial.CopyFrom(System.Text.Encoding.UTF8.GetBytes(sentinel));
        Success(await vault.CreateAsync(
            new CreateSecretRequest(reference, "Provider key", SecretKind.ApiKey, Scope, Purpose),
            material,
            default));

        AssertDenied(await vault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                Scope,
                new SecretUsePurpose(SecretUseKind.AiProviderAuthentication, "provider-2")),
            default));

        Assert.Collection(
            sink.Records,
            record => Assert.Equal(SecretAccessAuditOutcome.Requested, record.Outcome),
            record => Assert.Equal(SecretAccessAuditOutcome.Succeeded, record.Outcome),
            record => Assert.Equal(SecretAccessAuditOutcome.Requested, record.Outcome),
            record => Assert.Equal(SecretAccessAuditOutcome.Denied, record.Outcome));
        Assert.All(sink.Records, record => Assert.Equal(reference, record.Reference));
        Assert.Equal(sink.Records[0].CorrelationId, sink.Records[1].CorrelationId);
        Assert.Equal(sink.Records[2].CorrelationId, sink.Records[3].CorrelationId);
        Assert.NotEqual(sink.Records[0].CorrelationId, sink.Records[2].CorrelationId);
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(sink.Records), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_requested_audit_prevents_the_underlying_mutation()
    {
        using var inner = new InMemorySecretVault();
        var sink = new RecordingAuditSink { FailWrites = true };
        using var vault = new AuditedSecretVault(inner, sink);
        var reference = SecretRef.New();
        using var material = SecretMaterial.CopyFrom([1, 2, 3]);

        var result = await vault.CreateAsync(
            new CreateSecretRequest(reference, "Provider key", SecretKind.ApiKey, Scope, Purpose),
            material,
            default);

        Assert.Equal(
            SecretVaultErrorCode.PlatformFailure,
            Assert.IsType<SecretVaultResult<SecretMetadata>.Failure>(result).Error.Code);
        Assert.Equal(
            SecretVaultErrorCode.NotFound,
            Assert.IsType<SecretVaultResult<SecretMetadata>.Failure>(
                await inner.GetMetadataAsync(
                    new GetSecretMetadataRequest(reference, Scope, Purpose),
                    default)).Error.Code);
    }

    [Fact]
    public async Task Failed_completion_audit_reports_ambiguous_persisted_state()
    {
        using var inner = new InMemorySecretVault();
        var sink = new RecordingAuditSink { FailOnWriteNumber = 2 };
        using var vault = new AuditedSecretVault(inner, sink);
        var reference = SecretRef.New();
        using var material = SecretMaterial.CopyFrom([1, 2, 3]);

        var result = await vault.CreateAsync(
            new CreateSecretRequest(reference, "Provider key", SecretKind.ApiKey, Scope, Purpose),
            material,
            default);

        Assert.Equal(
            SecretVaultErrorCode.AuditPersistenceFailure,
            Assert.IsType<SecretVaultResult<SecretMetadata>.Failure>(result).Error.Code);
        Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(
            await inner.GetMetadataAsync(
                new GetSecretMetadataRequest(reference, Scope, Purpose),
                default));
        Assert.Single(sink.Records);
    }

    private static T Success<T>(SecretVaultResult<T> result) =>
        Assert.IsType<SecretVaultResult<T>.Success>(result).Value;

    private static void AssertDenied<T>(SecretVaultResult<T> result) =>
        Assert.Equal(
            SecretVaultErrorCode.AccessDenied,
            Assert.IsType<SecretVaultResult<T>.Failure>(result).Error.Code);

    private sealed class RecordingAuditSink : ISecretAccessAuditSink
    {
        public List<SecretAccessAuditRecord> Records { get; } = [];

        public bool FailWrites { get; init; }

        public int? FailOnWriteNumber { get; init; }

        private int WriteCount { get; set; }

        public ValueTask<SecretAccessAuditResult> AppendAsync(
            SecretAccessAuditRecord record,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            var failed = FailWrites || WriteCount == FailOnWriteNumber;
            if (!failed)
            {
                Records.Add(record);
            }

            return ValueTask.FromResult(
                failed ? SecretAccessAuditResult.Failed : SecretAccessAuditResult.Succeeded);
        }
    }
}
