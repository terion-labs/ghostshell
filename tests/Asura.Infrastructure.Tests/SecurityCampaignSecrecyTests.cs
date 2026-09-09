using System.IO.Compression;
using System.Text;
using Asura.Application;
using Asura.Core;
using Asura.SecurityCampaign.Tests;

namespace Asura.Infrastructure.Tests;

public sealed class SecurityCampaignSecrecyTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "secrecy.persistence-sqlite real checkpoint and audit stores contain only canary digests")]
    [Trait("SecurityCampaignCase", "secrecy.persistence-sqlite")]
    public async Task PersistenceStoresOnlyCanaryDigestsAndCounts()
    {
        await using var temporary = TemporaryDatabase.Create();
        Assert.Throws<ArgumentException>(() => CreateCheckpoint(
            $$"""{"transcript":[{"password":"{{SecurityCampaignCanaries.ApplicationManaged}}"}]}"""));
        Assert.Throws<ArgumentException>(() => CreateCheckpoint(
            $$"""{"toolResults":[{"authorization":"{{SecurityCampaignCanaries.VaultResolved}}"}]}"""));

        var applicationDigest = AgentActionDigest.FromUtf8(
            SecurityCampaignCanaries.ApplicationManaged);
        var vaultDigest = AgentActionDigest.FromUtf8(
            SecurityCampaignCanaries.VaultResolved);
        var aggregateDigest = AgentActionDigest.FromUtf8(SecurityCampaignCanaries.Joined);
        Assert.Equal(SecurityCampaignCanaries.Digest, aggregateDigest.Value);
        var checkpoint = CreateCheckpoint(
            $$"""
            {"canaryDigests":["{{applicationDigest.Value}}","{{vaultDigest.Value}}"],"aggregateDigest":"{{aggregateDigest.Value}}","canaryCount":2}
            """);
        var checkpointStore = new SqliteAgentSessionCheckpointStore(temporary.Database);
        Assert.True(
            (await checkpointStore.SaveAsync(checkpoint, CancellationToken.None)).IsSuccess);

        var auditDetails = AuditDetails.ForAgentAction(
            new AgentRunId("campaign-run"),
            AgentCapability.RunCommands,
            AgentActionRisk.Mutation,
            AgentPermission.Ask,
            AgentPolicyDecision.RequiresApproval,
            aggregateDigest,
            AgentAuthorizationSource.HumanApproval,
            resultCode: "ok",
            binding: new AgentActionAuditBinding(
                policyGeneration: 1,
                targetIdentity: applicationDigest,
                approvalIdDigest: vaultDigest,
                approvalDuration: AgentApprovalDuration.Once,
                authorizationIdDigest: applicationDigest,
                resultCount: 1));
        var auditStore = new SqliteAuditStore(temporary.Database);
        Assert.True((await auditStore.AppendAsync(
            new AuditEventRecord(
                "campaign-event",
                "campaign-correlation",
                new ActorDescriptor(
                    new ActorId("campaign-agent"),
                    ActorKind.Agent,
                    "Agent"),
                BuiltInAgentTools.TerminalSendText,
                new AuditTarget("agent-target-fingerprint", vaultDigest.Value),
                AuditOutcome.Succeeded,
                auditDetails,
                CapturedAt),
            CancellationToken.None)).IsSuccess);

        var persistedAudit = Assert.Single((await auditStore.ListByCorrelationAsync(
            "campaign-correlation",
            CancellationToken.None)).Value!);
        var persistedDetails = Assert.IsType<AuditDetails.AgentActionDetails>(
            persistedAudit.Details);
        Assert.Equal(1, persistedDetails.Binding.ResultCount);
        Assert.Equal(applicationDigest, persistedDetails.Binding.TargetIdentity);
        Assert.Equal(vaultDigest, persistedDetails.Binding.ApprovalIdDigest);

        await temporary.ReopenAsync();
        foreach (var path in Directory.EnumerateFiles(temporary.DirectoryPath))
        {
            AssertCanariesAbsent(
                SecurityCampaignCanaries.Values,
                await File.ReadAllBytesAsync(path));
        }
    }

    [Fact(DisplayName = "secrecy.diagnostics-zip real diagnostics exporter redacts shared canaries")]
    [Trait("SecurityCampaignCase", "secrecy.diagnostics-zip")]
    public async Task DiagnosticsExporterRedactsSharedCanaries()
    {
        var diagnosticsRequest = new DiagnosticsBundleRequest(
            new DiagnosticsBundleMetadata(
                "Asura",
                "com.terionlabs.asura",
                "Asura",
                "1.0.0",
                ".NET",
                "macOS",
                "arm64",
                CapturedAt),
            [
                new DiagnosticsBundleArtifact(
                    "logs/application.log",
                    DiagnosticsArtifactKind.ApplicationLog,
                    $$"""
                    password={{SecurityCampaignCanaries.ApplicationManaged}}
                    authorization: Bearer {{SecurityCampaignCanaries.VaultResolved}}
                    """),
                new DiagnosticsBundleArtifact(
                    "logs/browser.log",
                    DiagnosticsArtifactKind.ApplicationLog,
                    $$"""
                    cookie={{SecurityCampaignCanaries.ApplicationManaged}}
                    proxy-authorization: Basic {{SecurityCampaignCanaries.VaultResolved}}
                    """),
            ]);
        await using var archiveStream = new MemoryStream();
        var export = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            diagnosticsRequest,
            archiveStream,
            CancellationToken.None);

        Assert.True(export.IsSuccess, export.Error?.Message);
        Assert.NotNull(export.Value);
        Assert.Equal(2, export.Value.ArtifactCount);
        Assert.Equal(64, export.Value.Sha256.Length);
        AssertCanariesAbsent(SecurityCampaignCanaries.Values, export.Value.ToString());
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            AssertCanariesAbsent(
                SecurityCampaignCanaries.Values,
                await reader.ReadToEndAsync());
        }
    }

    private static AgentSessionCheckpoint CreateCheckpoint(string payload) =>
        new(
            new AgentRunId("campaign-run"),
            AgentSessionCheckpoint.CurrentSchemaVersion,
            generation: 1,
            revision: 1,
            payload,
            CapturedAt);

    private static void AssertCanariesAbsent(
        IEnumerable<string> canaries,
        string? content)
    {
        Assert.NotNull(content);
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, content, StringComparison.Ordinal);
        }
    }

    private static void AssertCanariesAbsent(
        IEnumerable<string> canaries,
        byte[] content)
    {
        foreach (var canary in canaries)
        {
            Assert.Equal(-1, content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)));
        }
    }
}
