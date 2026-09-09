using Asura.Application;
using Asura.Core;
using Asura.Git;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns terminal-connection definition authoring and optimistic persistence.
/// Opening connections and projecting them into the running shell remain root concerns.
/// </summary>
public sealed class TerminalConnectionSettingsViewModel : IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly IConnectionSecurityRuntime? _connectionSecurityRuntime;
    private readonly IGitRepositoryClient? _gitRepositoryClient;
    private bool _disposed;

    public TerminalConnectionSettingsViewModel(
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        IConnectionSecurityRuntime? connectionSecurityRuntime = null,
        IGitRepositoryClient? gitRepositoryClient = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _connectionSecurityRuntime = connectionSecurityRuntime;
        _gitRepositoryClient = gitRepositoryClient;
    }

    public ConnectionEditorViewModel CreateEditor(ConnectionId? connectionId = null)
    {
        ThrowIfDisposed();
        var savedConnections = _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .ToArray();
        if (connectionId is null)
        {
            return new ConnectionEditorViewModel(
                _connectionRuntime,
                securityRuntime: _connectionSecurityRuntime,
                gitClient: _gitRepositoryClient,
                savedConnections: savedConnections);
        }

        var stored = _catalog.Snapshot.Connections
            .SingleOrDefault(item => item.Value.Id == connectionId.Value)
            ?? throw new InvalidOperationException("That connection no longer exists.");
        return new ConnectionEditorViewModel(
            _connectionRuntime,
            stored.Value,
            stored.Revision,
            _connectionSecurityRuntime,
            _gitRepositoryClient,
            savedConnections);
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveAsync(
        ConnectionEditorSaveRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        return _catalog.SaveConnectionAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
