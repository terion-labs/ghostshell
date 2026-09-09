using Asura.Core;

namespace Asura.Protocol;

public sealed record ProtocolWatchRequest(
    SessionId SessionId,
    long AfterSequence,
    int MaximumBatchSize);
