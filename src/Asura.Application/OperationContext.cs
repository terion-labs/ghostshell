using Asura.Core;

namespace Asura.Application;

public sealed record OperationContext(
    RequestId RequestId,
    ActorDescriptor Actor,
    long? ExpectedRevision = null,
    IdempotencyKey? IdempotencyKey = null,
    CancellationId? CancellationId = null,
    DateTimeOffset? DeadlineUtc = null)
{
    public static OperationContext ForHuman(
        ClientId clientId,
        long? expectedRevision = null,
        IdempotencyKey? idempotencyKey = null,
        DateTimeOffset? deadlineUtc = null) =>
        new(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId(clientId.Value),
                ActorKind.Human,
                "Local user",
                clientId),
            expectedRevision,
            idempotencyKey,
            Asura.Core.CancellationId.New(),
            deadlineUtc);
}
