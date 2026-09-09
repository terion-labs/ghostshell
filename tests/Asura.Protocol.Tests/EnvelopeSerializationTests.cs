using System.Text.Json;
using System.Text.Json.Nodes;
using Asura.Core;
using Asura.Protocol;

namespace Asura.Protocol.Tests;

public sealed class EnvelopeSerializationTests
{
    [Fact]
    public void RequestRoundTripPreservesRequiredControlAndIdentityFields()
    {
        var request = CreateRequest("session.detach");
        var json = JsonSerializer.Serialize(
            request,
            typeof(ProtocolRequestEnvelope<ProtocolPing>),
            AsuraProtocolJsonContext.Default);
        var roundTrip = Assert.IsType<ProtocolRequestEnvelope<ProtocolPing>>(
            JsonSerializer.Deserialize(
                json,
                typeof(ProtocolRequestEnvelope<ProtocolPing>),
                AsuraProtocolJsonContext.Default));

        Assert.Equal(ProtocolVersions.Current, roundTrip.ProtocolVersion);
        Assert.Equal(request.RequestId, roundTrip.RequestId);
        Assert.Equal("session.detach", roundTrip.Operation);
        Assert.Equal(request.Actor, roundTrip.Actor);
        Assert.Equal(request.Targets, roundTrip.Targets);
        Assert.Equal(request.ExpectedRevision, roundTrip.ExpectedRevision);
        Assert.Equal(request.IdempotencyKey, roundTrip.IdempotencyKey);
        Assert.Equal(request.Control, roundTrip.Control);
        Assert.Equal("hello", roundTrip.Payload.Value);
    }

    [Fact]
    public void UnknownAdditiveFieldIsIgnored()
    {
        var request = CreateRequest("session.close");
        var json = JsonSerializer.Serialize(
            request,
            typeof(ProtocolRequestEnvelope<ProtocolPing>),
            AsuraProtocolJsonContext.Default);
        var node = JsonNode.Parse(json)!.AsObject();
        node["futureField"] = new JsonObject { ["enabled"] = true };

        var roundTrip = Assert.IsType<ProtocolRequestEnvelope<ProtocolPing>>(
            JsonSerializer.Deserialize(
                node.ToJsonString(),
                typeof(ProtocolRequestEnvelope<ProtocolPing>),
                AsuraProtocolJsonContext.Default));
        Assert.Equal("session.close", roundTrip.Operation);
    }

    [Fact]
    public void DetachAndCloseRemainDistinctOperationsWithSameStableTargets()
    {
        var detach = CreateRequest("session.detach");
        var close = CreateRequest("session.close");

        Assert.NotEqual(detach.Operation, close.Operation, StringComparer.Ordinal);
        Assert.Equal(detach.Targets, close.Targets);
        Assert.Equal(detach.Actor, close.Actor);
    }

    [Fact]
    public void ResponseInvariantRejectsAmbiguousSuccessAndFailure()
    {
        var invalidSuccess = new ProtocolResponseEnvelope<ProtocolPong>(
            ProtocolVersions.Current,
            new RequestId("request-1"),
            4,
            true,
            new ProtocolPong("pong"),
            new ProtocolError("engine_failed", "bad"));
        Assert.Throws<InvalidOperationException>(invalidSuccess.Validate);

        var invalidFailure = invalidSuccess with
        {
            Succeeded = false,
            Result = null,
            Error = null,
        };
        Assert.Throws<InvalidOperationException>(invalidFailure.Validate);
    }

    [Fact]
    public void EventEnvelopeClonesPayloadOutsideDocumentLifetime()
    {
        ProtocolSessionEventEnvelope cloned;
        using (var document = JsonDocument.Parse("{\"state\":\"active\"}"))
        {
            cloned = new ProtocolSessionEventEnvelope(
                new SessionId("session-1"),
                7,
                5,
                "state.changed",
                1,
                DateTimeOffset.UnixEpoch,
                document.RootElement).ClonePayload();
        }

        Assert.Equal("active", cloned.Payload.GetProperty("state").GetString());
    }

    private static ProtocolRequestEnvelope<ProtocolPing> CreateRequest(string operation) =>
        new(
            ProtocolVersions.Current,
            new RequestId("request-1"),
            operation,
            new ProtocolActor(
                new ActorId("actor-1"),
                "human",
                "Test user",
                new ClientId("client-1")),
            new ProtocolTargets(
                WindowId: "window-1",
                WorkspaceId: "workspace-1",
                TabId: "tab-1",
                PanelId: "panel-1",
                SessionId: "session-1",
                AttachmentId: "attachment-1"),
            3,
            new IdempotencyKey("idempotency-1"),
            new ProtocolRequestControl(
                new CancellationId("cancel-1"),
                DateTimeOffset.UnixEpoch.AddMinutes(1)),
            ["session.attach.read"],
            new ProtocolPing("hello"));
}
