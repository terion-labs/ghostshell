using Asura.Core;

namespace Asura.Application;

public sealed record SessionCloseResult(
    SessionId SessionId,
    SessionCloseOutcome Outcome,
    string Detail);
