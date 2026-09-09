using Asura.Core;

namespace Asura.Protocol;

public sealed record ProtocolActor(
    ActorId Id,
    string Kind,
    string DisplayName,
    ClientId? ClientId);
