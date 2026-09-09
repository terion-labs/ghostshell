using Asura.Application;

namespace Asura.SessionHost;

public interface ISessionLifecyclePolicy
{
    HostMode HostMode { get; }

    bool ClientDisconnectClosesSessions { get; }
}
