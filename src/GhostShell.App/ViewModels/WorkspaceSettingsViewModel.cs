using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record WorkspaceSettingsEditIdentity(
    DefinitionKey Key,
    long? Revision,
    string Name,
    string? Description);

/// <summary>
/// Owns the workspace-definition draft, its catalog projections, and its
/// optimistic save. Overlay routing and live-runtime updates belong to the host.
/// </summary>
public sealed class WorkspaceSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly Func<IReadOnlyList<AiProviderProfileDescriptor>> _aiProviders;
    private readonly Func<DefinitionKey, bool> _isWorkspaceOpen;
    private readonly WorkspaceDefinitionOccupancy? _workspaceDefinitionOccupancy;
    private readonly bool _isIsolationAvailable;
    private readonly string? _isolationRuntimeDisplayName;
    private readonly string _defaultIsolationImageReference;
    private readonly Func<WorkspaceId, string?> _activeIsolationImageReference;
    private WorkspaceEditorViewModel? _editor;
    private bool _disposed;

    public WorkspaceSettingsViewModel(
        IDefinitionCatalog catalog,
        Func<IReadOnlyList<AiProviderProfileDescriptor>>? aiProviders = null,
        Func<DefinitionKey, bool>? isWorkspaceOpen = null,
        WorkspaceDefinitionOccupancy? workspaceDefinitionOccupancy = null,
        bool isIsolationAvailable = true,
        string? isolationRuntimeDisplayName = null,
        string defaultIsolationImageReference = WorkspaceIsolationImages.Default,
        Func<WorkspaceId, string?>? activeIsolationImageReference = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _aiProviders = aiProviders ?? (() => []);
        _workspaceDefinitionOccupancy = workspaceDefinitionOccupancy;
        _isIsolationAvailable = isIsolationAvailable;
        _isolationRuntimeDisplayName = isolationRuntimeDisplayName;
        _defaultIsolationImageReference = string.IsNullOrWhiteSpace(
            defaultIsolationImageReference)
            ? throw new ArgumentException(
                "A default isolation image reference is required.",
                nameof(defaultIsolationImageReference))
            : defaultIsolationImageReference.Trim();
        _activeIsolationImageReference = activeIsolationImageReference ?? (_ => null);
        _isWorkspaceOpen = workspaceDefinitionOccupancy is null
            ? isWorkspaceOpen ?? (_ => false)
            : workspaceDefinitionOccupancy.IsOccupied;
    }

    public WorkspaceEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (ReferenceEquals(_editor, value))
            {
                return;
            }

            var previous = _editor;
            if (SetProperty(ref _editor, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasEditor));
            }
        }
    }

    public bool HasEditor => Editor is not null;

    public bool TryBeginEdit(
        WorkspaceId id,
        out WorkspaceSettingsEditIdentity? identity,
        out string? error)
    {
        ThrowIfDisposed();
        var stored = _catalog.Snapshot.Workspaces.SingleOrDefault(item => item.Value.Id == id);
        if (stored is null)
        {
            identity = null;
            error = "That workspace no longer exists.";
            return false;
        }

        if (!CanReplaceEditor(
            "Save or discard the current workspace changes before editing another workspace.",
            out error))
        {
            identity = null;
            return false;
        }

        Editor = CreateEditor(stored.Value, stored.Revision);
        identity = new(
            stored.Value.Key,
            stored.Revision,
            stored.Value.Name,
            stored.Value.Description);
        return true;
    }

    public bool TryBeginCreate(
        out WorkspaceSettingsEditIdentity? identity,
        out string? error)
    {
        ThrowIfDisposed();
        if (!CanReplaceEditor(
            "Save or discard the current workspace changes before creating another workspace.",
            out error))
        {
            identity = null;
            return false;
        }

        var definition = new WorkspaceDefinition(
            WorkspaceId.New(),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Untitled workspace",
            description: null,
            accent: null,
            []);
        Editor = CreateEditor(definition, expectedRevision: null);
        identity = new(
            definition.Key,
            Revision: null,
            definition.Name,
            Description: null);
        return true;
    }

    public void Dismiss() => Editor = null;

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        Editor?.ApplyNetworkCatalog(
            [.. snapshot.NetworkConnections.Select(item => item.Value)],
            snapshot.ApplicationNetworkSettings
                .SingleOrDefault(item =>
                    item.Value.Id == ApplicationNetworkSettings.DefaultId)?.Value
                ?? ApplicationNetworkSettings.Default);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Editor is null)
        {
            return Fail("Choose a workspace to edit before saving.");
        }

        WorkspaceEditorSaveRequest request;
        try
        {
            request = Editor.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }

        return await SaveAsync(request, cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveAsync(
            WorkspaceEditorSaveRequest request,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (Editor is null
            || Editor.Id != request.Definition.Id
            || Editor.ExpectedRevision != request.ExpectedRevision)
        {
            return Fail("The workspace editor changed before the save could begin.");
        }

        var current = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Id == request.Definition.Id);
        IDisposable? coldConfigurationEdit = null;
        if (current is not null
            && (current.Value.IsIsolated != request.Definition.IsIsolated
                || !current.Value.IsolationMounts.SequenceEqual(
                    request.Definition.IsolationMounts)
                || !string.Equals(
                    current.Value.IsolationImageReference,
                    request.Definition.IsolationImageReference,
                    StringComparison.Ordinal)))
        {
            coldConfigurationEdit = _workspaceDefinitionOccupancy?
                .TryReserveColdConfigurationEdit(current.Value.Key);
            var blocked = _workspaceDefinitionOccupancy is not null
                ? coldConfigurationEdit is null
                : _isWorkspaceOpen(current.Value.Key);
            if (blocked)
            {
                return Fail(
                    "Close this workspace before changing isolation or host mounts. "
                    + "Existing processes cannot be moved safely between execution boundaries.");
            }
        }

        using (coldConfigurationEdit)
        {
            var result = await _catalog.SaveWorkspaceAsync(
                request.Definition,
                request.ExpectedRevision,
                cancellationToken);
            if (result.IsSuccess)
            {
                Dismiss();
            }

            return result;
        }
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> CreateAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var definition = new WorkspaceDefinition(
            WorkspaceId.New(),
            WorkspaceDefinition.CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(name) ? "Workspace" : name.Trim(),
            "A GhostSHELL workspace.",
            accent: null,
            []);
        return _catalog.SaveWorkspaceAsync(definition, null, cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SetIsolationAsync(
            WorkspaceId id,
            bool isIsolated,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var stored = _catalog.Snapshot.Workspaces
            .SingleOrDefault(item => item.Value.Id == id);
        if (stored is null)
        {
            return Fail("That workspace no longer exists.");
        }

        if (isIsolated && !_isIsolationAvailable)
        {
            return Fail("Install a supported workspace isolation runtime before enabling isolation.");
        }

        if (stored.Value.IsIsolated == isIsolated)
        {
            return DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(stored);
        }

        using var coldConfigurationEdit = _workspaceDefinitionOccupancy?
            .TryReserveColdConfigurationEdit(stored.Value.Key);
        var blocked = _workspaceDefinitionOccupancy is not null
            ? coldConfigurationEdit is null
            : _isWorkspaceOpen(stored.Value.Key);
        if (blocked)
        {
            return Fail("Close this workspace before changing isolation or host mounts.");
        }

        var current = stored.Value;
        var updated = new WorkspaceDefinition(
            current.Id,
            current.SchemaVersion,
            current.Name,
            current.Description,
            current.Accent,
            current.Entries,
            current.AgentPolicyOverride,
            current.Icon,
            current.AutoSave,
            current.Color,
            current.AgentPanelPinned,
            current.TerminalMultiplexingOverride,
            current.BrowserProfileOverride,
            current.HasExplicitAccent,
            isIsolated,
            current.IsolationMounts,
            current.IsolationImageReference,
            current.RunAgentInIsolation && isIsolated,
            current.NetworkOverride);
        return await _catalog.SaveWorkspaceAsync(
            updated,
            stored.Revision,
            cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>?>
        SetAgentPanelPinnedAsync(
            DefinitionKey? sourceDefinition,
            bool isPinned,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (sourceDefinition is not { } key || key.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        var stored = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Key == key);
        if (stored is null || stored.Value.AgentPanelPinned == isPinned)
        {
            return null;
        }

        var current = stored.Value;
        var updated = new WorkspaceDefinition(
            current.Id,
            current.SchemaVersion,
            current.Name,
            current.Description,
            current.Accent,
            current.Entries,
            current.AgentPolicyOverride,
            current.Icon,
            current.AutoSave,
            current.Color,
            isPinned,
            current.TerminalMultiplexingOverride,
            current.BrowserProfileOverride,
            current.HasExplicitAccent,
            current.IsIsolated,
            current.IsolationMounts,
            current.IsolationImageReference,
            current.RunAgentInIsolation,
            current.NetworkOverride);
        return await _catalog.SaveWorkspaceAsync(
            updated,
            stored.Revision,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Editor = null;
    }

    private WorkspaceEditorViewModel CreateEditor(
        WorkspaceDefinition definition,
        long? expectedRevision)
    {
        var snapshot = _catalog.Snapshot;
        var editor = new WorkspaceEditorViewModel(
            definition,
            expectedRevision,
            [.. snapshot.Connections.Select(item => item.Value)],
            [.. snapshot.Screens.Select(item => item.Value)],
            [.. snapshot.Layouts.Select(item => item.Value)],
            [.. snapshot.FileProviderProfiles.Select(item => item.Value)],
            _aiProviders(),
            isIsolationAvailable: _isIsolationAvailable,
            isolationRuntimeDisplayName: _isolationRuntimeDisplayName,
            effectiveIsolationImageReference:
                _activeIsolationImageReference(definition.Id)
                ?? definition.IsolationImageReference
                ?? _defaultIsolationImageReference,
            defaultIsolationImageReference: _defaultIsolationImageReference,
            networkConnections:
                [.. snapshot.NetworkConnections.Select(item => item.Value)],
            applicationNetworkSettings: snapshot.ApplicationNetworkSettings
                .SingleOrDefault(item =>
                    item.Value.Id == ApplicationNetworkSettings.DefaultId)?.Value
                ?? ApplicationNetworkSettings.Default);
        editor.SetPeers([.. snapshot.Workspaces.Select(item => item.Value)]);
        return editor;
    }

    private bool CanReplaceEditor(string message, out string? error)
    {
        if (Editor?.RequestCancel()
            != WorkspaceEditorCancelDisposition.ConfirmDiscard)
        {
            error = null;
            return true;
        }

        error = message;
        return false;
    }

    private static DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>> Fail(
        string message) =>
        DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Failure(new(
            DefinitionStoreErrorCode.InvalidDefinition,
            message));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
