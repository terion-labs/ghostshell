using Asura.Core;

namespace Asura.Application;

public sealed record SshHostKeyTrustRequest
{
    public SshHostKeyTrustRequest(
        SshHostKeyReviewId reviewId,
        ConnectionId connectionId,
        SshHostKeyTrustAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId.Value);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        ReviewId = reviewId;
        ConnectionId = connectionId;
        Action = action;
    }

    public SshHostKeyReviewId ReviewId { get; }

    public ConnectionId ConnectionId { get; }

    public SshHostKeyTrustAction Action { get; }
}
