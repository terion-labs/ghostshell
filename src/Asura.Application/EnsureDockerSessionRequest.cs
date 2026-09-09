using Asura.Core;

namespace Asura.Application;

public sealed record EnsureDockerSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    DockerSessionTarget Target);
