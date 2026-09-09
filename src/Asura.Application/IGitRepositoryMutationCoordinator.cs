using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Serializes every UI and governed mutation of the same canonical repository.
/// It does not replace Git state guards, because external processes remain able
/// to change a repository while the lease is held.
/// </summary>
public interface IGitRepositoryMutationCoordinator
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        GitRepositoryIdentity repository,
        CancellationToken cancellationToken);
}
