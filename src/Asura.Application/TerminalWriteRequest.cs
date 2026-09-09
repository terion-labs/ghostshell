using Asura.Core;

namespace Asura.Application;

public sealed record TerminalWriteRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    string Text);
