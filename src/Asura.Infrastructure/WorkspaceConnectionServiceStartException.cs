namespace Asura.Infrastructure;

/// <summary>
/// Startup failed after acquiring resources, and cleanup also failed. The caller
/// must retain Cleanup and retry its disposal; retrying a database operation does
/// not release this old VM lease.
/// </summary>
public sealed class WorkspaceConnectionServiceStartException : IOException
{
    internal WorkspaceConnectionServiceStartException(Exception startupFailure, Exception cleanupFailure, IAsyncDisposable cleanup)
        : base("The connection service failed to start and still has owned resources to release.",
            new AggregateException(startupFailure, cleanupFailure)) => Cleanup = cleanup;

    public IAsyncDisposable Cleanup { get; }
}
