using Asura.Core;

namespace Asura.Protocol;

public sealed record ProtocolRequestControl(
    CancellationId? CancellationId,
    DateTimeOffset? DeadlineUtc);
