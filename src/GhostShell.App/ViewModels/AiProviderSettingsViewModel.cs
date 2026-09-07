using System.Collections.ObjectModel;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns AI-provider definition projection, authoring, and optimistic persistence.
/// Agent policy, approvals, audit, and secret mutation remain shell-host concerns.
/// </summary>
public sealed class AiProviderSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly IAiProviderProfileRuntime? _runtime;
    private readonly IAiProviderAuthenticationRuntime? _authenticationRuntime;
    private readonly Func<IReadOnlyList<SecretMetadataViewModel>> _secretMetadata;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private DefinitionCatalogSnapshot _snapshot;
    private bool _disposed;

    public AiProviderSettingsViewModel(
        IDefinitionCatalog catalog,
        IAiProviderProfileRuntime? runtime,
        IAiProviderAuthenticationRuntime? authenticationRuntime,
        Func<IReadOnlyList<SecretMetadataViewModel>> secretMetadata,
        IUiThreadDispatcher dispatcher)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtime = runtime;
        _authenticationRuntime = authenticationRuntime;
        _secretMetadata = secretMetadata ?? throw new ArgumentNullException(nameof(secretMetadata));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _snapshot = _catalog.Snapshot;
        _runtime?.ProfilesChanged += OnProfilesChanged;
        RefreshDefinitions();
    }

    public event EventHandler? RuntimeProfilesChanged;

    public ObservableCollection<AiProviderProfileItemViewModel> Definitions { get; } = [];

    public IReadOnlyList<AiProviderProfileDescriptor> Profiles => _runtime?.Profiles ?? [];

    public bool HasProviders => Definitions.Count > 0;

    public bool HasNoProviders => !HasProviders;

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        RefreshDefinitions();
    }

    public AiProviderProfileEditorViewModel CreateEditor(
        AiProviderProfileId? profileId = null,
        Func<CreateSecretRequest, SecretMaterial, CancellationToken, ValueTask<SecretVaultResult<SecretMetadata>>>? storeCredential = null)
    {
        ThrowIfDisposed();
        var runtime = _runtime
            ?? throw new InvalidOperationException("The AI-provider runtime is unavailable.");
        var secrets = _secretMetadata();
        if (profileId is null)
        {
            return new AiProviderProfileEditorViewModel(
                runtime,
                secrets,
                suggestedOrder: NextOrder(_catalog.Snapshot),
                authenticationRuntime: _authenticationRuntime,
                storeCredential: storeCredential);
        }

        var stored = _catalog.Snapshot.AiProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId.Value)
            ?? throw new InvalidOperationException(
                "That AI-provider profile no longer exists.");
        return new AiProviderProfileEditorViewModel(
            runtime,
            secrets,
            stored.Value,
            stored.Revision,
            authenticationRuntime: _authenticationRuntime,
            storeCredential: storeCredential);
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAsync(
        AiProviderProfileSaveRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        return _catalog.SaveAiProviderProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime?.ProfilesChanged -= OnProfilesChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        RuntimeProfilesChanged = null;
    }

    private async void OnProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    RefreshDefinitions();
                    RuntimeProfilesChanged?.Invoke(this, EventArgs.Empty);
                },
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void RefreshDefinitions()
    {
        var descriptors = Profiles.ToDictionary(item => item.Id);
        var diagnostics = (_runtime?.Diagnostics ?? [])
            .Where(item => item.ProfileId is not null)
            .GroupBy(item => item.ProfileId!.Value)
            .ToDictionary(item => item.Key, item => item.ToArray());
        var secrets = _secretMetadata();
        ReplaceIfChanged(
            Definitions,
            [.. _snapshot.AiProviderProfiles
                .OrderBy(item => item.Value.Order)
                .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => CreateDefinitionItem(item, descriptors, diagnostics, secrets))]);
        OnPropertyChanged(nameof(Definitions));
        OnPropertyChanged(nameof(Profiles));
        OnPropertyChanged(nameof(HasProviders));
        OnPropertyChanged(nameof(HasNoProviders));
    }

    private static AiProviderProfileItemViewModel CreateDefinitionItem(
        StoredDefinition<AiProviderProfile> item,
        IReadOnlyDictionary<AiProviderProfileId, AiProviderProfileDescriptor> descriptors,
        IReadOnlyDictionary<AiProviderProfileId, AiProviderRuntimeDiagnostic[]> diagnostics,
        IReadOnlyList<SecretMetadataViewModel> secrets)
    {
        descriptors.TryGetValue(item.Value.Id, out var descriptor);
        diagnostics.TryGetValue(item.Value.Id, out var profileDiagnostics);
        var error = profileDiagnostics?.FirstOrDefault(diagnostic =>
            diagnostic.Severity == AiProviderRuntimeDiagnosticSeverity.Error);
        var warning = profileDiagnostics?.FirstOrDefault(diagnostic =>
            diagnostic.Severity == AiProviderRuntimeDiagnosticSeverity.Warning);
        var needsCredential = item.Value.Authentication is AiProviderAuthentication.ApiKey apiKey
            && secrets.All(secret => secret.Reference != apiKey.Secret);
        var status = !item.Value.IsEnabled
            ? "Disabled"
            : error is not null
                ? "Unavailable"
                : needsCredential
                    ? "Credential missing"
                    : descriptor is null
                        ? "Loading"
                        : "Ready";
        return new(
            item.Value.Id,
            item.Revision,
            item.Value.Name,
            AiProviderCatalog.Get(item.Value.Identity).DisplayName,
            item.Value.Endpoint.AbsoluteUri,
            item.Value.DefaultModel,
            item.Value.Order,
            status,
            error?.Message
                ?? warning?.Message
                ?? (needsCredential
                    ? "Store the API key in the system keychain before testing."
                    : item.Value.IsEnabled
                        ? "Ready."
                        : "This provider is disabled."),
            item.Value.IsEnabled,
            error is not null || needsCredential,
            warning is not null,
            needsCredential);
    }

    private static void ReplaceIfChanged(
        ObservableCollection<AiProviderProfileItemViewModel> target,
        IReadOnlyList<AiProviderProfileItemViewModel> values)
    {
        if (target.SequenceEqual(values))
        {
            return;
        }

        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static int NextOrder(DefinitionCatalogSnapshot snapshot)
    {
        var used = snapshot.AiProviderProfiles.Select(item => item.Value.Order).ToHashSet();
        for (var order = 0; order <= AiProviderProfile.MaximumOrder; order++)
        {
            if (!used.Contains(order))
            {
                return order;
            }
        }

        throw new InvalidOperationException(
            "Every available AI-provider display position is already in use.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
