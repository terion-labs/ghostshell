using System.Text.Json;
using Asura.Core;

namespace Asura.Protocol;

public sealed record ProtocolSessionEventEnvelope(
    SessionId SessionId,
    long Sequence,
    long Revision,
    string EventKind,
    int PayloadVersion,
    DateTimeOffset TimestampUtc,
    JsonElement Payload)
{
    public ProtocolSessionEventEnvelope ClonePayload() => this with { Payload = Payload.Clone() };
}
