using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Resolves an SFTP provider to its referenced SSH connection and keeps host-key review and trust
/// on the same bounded connection-security workflow used by terminal sessions.
/// </summary>
public interface IFileProviderHostKeyRepair
{
    ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
        FileProviderProfile profile,
        CancellationToken cancellationToken);

    ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
        FileProviderProfile profile,
        SshHostKeyReviewId reviewId,
        SshHostKeyTrustAction action,
        CancellationToken cancellationToken);
}
