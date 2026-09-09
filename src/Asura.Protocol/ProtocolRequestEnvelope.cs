using Asura.Core;

namespace Asura.Protocol;

public sealed record ProtocolRequestEnvelope<TPayload>(
    int ProtocolVersion,
    RequestId RequestId,
    string Operation,
    ProtocolActor Actor,
    ProtocolTargets Targets,
    long? ExpectedRevision,
    IdempotencyKey? IdempotencyKey,
    ProtocolRequestControl Control,
    IReadOnlyList<string> Capabilities,
    TPayload Payload);
