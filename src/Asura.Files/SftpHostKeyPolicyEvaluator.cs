using Asura.Application;
using Asura.Core;

namespace Asura.Files;

internal static class SftpHostKeyPolicyEvaluator
{
    public static SftpHostKeyDecision Evaluate(
        ConnectionProfile connection,
        ISshHostKeyTrustStore knownHosts,
        SshHostKeyCandidate presented)
    {
        var verification = knownHosts.Verify(
            connection.Id,
            connection.HostKeyPolicy,
            presented);
        return verification switch
        {
            SshHostKeyVerification.Trusted => new SftpHostKeyDecision(true, null),
            SshHostKeyVerification.Unknown => new SftpHostKeyDecision(
                Trusted: false,
                RemoteFileSessionErrorCode.HostKeyUnknown),
            SshHostKeyVerification.Changed => new SftpHostKeyDecision(
                Trusted: false,
                RemoteFileSessionErrorCode.HostKeyChanged),
            SshHostKeyVerification.StoreInvalid => new SftpHostKeyDecision(
                Trusted: false,
                RemoteFileSessionErrorCode.HostKeyStoreInvalid),
            _ => throw new ArgumentOutOfRangeException(nameof(verification), verification, null),
        };
    }
}

internal sealed record SftpHostKeyDecision(
    bool Trusted,
    RemoteFileSessionErrorCode? Failure);
