using Asura.Core;

namespace Asura.Application;

/// <summary>
/// A bounded review snapshot. The opaque review ID lets Infrastructure retain the public key bytes
/// without carrying them through view models or transport messages.
/// </summary>
public sealed record SshHostKeyReview
{
    public SshHostKeyReview(
        SshHostKeyReviewId id,
        ConnectionId connectionId,
        string endpoint,
        SshHostKeyDisposition disposition,
        SshHostKeyIdentity presented,
        SshHostKeyIdentity? trusted,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(presented);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
        }

        var hasTrustedIdentity = trusted is not null;
        var validTrustedShape = disposition switch
        {
            SshHostKeyDisposition.Trusted or SshHostKeyDisposition.Changed => hasTrustedIdentity,
            SshHostKeyDisposition.Unknown or SshHostKeyDisposition.VerificationDisabled => !hasTrustedIdentity,
            _ => false,
        };
        if (!validTrustedShape)
        {
            throw new ArgumentException("The trusted host-key identity does not match the review disposition.");
        }

        if (expiresAtUtc <= DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        }

        Id = id;
        ConnectionId = connectionId;
        Endpoint = endpoint.Trim();
        Disposition = disposition;
        Presented = presented;
        Trusted = trusted;
        ExpiresAtUtc = expiresAtUtc;
    }

    public SshHostKeyReviewId Id { get; }

    public ConnectionId ConnectionId { get; }

    public string Endpoint { get; }

    public SshHostKeyDisposition Disposition { get; }

    public SshHostKeyIdentity Presented { get; }

    public SshHostKeyIdentity? Trusted { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool RequiresExplicitReplacement => Disposition == SshHostKeyDisposition.Changed;
}
