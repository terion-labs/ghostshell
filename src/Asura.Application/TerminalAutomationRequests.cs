using Asura.Core;

namespace Asura.Application;

public sealed record TerminalEnterRequest(
    SessionId SessionId,
    InputLeaseId LeaseId);

public sealed record TerminalInterruptRequest(
    SessionId SessionId,
    InputLeaseId LeaseId);

public sealed record TerminalWaitForDelayRequest(
    SessionId SessionId,
    TerminalWaitForDelayInput Wait);

public sealed record TerminalWaitForTextRequest(
    SessionId SessionId,
    TerminalWaitForTextInput Wait);

public sealed record TerminalWaitForChangeRequest(
    SessionId SessionId,
    TerminalWaitForChangeInput Wait);

public sealed record TerminalWaitForStableRequest(
    SessionId SessionId,
    TerminalWaitForStableInput Wait);

public sealed record TerminalWaitForPromptReadyRequest(
    SessionId SessionId,
    TerminalWaitForPromptReadyInput Wait);

public sealed record TerminalWaitForCommandFinishedRequest(
    SessionId SessionId,
    TerminalWaitForCommandFinishedInput Wait);
