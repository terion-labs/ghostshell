using Asura.Core;

namespace Asura.Protocol;

public sealed record ProtocolResponseEnvelope<TResult>(
    int ProtocolVersion,
    RequestId RequestId,
    long ResultingRevision,
    bool Succeeded,
    TResult? Result,
    ProtocolError? Error)
{
    public void Validate()
    {
        if (Succeeded == (Error is not null))
        {
            throw new InvalidOperationException(
                "A successful protocol response cannot contain an error, and a failed response must contain one.");
        }
    }
}
