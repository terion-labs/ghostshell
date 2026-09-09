using Asura.Application;

namespace Asura.SessionHost;

public sealed class DesktopLifecyclePolicy : ISessionLifecyclePolicy
{
    public HostMode HostMode => Asura.Application.HostMode.Desktop;

    public bool ClientDisconnectClosesSessions => false;
}
