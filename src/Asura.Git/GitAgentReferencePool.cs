using Asura.Application;

namespace Asura.Git;

internal sealed class GitAgentReferencePool(TimeProvider timeProvider)
{
    private const int MaximumStateLeases = 8;
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RemoteStateLifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<GitStateReferenceId, StateLease> _states = [];
    private readonly Dictionary<GitRemoteStateReferenceId, RemoteStateLease> _remoteStates = [];
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public GitStateReferenceId AddState(
        GitRepositoryGuard guard,
        IReadOnlyList<GitFileChange> changes,
        IReadOnlyList<GitRefItem> branches,
        IReadOnlyList<GitRemoteItem> remotes)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var stateId = new GitStateReferenceId(Guid.NewGuid().ToString("N"));
        var changeMap = changes.ToDictionary(
            _ => new GitChangeReferenceId(Guid.NewGuid().ToString("N")),
            static change => change);
        var branchMap = branches.ToDictionary(
            _ => new GitBranchReferenceId(Guid.NewGuid().ToString("N")),
            static branch => branch);
        var remoteMap = remotes.ToDictionary(
            _ => new GitRemoteReferenceId(Guid.NewGuid().ToString("N")),
            static remote => remote);
        lock (_gate)
        {
            ExpireUnsafe();
            while (_states.Count >= MaximumStateLeases)
            {
                var oldest = _states.MinBy(static pair => pair.Value.ExpiresAtUtc).Key;
                _states.Remove(oldest);
            }

            _states.Add(
                stateId,
                new StateLease(
                    guard,
                    changeMap,
                    branchMap,
                    remoteMap,
                    _timeProvider.GetUtcNow() + StateLifetime));
        }

        return stateId;
    }

    public bool TryGetState(GitStateReferenceId reference, out StateLease lease)
    {
        lock (_gate)
        {
            ExpireUnsafe();
            return _states.TryGetValue(reference, out lease!);
        }
    }

    public GitRemoteStateReferenceId AddRemoteState(
        GitStateReferenceId state,
        GitRemoteReferenceId remote,
        GitBranchReferenceId branch,
        GitGovernedRemoteRef observed)
    {
        var reference = new GitRemoteStateReferenceId(Guid.NewGuid().ToString("N"));
        lock (_gate)
        {
            ExpireUnsafe();
            if (!_states.ContainsKey(state))
            {
                throw new InvalidOperationException("The Git state reference expired.");
            }

            _remoteStates.Add(
                reference,
                new RemoteStateLease(
                    state,
                    remote,
                    branch,
                    observed,
                    _timeProvider.GetUtcNow() + RemoteStateLifetime));
        }

        return reference;
    }

    public bool TryGetRemoteState(
        GitRemoteStateReferenceId reference,
        out RemoteStateLease lease)
    {
        lock (_gate)
        {
            ExpireUnsafe();
            return _remoteStates.TryGetValue(reference, out lease!);
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            _states.Clear();
            _remoteStates.Clear();
        }
    }

    private void ExpireUnsafe()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var reference in _states
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _states.Remove(reference);
        }

        foreach (var reference in _remoteStates
                     .Where(pair => pair.Value.ExpiresAtUtc <= now
                         || !_states.ContainsKey(pair.Value.State))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _remoteStates.Remove(reference);
        }
    }

    internal sealed record StateLease(
        GitRepositoryGuard Guard,
        IReadOnlyDictionary<GitChangeReferenceId, GitFileChange> Changes,
        IReadOnlyDictionary<GitBranchReferenceId, GitRefItem> Branches,
        IReadOnlyDictionary<GitRemoteReferenceId, GitRemoteItem> Remotes,
        DateTimeOffset ExpiresAtUtc);

    internal sealed record RemoteStateLease(
        GitStateReferenceId State,
        GitRemoteReferenceId Remote,
        GitBranchReferenceId Branch,
        GitGovernedRemoteRef Observed,
        DateTimeOffset ExpiresAtUtc);
}
