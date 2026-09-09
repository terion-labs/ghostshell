using System.Collections.Concurrent;
using Asura.Application;

namespace Asura.Git;

public sealed class GitRepositoryMutationCoordinator : IGitRepositoryMutationCoordinator
{
    private readonly ConcurrentDictionary<GitRepositoryIdentity, SemaphoreSlim> _gates = [];

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        GitRepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(repository, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MutationLease(gate);
    }

    private sealed class MutationLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
