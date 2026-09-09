using Asura.Core;

namespace Asura.Application;

public sealed record ActiveSessionSummary(
    SessionId SessionId,
    PanelInstanceId PanelId,
    string Title,
    string Detail,
    long Revision);
