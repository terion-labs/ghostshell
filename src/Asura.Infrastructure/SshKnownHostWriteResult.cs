namespace Asura.Infrastructure;

internal enum SshKnownHostWriteResult
{
    Stored,
    AlreadyCurrent,
    ChangedSinceReview,
}
