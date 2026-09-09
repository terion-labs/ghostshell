using System.Collections.ObjectModel;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns credential-vault mutation, dependency validation, and metadata
/// projection. The shell supplies the runtime effects that cross feature
/// boundaries after a credential changes.
/// </summary>
public sealed class SecretSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly ISecretVault _vault;
    private readonly IFileProviderProfileRuntime? _fileProviderRuntime;
    private readonly IAiProviderProfileRuntime? _aiProviderRuntime;
    private readonly IMcpCredentialSessionInvalidator? _mcpSessionInvalidator;
    private readonly Action<SecretRef> _invalidateMcpTests;
    private readonly Action _clearError;
    private readonly Action<string> _setError;
    private string _status = "Checking the operating-system vault…";
    private bool _disposed;

    public SecretSettingsViewModel(
        IDefinitionCatalog catalog,
        ISecretVault vault,
        IFileProviderProfileRuntime? fileProviderRuntime,
        IAiProviderProfileRuntime? aiProviderRuntime,
        IMcpCredentialSessionInvalidator? mcpSessionInvalidator,
        Action<SecretRef> invalidateMcpTests,
        Action clearError,
        Action<string> setError)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _fileProviderRuntime = fileProviderRuntime;
        _aiProviderRuntime = aiProviderRuntime;
        _mcpSessionInvalidator = mcpSessionInvalidator;
        _invalidateMcpTests = invalidateMcpTests
            ?? throw new ArgumentNullException(nameof(invalidateMcpTests));
        _clearError = clearError ?? throw new ArgumentNullException(nameof(clearError));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
    }

    public event EventHandler? ProjectionChanged;

    public ObservableCollection<SecretMetadataViewModel> Secrets { get; } = [];

    public bool HasNoSecrets => Secrets.Count == 0;

    public IReadOnlyList<SecretKind> Kinds { get; } = Enum.GetValues<SecretKind>();

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async ValueTask<bool> CreateConnectionAsync(
        ConnectionId connectionId,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _clearError();
        if (_catalog.Snapshot.Connections.All(item => item.Value.Id != connectionId))
        {
            return Fail("Choose an existing connection for this credential.");
        }

        return await CreateAsync(
            SecretRef.New(),
            label,
            kind,
            value,
            new SecretScope(SecretScopeKind.Connection, connectionId.Value),
            cancellationToken);
    }

    public async ValueTask<bool> CreateFileProviderAsync(
        FileProviderProfileId profileId,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _clearError();
        if (_catalog.Snapshot.FileProviderProfiles.All(item => item.Value.Id != profileId))
        {
            return Fail("Choose an existing file-provider profile for this credential.");
        }

        return await CreateAsync(
            SecretRef.New(),
            label,
            kind,
            value,
            new SecretScope(SecretScopeKind.FileProvider, profileId.Value),
            cancellationToken);
    }

    public async ValueTask<SecretVaultResult<SecretMetadata>> CreateAiProviderDraftAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(material);
        if (request.Scope.Kind != SecretScopeKind.AiProvider || request.Kind != SecretKind.ApiKey)
        {
            return SecretVaultResult<SecretMetadata>.Fail(SecretVaultError.Create(SecretVaultErrorCode.InvalidRequest));
        }

        // Drafts already have a stable provider ID but are not in the catalog yet.
        var result = await _vault.CreateAsync(request, material, cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Success)
        {
            await RefreshAsync(cancellationToken);
        }

        return result;
    }

    public async ValueTask<bool> CreateAiProviderAsync(
        AiProviderProfileId profileId,
        string label,
        string value,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _clearError();
        var profile = _catalog.Snapshot.AiProviderProfiles
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return Fail("Choose an existing AI-provider profile for this credential.");
        }

        if (profile.Authentication is not AiProviderAuthentication.ApiKey apiKey)
        {
            return Fail("This provider is configured for local unauthenticated access.");
        }

        var created = await CreateAsync(
            apiKey.Secret,
            label,
            SecretKind.ApiKey,
            value,
            new SecretScope(SecretScopeKind.AiProvider, profileId.Value),
            cancellationToken);
        if (created && _aiProviderRuntime is not null)
        {
            await _aiProviderRuntime.ReloadAsync(cancellationToken);
        }

        return created;
    }

    public async ValueTask<bool> CreateMcpServerAsync(
        McpServerSecretTargetViewModel target,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(target);
        _clearError();
        var profile = _catalog.Snapshot.McpServerProfiles
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == target.ProfileId);
        var bindingStillExists = profile is not null
            && SecretDefinitionReferences
                .EnumerateMcpServerCredentialBindings(profile)
                .Any(binding =>
                    binding.Kind == target.BindingKind
                    && string.Equals(
                        binding.Name,
                        target.BindingName,
                        StringComparison.Ordinal)
                    && binding.Reference == target.Reference);
        if (!bindingStillExists)
        {
            return Fail("That MCP credential binding changed. Reopen the server settings.");
        }

        return await CreateAsync(
            target.Reference,
            label,
            kind,
            value,
            new SecretScope(SecretScopeKind.McpServer, target.ProfileId.Value),
            cancellationToken);
    }

    public async ValueTask<bool> DeleteAsync(
        SecretMetadataViewModel secret,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(secret);
        _clearError();
        var dependents = Dependencies(_catalog.Snapshot, secret.Reference);
        if (dependents.Length > 0)
        {
            return Fail($"This credential is still referenced by: {string.Join(", ", dependents)}. Replace the reference before deleting it.");
        }

        var result = await _vault.DeleteAsync(
            new DeleteSecretRequest(
                secret.Reference,
                secret.SecretScope,
                ManagementPurpose(secret)),
            cancellationToken);
        if (result is SecretVaultResult<Unit>.Failure failure)
        {
            return Fail(failure.Error.Message);
        }

        await CompleteMcpMutationAsync(secret);
        await RefreshAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> RelabelAsync(
        SecretMetadataViewModel secret,
        string label,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(secret);
        _clearError();
        SecretVaultResult<SecretMetadata> result;
        try
        {
            result = await _vault.RelabelAsync(
                new RelabelSecretRequest(
                    secret.Reference,
                    secret.SecretScope,
                    label,
                    ManagementPurpose(secret)),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Fail(exception.Message);
        }

        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            return Fail(failure.Error.Message);
        }

        await RefreshAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> ReplaceAsync(
        SecretMetadataViewModel secret,
        SecretMaterial replacement,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(replacement);
        _clearError();
        var result = await _vault.ReplaceAsync(
            new ReplaceSecretRequest(
                secret.Reference,
                secret.SecretScope,
                ManagementPurpose(secret)),
            replacement,
            cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            return Fail(failure.Error.Message);
        }

        await CompleteMcpMutationAsync(secret);
        await RefreshAsync(cancellationToken);
        if (secret.SecretScope.Kind == SecretScopeKind.FileProvider
            && _fileProviderRuntime is not null)
        {
            await _fileProviderRuntime.ReloadAsync(cancellationToken);
        }
        else if (secret.SecretScope.Kind == SecretScopeKind.AiProvider
            && _aiProviderRuntime is not null)
        {
            await _aiProviderRuntime.ReloadAsync(cancellationToken);
        }

        return true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            Status = _vault.Availability.Message;
            var result = await _vault.ListMetadataAsync(
                new ListSecretMetadataRequest(null, SecretUsePurpose.ManageAll()),
                cancellationToken);
            if (result is SecretVaultResult<IReadOnlyList<SecretMetadata>>.Failure failure)
            {
                Status = failure.Error.Message;
                return;
            }

            var metadata =
                ((SecretVaultResult<IReadOnlyList<SecretMetadata>>.Success)result).Value;
            Replace(metadata
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .Select(item => Project(item, _catalog.Snapshot)));
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!_disposed)
        {
            Status = "The operating-system credential vault could not be queried.";
            OnPropertyChanged(nameof(HasNoSecrets));
        }
    }

    public void ReportStatus(string message)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Status = message.Trim();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ProjectionChanged = null;
    }

    private async ValueTask<bool> CreateAsync(
        SecretRef reference,
        string label,
        SecretKind kind,
        string value,
        SecretScope scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrEmpty(value))
        {
            return Fail("Credential label and value are required.");
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        using var material = SecretMaterial.TakeOwnership(bytes);
        var result = await _vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                label.Trim(),
                kind,
                scope,
                new SecretUsePurpose(
                    SecretUseKind.UserManagement,
                    scope.OwnerId ?? SecretUsePurpose.GlobalTargetId)),
            material,
            cancellationToken);
        if (result is SecretVaultResult<SecretMetadata>.Failure failure)
        {
            return Fail(failure.Error.Message);
        }

        await RefreshAsync(cancellationToken);
        return true;
    }

    private async Task CompleteMcpMutationAsync(SecretMetadataViewModel secret)
    {
        if (secret.SecretScope.Kind != SecretScopeKind.McpServer)
        {
            return;
        }

        _invalidateMcpTests(secret.Reference);
        if (_mcpSessionInvalidator is not null)
        {
            await _mcpSessionInvalidator.InvalidateAsync(secret.Reference);
        }
    }

    private void Replace(IEnumerable<SecretMetadataViewModel> values)
    {
        Secrets.Clear();
        foreach (var value in values)
        {
            Secrets.Add(value);
        }

        OnPropertyChanged(nameof(Secrets));
        OnPropertyChanged(nameof(HasNoSecrets));
    }

    private static SecretMetadataViewModel Project(
        SecretMetadata metadata,
        DefinitionCatalogSnapshot snapshot)
    {
        var dependencies = Dependencies(snapshot, metadata.Reference);
        return new SecretMetadataViewModel(
            metadata.Reference,
            metadata.Label,
            metadata.Kind.ToString(),
            metadata.Scope.Kind == SecretScopeKind.Global
                ? "Global"
                : $"{metadata.Scope.Kind} · {metadata.Scope.OwnerId}",
            metadata.UpdatedAt.ToLocalTime().ToString(
                "g",
                System.Globalization.CultureInfo.InvariantCulture),
            metadata.LastUsedAt?.ToLocalTime().ToString(
                "g",
                System.Globalization.CultureInfo.InvariantCulture) ?? "Never",
            metadata.Scope,
            dependencies.Length == 0
                ? "No saved definition dependencies"
                : $"Used by: {string.Join(", ", dependencies)}",
            dependencies.Length);
    }

    private static string[] Dependencies(
        DefinitionCatalogSnapshot snapshot,
        SecretRef reference)
    {
        var connections = snapshot.Connections
            .Select(item => item.Value)
            .Where(connection => SecretDefinitionReferences.Uses(connection, reference))
            .Select(connection => $"connection {connection.Name}");
        var fileProviders = snapshot.FileProviderProfiles
            .Select(item => item.Value)
            .Where(profile => SecretDefinitionReferences.Uses(profile, reference))
            .Select(profile => $"file provider {profile.Name}");
        var aiProviders = snapshot.AiProviderProfiles
            .Select(item => item.Value)
            .Where(profile => SecretDefinitionReferences.Uses(profile, reference))
            .Select(profile => $"AI provider {profile.Name}");
        var mcpServers = snapshot.McpServerProfiles
            .Select(item => item.Value)
            .Where(profile => SecretDefinitionReferences.Uses(profile, reference))
            .Select(profile => $"MCP server {profile.Name}");
        var browserProfiles = snapshot.BrowserProfiles
            .Select(item => item.Value)
            .Where(profile => SecretDefinitionReferences.Uses(profile, reference))
            .Select(profile => $"browser profile {profile.Name}");
        return [.. connections
            .Concat(fileProviders)
            .Concat(aiProviders)
            .Concat(mcpServers)
            .Concat(browserProfiles)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static SecretUsePurpose ManagementPurpose(
        SecretMetadataViewModel secret) =>
        new(
            SecretUseKind.UserManagement,
            secret.SecretScope.Kind == SecretScopeKind.Global
                ? SecretUsePurpose.GlobalTargetId
                : secret.SecretScope.OwnerId!);

    private bool Fail(string message)
    {
        _setError(message);
        return false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
