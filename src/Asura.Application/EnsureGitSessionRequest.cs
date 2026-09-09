using Asura.Core;

namespace Asura.Application;

public sealed record EnsureGitSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    GitSessionTarget Target);
