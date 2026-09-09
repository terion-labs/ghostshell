using Asura.Core;

namespace Asura.Application;

public sealed record EnsureDatabaseSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    DatabaseSessionTarget Target);
