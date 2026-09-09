using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Applies one durable SSH host-key policy decision synchronously from a vendor handshake
/// callback. Accept-new must be an atomic create and must never replace a different key.
/// </summary>
public interface ISshHostKeyTrustStore
{
    SshHostKeyVerification Verify(
        ConnectionId connectionId,
        SshHostKeyPolicy policy,
        SshHostKeyCandidate presented);
}

public enum SshHostKeyVerification
{
    Trusted,
    Unknown,
    Changed,
    StoreInvalid,
}
