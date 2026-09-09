namespace Asura.Files;

/// <summary>
/// Only metadata operations may be retried. Retrying reads could duplicate caller output and
/// retrying mutations could repeat a commit whose reply was lost.
/// </summary>
public enum RemoteMetadataReconnectPolicy
{
    None,
    RetryOnce,
}
