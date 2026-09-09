using Asura.Application;

namespace Asura.SessionHost;

public sealed class ServerLifecyclePolicy : ISessionLifecyclePolicy
{
    public HostMode HostMode => Asura.Application.HostMode.Server;

    public bool ClientDisconnectClosesSessions => false;
}
