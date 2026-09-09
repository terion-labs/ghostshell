using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal interface ISshHostKeyScanner
{
    ValueTask<ConnectionRuntimeResult<SshHostKeyCandidate>> ScanAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}
