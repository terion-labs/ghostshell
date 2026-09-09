using Asura.Core;

namespace Asura.Application;

public sealed record ProcessMonitorHostRequest(
    SessionId SessionId,
    ProcessMonitorQuery Query);
