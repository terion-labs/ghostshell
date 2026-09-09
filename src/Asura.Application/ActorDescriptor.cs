using Asura.Core;

namespace Asura.Application;

public sealed record ActorDescriptor(
    ActorId Id,
    ActorKind Kind,
    string DisplayName,
    ClientId? ClientId = null);
