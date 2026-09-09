using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

/// <summary>
/// One process-local desktop identity shared by the window/session client and
/// governed approval runtime. Model requests cannot choose or replace it.
/// </summary>
internal sealed class DesktopAgentApprovalPrincipal : IAgentApprovalPrincipal
{
    public DesktopAgentApprovalPrincipal()
    {
        var clientId = ClientId.New();
        Actor = new ActorDescriptor(
            new ActorId(clientId.Value),
            ActorKind.Human,
            "Local user",
            clientId);
    }

    public ActorDescriptor Actor { get; }
}
