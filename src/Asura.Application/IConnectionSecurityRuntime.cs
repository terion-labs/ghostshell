using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Keeps host-key bytes and resolved authentication material behind Infrastructure while exposing
/// non-secret review and diagnostics projections to desktop and future headless clients.
/// </summary>
public interface IConnectionSecurityRuntime
{
    /// <summary>
    /// Resolves the host-key trust needed to launch a connection. An existing pin can be enforced
    /// by the connection runtime without opening a second network session.
    /// </summary>
    ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> PrepareSshHostKeyAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
        SshHostKeyTrustRequest request,
        CancellationToken cancellationToken);

    ValueTask<ConnectionRuntimeResult<ConnectionDiagnosticsReport>> DiagnoseAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);
}
