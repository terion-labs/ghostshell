using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Git;

public sealed class GitPanelSessionFactory(
    IGitRepositoryClient client,
    IGitRepositoryMutationCoordinator coordinator,
    TimeProvider timeProvider) : IGitPanelSessionFactory
{
    private readonly IGitRepositoryClient _client =
        client ?? throw new ArgumentNullException(nameof(client));
    private readonly IGitRepositoryMutationCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.GitReadState,
        SessionCapabilities.GitReadDiff,
        SessionCapabilities.GitReadRemoteRef,
        SessionCapabilities.GitStage,
        SessionCapabilities.GitUnstage,
        SessionCapabilities.GitBranchCreate,
        SessionCapabilities.GitBranchCheckout,
        SessionCapabilities.GitCommit,
    ]);

    public ValueTask<IGitPanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        GitSessionTarget target,
        CancellationToken cancellationToken) =>
        CreateAsync(sessionId, target, cancellationToken);

    public async ValueTask<IGitPanelSession> CreateAsync(
        SessionId sessionId,
        GitSessionTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!OperatingSystem.IsMacOS()
            || target.Repository.Connection.Endpoint is not ConnectionEndpoint.Local)
        {
            throw new PlatformNotSupportedException(
                "Governed Git sessions currently require a local macOS repository.");
        }

        var initial = await _client
            .ReadGovernedStateAsync(target.Repository, generation: 1, cancellationToken)
            .ConfigureAwait(false);
        if (initial is not GitResult<GitGovernedState>.Success success)
        {
            throw new InvalidOperationException("The governed Git repository could not be opened.");
        }

        return new GitPanelSession(
            sessionId,
            target,
            _client,
            _coordinator,
            success.Value,
            Capabilities,
            _timeProvider);
    }
}
