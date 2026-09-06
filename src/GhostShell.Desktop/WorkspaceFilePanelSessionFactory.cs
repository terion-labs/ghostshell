using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Files;

namespace GhostShell.Desktop;

/// <summary>
/// Selects the file-panel implementation for a workspace while the session host
/// remains the sole owner of sessions and workspace graph links.
/// </summary>
internal sealed class WorkspaceFilePanelSessionFactory(
    FilePanelSessionFactory hostFactory) : IFilePanelSessionFactory
{
    private readonly WorkspaceSessionFactoryRegistry<IFilePanelSessionFactory> _factories = new(
        hostFactory,
        "The workspace already has a file-panel factory.");

    public CapabilitySet Capabilities => hostFactory.Capabilities;

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        IFilePanelSessionFactory factory)
        => _factories.Register(workspaceId, factory);

    public ValueTask<IFilePanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        FilePanelLocation initialLocation,
        CancellationToken cancellationToken) =>
        _factories.Resolve(workspaceId).CreateAsync(
            workspaceId,
            sessionId,
            initialLocation,
            cancellationToken);
}
