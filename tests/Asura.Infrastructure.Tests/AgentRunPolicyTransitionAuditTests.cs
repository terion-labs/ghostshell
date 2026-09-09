using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class AgentRunPolicyTransitionAuditTests
{
    [Theory]
    [InlineData(AgentRunPolicyTransition.Updated, false)]
    [InlineData(AgentRunPolicyTransition.YoloEnabled, true)]
    [InlineData(AgentRunPolicyTransition.YoloDisabled, false)]
    [InlineData(AgentRunPolicyTransition.YoloExpired, true)]
    public void CurrentCodecRoundTripsEveryClosedTransitionShape(
        AgentRunPolicyTransition transition,
        bool includesYoloExpiry)
    {
        const string rawTarget = "ssh://root@production.example:22";
        var targetIdentityDigest = AgentActionDigest.FromUtf8(rawTarget);
        var yoloExpiresAtUtc = includesYoloExpiry
            ? new DateTimeOffset(2026, 7, 24, 12, 30, 0, TimeSpan.Zero)
            : (DateTimeOffset?)null;
        var expected = AuditDetails.ForAgentRunPolicyTransition(
            new AgentRunId("run-1"),
            transition,
            7,
            targetIdentityDigest,
            yoloExpiresAtUtc);

        var encoded = AuditDetailsJson.Serialize(expected);
        var decoded = AuditDetailsJson.TryDeserialize(encoded, out var details);

        Assert.True(decoded);
        Assert.Equal(expected, details);
        Assert.DoesNotContain(rawTarget, encoded, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(
            [
                "schemaVersion",
                "kind",
                "runId",
                "transition",
                "policyGeneration",
                "targetIdentityDigest",
                "yoloExpiresAtUtc",
                "capabilityRequestId",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void FactoryRejectsUnboundedOrNonCanonicalTransitionEvidence()
    {
        var digest = AgentActionDigest.FromUtf8("target");

        Assert.Throws<ArgumentException>(() =>
            AuditDetails.ForAgentRunPolicyTransition(
                new AgentRunId(new string('r', 257)),
                AgentRunPolicyTransition.Updated,
                1,
                digest));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuditDetails.ForAgentRunPolicyTransition(
                new AgentRunId("run-1"),
                (AgentRunPolicyTransition)int.MaxValue,
                1,
                digest));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuditDetails.ForAgentRunPolicyTransition(
                new AgentRunId("run-1"),
                AgentRunPolicyTransition.Updated,
                -1,
                digest));
        Assert.Throws<ArgumentException>(() =>
            AuditDetails.ForAgentRunPolicyTransition(
                new AgentRunId("run-1"),
                AgentRunPolicyTransition.Updated,
                1,
                default));
        Assert.Throws<ArgumentException>(() =>
            AuditDetails.ForAgentRunPolicyTransition(
                new AgentRunId("run-1"),
                AgentRunPolicyTransition.YoloEnabled,
                1,
                digest,
                new DateTimeOffset(2026, 7, 24, 12, 30, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void CodecRejectsMalformedOrOpenEndedPolicyTransitionShapes()
    {
        var digest = AgentActionDigest.FromUtf8("target").Value;
        var valid = $$"""
            {"schemaVersion":2,"kind":"agent-run-policy-transition","runId":"run-1","transition":"Updated","policyGeneration":7,"targetIdentityDigest":"{{digest}}","yoloExpiresAtUtc":null}
            """;

        AssertRejected(valid.Replace(
            "\"schemaVersion\":2",
            "\"schemaVersion\":1",
            StringComparison.Ordinal));
        AssertRejected(valid.Replace(
            "\"transition\":\"Updated\"",
            "\"transition\":\"Unknown\"",
            StringComparison.Ordinal));
        AssertRejected(valid.Replace(
            "\"policyGeneration\":7",
            "\"policyGeneration\":-1",
            StringComparison.Ordinal));
        AssertRejected(valid.Replace(
            digest,
            new string('A', AgentActionDigest.EncodedLength),
            StringComparison.Ordinal));
        AssertRejected(valid.Replace(
            "\"run-1\"",
            $"\"{new string('r', 257)}\"",
            StringComparison.Ordinal));
        AssertRejected(valid.Replace(
            "\"yoloExpiresAtUtc\":null",
            "\"yoloExpiresAtUtc\":\"2026-07-24T12:30:00.0000000+02:00\"",
            StringComparison.Ordinal));
        AssertRejected(valid.Replace(
            "}",
            ",\"command\":\"secret-canary\"}",
            StringComparison.Ordinal));
    }

    [Fact]
    public void PreviousAgentActionSchemaRemainsReadable()
    {
        var digest = AgentActionDigest.FromUtf8("legacy-arguments");
        var encoded = $$"""
            {"schemaVersion":1,"kind":"agent-action","runId":"run-1","capability":"TerminalRead","risk":"Observation","permission":"Auto","decision":"AuthorizedByAuto","argumentDigest":"{{digest.Value}}","authorizationSource":"AutoPolicy","errorCode":null,"resultCode":null}
            """;

        var decoded = AuditDetailsJson.TryDeserialize(encoded, out var details);

        Assert.True(decoded);
        var action = Assert.IsType<AuditDetails.AgentActionDetails>(details);
        Assert.Equal(AgentActionAuditBinding.Empty, action.Binding);
    }

    [Fact]
    public void PreviousPolicyTransitionSchemaRemainsReadable()
    {
        var digest = AgentActionDigest.FromUtf8("legacy-target");
        var encoded = $$"""
            {"schemaVersion":2,"kind":"agent-run-policy-transition","runId":"run-1","transition":"Updated","policyGeneration":7,"targetIdentityDigest":"{{digest.Value}}","yoloExpiresAtUtc":null}
            """;

        Assert.True(AuditDetailsJson.TryDeserialize(encoded, out var details));
        Assert.Null(
            Assert.IsType<AuditDetails.AgentRunPolicyTransitionDetails>(details)
                .CapabilityRequestId);
    }

    [Fact]
    public void CapabilityRequestCodecIsClosedAndSecretFree()
    {
        const string rawTarget = "ssh://root:secret-canary@production.example";
        var expected = AuditDetails.ForAgentCapabilityRequest(
            new AgentRunId("run-1"),
            AgentCapability.RunCommands,
            policyGeneration: 4,
            AgentActionDigest.FromUtf8(rawTarget),
            AgentCapabilityRequestAuditDecision.TargetChanged);

        var encoded = AuditDetailsJson.Serialize(expected);

        Assert.True(AuditDetailsJson.TryDeserialize(encoded, out var decoded));
        Assert.Equal(expected, decoded);
        Assert.DoesNotContain(rawTarget, encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", encoded, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(
            [
                "schemaVersion",
                "kind",
                "runId",
                "capability",
                "policyGeneration",
                "targetIdentityDigest",
                "decision",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task PolicyTransitionDetailsRoundTripThroughSqliteWithoutRawTarget()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAuditStore(temporary.Database);
        const string rawTarget = "ssh://root@production.example:22";
        var targetIdentityDigest = AgentActionDigest.FromUtf8(rawTarget);
        var details = AuditDetails.ForAgentRunPolicyTransition(
            new AgentRunId("run-1"),
            AgentRunPolicyTransition.YoloEnabled,
            8,
            targetIdentityDigest,
            new DateTimeOffset(2026, 7, 24, 12, 30, 0, TimeSpan.Zero));
        var auditEvent = new AuditEventRecord(
            "event-policy-transition",
            "run-1",
            new ActorDescriptor(
                new ActorId("human-1"),
                ActorKind.Human,
                "Local user",
                new ClientId("human-1")),
            "agent.run.policy.transition",
            new AuditTarget("agent-target-fingerprint", targetIdentityDigest.Value),
            AuditOutcome.Succeeded,
            details,
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

        var append = await store.AppendAsync(auditEvent, CancellationToken.None);
        var read = await store.ListByCorrelationAsync(
            auditEvent.CorrelationId,
            CancellationToken.None);

        Assert.True(append.IsSuccess, append.Error?.Message);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(details, Assert.Single(read.Value!).Details);
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT details_json
            FROM audit_events
            WHERE event_id = $eventId;
            """;
        command.Parameters.AddWithValue("$eventId", auditEvent.EventId);
        var encoded = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain(rawTarget, encoded, StringComparison.Ordinal);
        Assert.Contains(targetIdentityDigest.Value, encoded, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"YoloEnabled\"", encoded, StringComparison.Ordinal);
    }

    private static void AssertRejected(string encoded)
    {
        Assert.False(AuditDetailsJson.TryDeserialize(encoded, out var details));
        Assert.Null(details);
    }
}
