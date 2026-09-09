using Asura.Core;

namespace Asura.Application;

public sealed record EnsureTerminalSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    TerminalLaunchRequest Launch,
    PanelSessionRole Role = PanelSessionRole.Primary);
