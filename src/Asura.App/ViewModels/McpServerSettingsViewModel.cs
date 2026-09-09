using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns MCP profile editor construction and revision-aware persistence. Runtime
/// diagnostics, credential invalidation, and secret mutation stay outside this
/// settings boundary.
/// </summary>
public sealed class McpServerSettingsViewModel : IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly Func<IReadOnlyList<SecretMetadataViewModel>> _secrets;
    private bool _disposed;

    public McpServerSettingsViewModel(
        IDefinitionCatalog catalog,
        Func<IReadOnlyList<SecretMetadataViewModel>> secrets)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public McpServerProfileEditorViewModel CreateEditor(McpServerProfileId? profileId = null)
    {
        ThrowIfDisposed();
        var secrets = _secrets();
        if (profileId is null)
        {
            return new McpServerProfileEditorViewModel(secrets: secrets);
        }

        var stored = _catalog.Snapshot.McpServerProfiles
            .SingleOrDefault(item => item.Value.Id == profileId.Value)
            ?? throw new InvalidOperationException(
                "That MCP-server profile no longer exists.");
        return new McpServerProfileEditorViewModel(
            stored.Value,
            stored.Revision,
            secrets);
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveAsync(
        McpServerProfileSaveRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsAuthorizedForSave)
        {
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<McpServerProfile>>.Failure(new(
                    DefinitionStoreErrorCode.InvalidDefinition,
                    "Confirm the trusted MCP transport details before saving this profile.")));
        }

        return _catalog.SaveMcpServerProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
    }

    public void Dispose() => _disposed = true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
