using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal interface ISshAuthenticationProbe
{
    ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> AuthenticateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}
