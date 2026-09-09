using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record NetworkConnectionProfileItemViewModel(
    NetworkConnectionId Id,
    long Revision,
    string Name,
    NetworkConnectionKind Kind,
    string Summary)
{
    public string KindLabel => NetworkConnectionPresentation.KindLabel(Kind);

    public FluentIcons.Common.Symbol KindSymbol => NetworkConnectionPresentation.KindSymbol(Kind);
}

/// <summary>
/// Owns the reusable connection profiles and the application default policy. Runtime
/// connection state belongs to the workspace network coordinator, not this editor.
/// </summary>
public sealed class NetworkSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly ISecretVault _secretVault;
    private readonly IWorkspaceNetworkRuntime? _workspaceNetworkRuntime;
    private readonly Dictionary<NetworkCredentialTarget, PendingNetworkCredential>
        _pendingCredentials = [];
    private NetworkConnectionProfileEditorViewModel? _profileEditor;
    private NetworkPolicyEditorViewModel _policy;
    private long? _policyRevision;
    private string _applicationSettingsName;
    private string _profileCatalogIdentity = string.Empty;
    private string? _operationStatus;
    private bool _hasError;
    private bool _isTestingProfile;
    private string _profileTestStatus = "Not tested";
    private string _profileTestDetail =
        "Tests the current draft through an app-scoped connection without saving it.";
    private bool _profileTestHasError;
    private bool _profileTestHasResult;
    private CancellationTokenSource? _profileTestCancellation;
    private Task<bool>? _profileTestTask;
    private bool _disposed;

    public NetworkSettingsViewModel(
        IDefinitionCatalog catalog,
        ISecretVault secretVault,
        IWorkspaceNetworkRuntime? workspaceNetworkRuntime = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _workspaceNetworkRuntime = workspaceNetworkRuntime;
        _policy = new([], NetworkPolicy.Direct);
        _applicationSettingsName = ApplicationNetworkSettings.Default.Name;
        ApplyCatalog(_catalog.Snapshot);
    }

    public ObservableCollection<NetworkConnectionProfileItemViewModel> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasNoProfiles => !HasProfiles;

    public NetworkConnectionProfileEditorViewModel? ProfileEditor
    {
        get => _profileEditor;
        private set
        {
            var previous = _profileEditor;
            if (SetProperty(ref _profileEditor, value))
            {
                previous?.PropertyChanged -= OnProfileEditorPropertyChanged;
                value?.PropertyChanged += OnProfileEditorPropertyChanged;

                OnPropertyChanged(nameof(HasProfileEditor));
                OnPropertyChanged(nameof(CanTestProfile));
                ResetProfileTestPresentation();
            }
        }
    }

    public bool HasProfileEditor => ProfileEditor is not null;

    public bool IsTestingProfile
    {
        get => _isTestingProfile;
        private set
        {
            if (SetProperty(ref _isTestingProfile, value))
            {
                OnPropertyChanged(nameof(ProfileTestActionLabel));
                OnPropertyChanged(nameof(CanTestProfile));
                PublishProfileTestResult();
            }
        }
    }

    public string ProfileTestActionLabel =>
        IsTestingProfile ? "Cancel test" : "Test connection";

    /// <summary>Whether a test has said anything yet; before it has, the dialog says nothing.</summary>
    public bool HasProfileTestResult => IsTestingProfile || _profileTestHasResult;

    public bool ProfileTestSucceeded => _profileTestHasResult && !ProfileTestHasError && !IsTestingProfile;

    public bool CanTestProfile => IsTestingProfile
        || (_workspaceNetworkRuntime is not null && ProfileEditor?.IsValid == true);

    public string ProfileTestStatus
    {
        get => _profileTestStatus;
        private set => SetProperty(ref _profileTestStatus, value);
    }

    public string ProfileTestDetail
    {
        get => _profileTestDetail;
        private set => SetProperty(ref _profileTestDetail, value);
    }

    public bool ProfileTestHasError
    {
        get => _profileTestHasError;
        private set => SetProperty(ref _profileTestHasError, value);
    }

    public bool CanStoreCredentials =>
        _secretVault.Availability.CanPersist
        && (_secretVault.Availability.Capabilities & SecretVaultCapabilities.Create) != 0;

    public string CredentialVaultStatus => _secretVault.Availability.Message;

    public NetworkPolicyEditorViewModel Policy
    {
        get => _policy;
        private set
        {
            if (ReferenceEquals(_policy, value))
            {
                return;
            }

            var previous = _policy;
            if (SetProperty(ref _policy, value))
            {
                previous.Dispose();
            }
        }
    }

    public string? OperationStatus
    {
        get => _operationStatus;
        private set
        {
            if (SetProperty(ref _operationStatus, value))
            {
                OnPropertyChanged(nameof(HasOperationStatus));
            }
        }
    }

    public bool HasOperationStatus => OperationStatus is not null;

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public void BeginCreateProfile()
    {
        ThrowIfDisposed();
        if (IsTestingProfile)
        {
            Fail("Cancel the connection test before creating another connection.");
            return;
        }

        if (ProfileEditor is not null)
        {
            Fail("Save or cancel the open network connection before creating another one.");
            return;
        }

        ProfileEditor = new();
        ClearStatus();
    }

    public async ValueTask BeginEditProfileAsync(
        NetworkConnectionProfileItemViewModel item,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (IsTestingProfile)
        {
            Fail("Cancel the connection test before editing another connection.");
            return;
        }

        if (ProfileEditor is not null)
        {
            Fail("Save or cancel the open network connection before editing another one.");
            return;
        }

        var stored = _catalog.Snapshot.NetworkConnections
            .SingleOrDefault(candidate => candidate.Value.Id == item.Id);
        if (stored is null)
        {
            Fail("That network connection no longer exists.");
            return;
        }

        var editor = new NetworkConnectionProfileEditorViewModel(
            stored.Value,
            stored.Revision);
        editor.BeginCredentialMetadataLoad();
        ProfileEditor = editor;
        ClearStatus();

        SecretVaultResult<IReadOnlyList<SecretMetadata>> result;
        try
        {
            result = await _secretVault.ListMetadataAsync(
                new ListSecretMetadataRequest(
                    NetworkCredentialScope(item.Id),
                    new SecretUsePurpose(
                        SecretUseKind.UserManagement,
                        item.Id.Value)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            editor.MarkCredentialMetadataUnavailable();
            return;
        }
        catch (Exception)
        {
            editor.MarkCredentialMetadataUnavailable();
            Warn("The operating-system vault could not provide credential metadata for this connection.");
            return;
        }

        if (!ReferenceEquals(ProfileEditor, editor))
        {
            return;
        }

        if (result is SecretVaultResult<IReadOnlyList<SecretMetadata>>.Failure failure)
        {
            editor.MarkCredentialMetadataUnavailable();
            Warn(failure.Error.Message);
            return;
        }

        var metadata =
            ((SecretVaultResult<IReadOnlyList<SecretMetadata>>.Success)result).Value;
        editor.ApplyCredentialMetadata(metadata);
    }

    public async ValueTask CancelProfileEditAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsTestingProfile)
        {
            await CancelProfileTestAsync();
        }

        var cleanupFailures = await DiscardPendingCredentialsAsync(cancellationToken);
        ProfileEditor = null;
        if (cleanupFailures == 0)
        {
            ClearStatus();
        }
        else
        {
            Warn("The connection draft was discarded, but an unused credential could not be removed from the operating-system vault. Delete it in Security & secrets.");
        }
    }

    public async ValueTask<bool> SaveProfileAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsTestingProfile)
        {
            return Fail("Cancel the connection test before saving the connection.");
        }

        if (ProfileEditor is null)
        {
            return Fail("Open a network connection before saving it.");
        }

        NetworkConnectionProfileSaveRequest request;
        try
        {
            request = ProfileEditor.CreateSaveRequest();
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }

        var original = _catalog.Snapshot.NetworkConnections
            .SingleOrDefault(item => item.Value.Id == request.Profile.Id)?.Value;
        DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>> result;
        try
        {
            var credentialError = await StorePendingCredentialsAsync(
                request.Profile,
                cancellationToken);
            if (credentialError is not null)
            {
                return Fail(credentialError);
            }

            result = await _catalog.SaveNetworkConnectionAsync(
                request.Profile,
                request.ExpectedRevision,
                cancellationToken);
        }
        catch
        {
            _ = await ResetStoredPendingCredentialsAsync(CancellationToken.None);
            throw;
        }

        if (!result.IsSuccess)
        {
            var cleanupFailures = await ResetStoredPendingCredentialsAsync(
                CancellationToken.None);
            var message = result.Error?.Message ?? "The network connection could not be saved.";
            return Fail(cleanupFailures == 0
                ? message
                : message + " An unused credential could not be removed from the operating-system vault.");
        }

        var finalReferences = CredentialReferences(request.Profile.Configuration);
        var detachedReferences = original is null
            ? []
            : CredentialReferences(original.Configuration)
                .Except(finalReferences)
                .ToArray();
        var unusedPendingReferences = _pendingCredentials.Values
            .Where(item => item.IsStored && !finalReferences.Contains(item.Reference))
            .Select(item => item.Reference);
        var cleanupFailuresAfterSave = await DeleteCredentialsAsync(
            detachedReferences.Concat(unusedPendingReferences).Distinct(),
            request.Profile.Id,
            CancellationToken.None);
        DisposePendingCredentials();
        ProfileEditor = null;
        ApplyCatalog(_catalog.Snapshot);
        if (cleanupFailuresAfterSave == 0)
        {
            Succeed($"Saved {request.Profile.Name}.");
        }
        else
        {
            Warn($"Saved {request.Profile.Name}, but an old credential could not be removed from the operating-system vault. Delete it in Security & secrets.");
        }

        return true;
    }

    public async ValueTask<bool> StoreCredentialAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsTestingProfile)
        {
            return Fail("Cancel the connection test before changing credentials.");
        }

        if (ProfileEditor is not { Credential.SelectedTarget: { } target } editor
            || !editor.Credential.CanStore)
        {
            return Fail("Choose a credential type, label it, and enter its value.");
        }

        if (!CanStoreCredentials)
        {
            return Fail(CredentialVaultStatus);
        }

        if (_pendingCredentials.Remove(target.Target, out var previous))
        {
            if (previous.IsStored
                && await DeleteCredentialAsync(
                    previous.Reference,
                    editor.Id,
                    cancellationToken) is false)
            {
                _pendingCredentials.Add(target.Target, previous);
                return Fail("The previous credential could not be replaced because it could not be removed from the operating-system vault.");
            }

            previous.Dispose();
        }

        var reference = SecretRef.New();
        var material = SecretMaterial.TakeOwnership(
            Encoding.UTF8.GetBytes(editor.Credential.Value));
        _pendingCredentials.Add(
            target.Target,
            new PendingNetworkCredential(
                reference,
                editor.Credential.Label,
                target.Kind,
                material));
        editor.ApplyCredential(
            target.Target,
            reference,
            editor.Credential.Label,
            target.Kind);
        editor.Credential.ClearValue();
        editor.CloseCredentialDraft();
        Succeed($"{target.DisplayName} is ready. Save the connection to store it in the credential vault.");
        return true;
    }

    public async ValueTask<bool> TestProfileAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_profileTestTask is { } running)
        {
            _profileTestCancellation?.Cancel();
            _ = await running;
            return false;
        }

        if (_workspaceNetworkRuntime is null)
        {
            SetProfileTestFailure(
                "Test unavailable",
                "The app-scoped networking runtime is unavailable in this build.");
            return false;
        }

        if (ProfileEditor is not { } editor)
        {
            SetProfileTestFailure(
                "Test unavailable",
                "Open a network connection before testing it.");
            return false;
        }

        NetworkConnectionProfile profile;
        try
        {
            profile = editor.CreateSaveRequest().Profile;
        }
        catch (InvalidOperationException exception)
        {
            SetProfileTestFailure("Validation failed", exception.Message);
            return false;
        }

        if (editor.Credential.CanStore)
        {
            SetProfileTestFailure(
                "Credential not added",
                "Add the entered credential to the connection draft before testing.");
            return false;
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _profileTestCancellation = operation;
        IsTestingProfile = true;
        ProfileTestHasError = false;
        ProfileTestStatus = "Testing connection";
        ProfileTestDetail = "Preparing credentials…";
        var task = RunProfileTestAsync(editor, profile, operation.Token);
        _profileTestTask = task;
        try
        {
            return await task;
        }
        finally
        {
            if (ReferenceEquals(_profileTestTask, task))
            {
                _profileTestTask = null;
                _profileTestCancellation = null;
                IsTestingProfile = false;
            }

            operation.Dispose();
        }
    }

    public async ValueTask<bool> DeleteProfileAsync(
        NetworkConnectionProfileItemViewModel item,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (IsTestingProfile)
        {
            return Fail("Cancel the connection test before deleting a connection.");
        }

        var stored = _catalog.Snapshot.NetworkConnections
            .SingleOrDefault(candidate => candidate.Value.Id == item.Id);
        if (stored is null)
        {
            return Fail("That network connection no longer exists.");
        }

        var result = await _catalog.DeleteAsync(
            new DefinitionKey(NetworkConnectionProfile.Kind, item.Id.Value),
            item.Revision,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return Fail(result.Error?.Message ?? "The network connection could not be deleted.");
        }

        var cleanupFailures = await DeleteCredentialsAsync(
            CredentialReferences(stored.Value.Configuration),
            item.Id,
            CancellationToken.None);
        if (ProfileEditor?.Id == item.Id)
        {
            cleanupFailures += await DiscardPendingCredentialsAsync(CancellationToken.None);
            ProfileEditor = null;
        }

        ApplyCatalog(_catalog.Snapshot);
        if (cleanupFailures == 0)
        {
            Succeed($"Deleted {item.Name}.");
        }
        else
        {
            Warn($"Deleted {item.Name}, but a credential could not be removed from the operating-system vault. Delete it in Security & secrets.");
        }

        return true;
    }

    public async ValueTask<bool> SavePolicyAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        NetworkPolicy policy;
        try
        {
            policy = NetworkPolicyResolver.ResolveApplication(
                Policy.CreatePolicy(),
                [.. _catalog.Snapshot.NetworkConnections.Select(item => item.Value)]);
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }

        var settings = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            _applicationSettingsName,
            policy);
        var result = await _catalog.SaveApplicationNetworkSettingsAsync(
            settings,
            _policyRevision,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return Fail(result.Error?.Message ?? "Application networking could not be saved.");
        }

        ApplyCatalog(_catalog.Snapshot);
        Succeed("Saved application networking.");
        return true;
    }

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        var storedSettings = snapshot.ApplicationNetworkSettings.SingleOrDefault(item =>
            item.Value.Id == ApplicationNetworkSettings.DefaultId);
        var settings = storedSettings?.Value ?? ApplicationNetworkSettings.Default;
        var profileIdentity = string.Join(
            '|',
            snapshot.NetworkConnections
                .OrderBy(item => item.Value.Id.Value, StringComparer.Ordinal)
                .Select(item => $"{item.Value.Id.Value}:{item.Revision}"));
        var policyRevisionChanged = _policyRevision != storedSettings?.Revision;
        var profilesChanged = !string.Equals(
            _profileCatalogIdentity,
            profileIdentity,
            StringComparison.Ordinal);

        ReplaceProfiles(snapshot.NetworkConnections);
        if (policyRevisionChanged || profilesChanged)
        {
            var preserveDraft = !policyRevisionChanged && Policy.IsDirty && Policy.IsValid;
            var policy = NetworkPolicyResolver.ResolveApplication(
                preserveDraft ? Policy.CreatePolicy() : settings.Policy,
                [.. snapshot.NetworkConnections.Select(item => item.Value)]);
            Policy = new(
                [.. snapshot.NetworkConnections.Select(item => item.Value)],
                policy,
                isDirty: preserveDraft);
        }

        _policyRevision = storedSettings?.Revision;
        _applicationSettingsName = settings.Name;
        _profileCatalogIdentity = profileIdentity;
    }

    public void ClearStatus()
    {
        HasError = false;
        OperationStatus = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _profileTestCancellation?.Cancel();
        DisposePendingCredentials();
        ProfileEditor = null;
        Policy.Dispose();
    }

    private async Task<bool> RunProfileTestAsync(
        NetworkConnectionProfileEditorViewModel editor,
        NetworkConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        IWorkspaceNetworkSession? session = null;
        var succeeded = false;
        var progressGate = new object();
        var acceptsProgress = true;
        try
        {
            var credentialError = await StorePendingCredentialsAsync(
                profile,
                cancellationToken);
            if (credentialError is not null)
            {
                SetProfileTestFailure("Credential unavailable", credentialError);
                return false;
            }

            var policy = new NetworkPolicy(
                [profile.Id],
                profile.Id,
                isEnabled: true,
                killSwitchEnabled: false);
            var update = new WorkspaceNetworkPolicyUpdate(policy, [profile]);
            var progress = new Progress<NetworkConnectionProgress>(item =>
            {
                lock (progressGate)
                {
                    if (acceptsProgress
                        && ReferenceEquals(ProfileEditor, editor)
                        && IsTestingProfile)
                    {
                        ProfileTestStatus = "Testing connection";
                        ProfileTestDetail = item.Status;
                    }
                }
            });
            session = await _workspaceNetworkRuntime!.OpenAsync(
                new WorkspaceNetworkOpenRequest(
                    WorkspaceInstanceId.New(),
                    update,
                    WorkspaceNetworkPlacement.Host),
                progress,
                cancellationToken);
            lock (progressGate)
            {
                acceptsProgress = false;
                var snapshot = session.Snapshot;
                if (snapshot.State == WorkspaceNetworkState.Connected)
                {
                    ProfileTestHasError = false;
                    _profileTestHasResult = true;
                    ProfileTestStatus = "Host provider ready";
                    ProfileTestDetail = profile.ConnectionKind == NetworkConnectionKind.Proxy
                        ? "The host proxy route passed its provider check. Isolated workspace attachment is verified when the connection is selected in a workspace."
                        : "The host VPN session and local route are ready. Isolated workspace attachment is verified when the connection is selected in a workspace.";
                    succeeded = true;
                }
                else
                {
                    SetProfileTestFailure(
                        "Test failed",
                        snapshot.Error?.Message
                        ?? "The networking runtime did not establish the connection.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (progressGate)
            {
                acceptsProgress = false;
                SetProfileTestFailure(
                    "Test cancelled",
                    "The connection test was cancelled and its temporary route was removed.");
            }
        }
        catch (Exception)
        {
            lock (progressGate)
            {
                acceptsProgress = false;
                SetProfileTestFailure(
                    "Test failed",
                    "The networking runtime could not complete the connection test.");
            }
        }
        finally
        {
            lock (progressGate)
            {
                acceptsProgress = false;
            }
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception)
                {
                    succeeded = false;
                    SetProfileTestFailure(
                        "Cleanup failed",
                        "The test route could not be removed cleanly. Restart Asura before using this connection.");
                }
            }

            var cleanupFailures = await ResetStoredPendingCredentialsAsync(
                CancellationToken.None);
            if (cleanupFailures > 0)
            {
                succeeded = false;
                SetProfileTestFailure(
                    "Credential cleanup failed",
                    "A temporary test credential could not be removed from the operating-system vault. Cancel the draft to retry cleanup.");
            }
        }

        return succeeded;
    }

    private async Task CancelProfileTestAsync()
    {
        var task = _profileTestTask;
        _profileTestCancellation?.Cancel();
        if (task is not null)
        {
            _ = await task;
        }
    }

    private void OnProfileEditorPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(CanTestProfile));
    }

    private void ResetProfileTestPresentation()
    {
        ProfileTestHasError = false;
        ProfileTestStatus = "Not tested";
        ProfileTestDetail =
            "Tests the current draft through an app-scoped connection without saving it.";
        _profileTestHasResult = false;
        PublishProfileTestResult();
    }

    private void SetProfileTestFailure(string status, string detail)
    {
        ProfileTestHasError = true;
        ProfileTestStatus = status;
        ProfileTestDetail = detail;
        _profileTestHasResult = true;
        PublishProfileTestResult();
    }

    private void PublishProfileTestResult()
    {
        OnPropertyChanged(nameof(HasProfileTestResult));
        OnPropertyChanged(nameof(ProfileTestSucceeded));
    }

    private void ReplaceProfiles(
        IReadOnlyList<StoredDefinition<NetworkConnectionProfile>> profiles)
    {
        var projected = profiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new NetworkConnectionProfileItemViewModel(
                item.Value.Id,
                item.Revision,
                item.Value.Name,
                item.Value.ConnectionKind,
                NetworkConnectionPresentation.Summary(item.Value.Configuration)))
            .ToArray();
        if (Profiles.SequenceEqual(projected))
        {
            return;
        }

        Profiles.Clear();
        foreach (var item in projected)
        {
            Profiles.Add(item);
        }

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
    }

    private bool Fail(string message)
    {
        HasError = true;
        OperationStatus = message;
        return false;
    }

    private void Succeed(string message)
    {
        HasError = false;
        OperationStatus = message;
    }

    private void Warn(string message)
    {
        HasError = true;
        OperationStatus = message;
    }

    private async ValueTask<string?> StorePendingCredentialsAsync(
        NetworkConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var references = CredentialReferences(profile.Configuration);
        foreach (var pending in _pendingCredentials.Values.Where(item =>
                     references.Contains(item.Reference) && !item.IsStored))
        {
            using var material = pending.Material.Clone();
            var result = await _secretVault.CreateAsync(
                new CreateSecretRequest(
                    pending.Reference,
                    pending.Label,
                    pending.Kind,
                    NetworkCredentialScope(profile.Id),
                    new SecretUsePurpose(
                        SecretUseKind.NetworkConnectionAuthentication,
                        profile.Id.Value)),
                material,
                cancellationToken);
            if (result is SecretVaultResult<SecretMetadata>.Failure failure)
            {
                _ = await ResetStoredPendingCredentialsAsync(CancellationToken.None);
                return failure.Error.Message;
            }

            pending.IsStored = true;
        }

        return null;
    }

    private async ValueTask<int> ResetStoredPendingCredentialsAsync(
        CancellationToken cancellationToken)
    {
        if (ProfileEditor is not { } editor)
        {
            return 0;
        }

        var failures = 0;
        foreach (var pending in _pendingCredentials.Values.Where(item => item.IsStored))
        {
            if (await DeleteCredentialAsync(
                pending.Reference,
                editor.Id,
                cancellationToken))
            {
                pending.IsStored = false;
            }
            else
            {
                failures++;
            }
        }

        return failures;
    }

    private async ValueTask<int> DiscardPendingCredentialsAsync(
        CancellationToken cancellationToken)
    {
        var failures = await ResetStoredPendingCredentialsAsync(cancellationToken);
        DisposePendingCredentials();
        return failures;
    }

    private async ValueTask<int> DeleteCredentialsAsync(
        IEnumerable<SecretRef> references,
        NetworkConnectionId ownerId,
        CancellationToken cancellationToken)
    {
        var failures = 0;
        foreach (var reference in references)
        {
            if (!await DeleteCredentialAsync(reference, ownerId, cancellationToken))
            {
                failures++;
            }
        }

        return failures;
    }

    private async ValueTask<bool> DeleteCredentialAsync(
        SecretRef reference,
        NetworkConnectionId ownerId,
        CancellationToken cancellationToken)
    {
        var result = await _secretVault.DeleteAsync(
            new DeleteSecretRequest(
                reference,
                NetworkCredentialScope(ownerId),
                new SecretUsePurpose(SecretUseKind.UserManagement, ownerId.Value)),
            cancellationToken);
        return result is SecretVaultResult<Unit>.Success;
    }

    private void DisposePendingCredentials()
    {
        foreach (var pending in _pendingCredentials.Values)
        {
            pending.Dispose();
        }

        _pendingCredentials.Clear();
    }

    private static HashSet<SecretRef> CredentialReferences(
        NetworkConnectionConfiguration configuration) => configuration switch
        {
            NetworkConnectionConfiguration.Proxy proxy => Set(proxy.PasswordSecret),
            NetworkConnectionConfiguration.WireGuard wireGuard =>
                [wireGuard.ConfigurationSecret],
            NetworkConnectionConfiguration.OpenVpn openVpn =>
                Set(openVpn.ConfigurationSecret, openVpn.PasswordSecret),
            NetworkConnectionConfiguration.AnyConnect anyConnect =>
                Set(anyConnect.PasswordSecret, anyConnect.ClientCertificateSecret),
            NetworkConnectionConfiguration.Tailscale tailscale => Set(tailscale.AuthKeySecret),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };

    private static HashSet<SecretRef> Set(params SecretRef?[] references) =>
        [.. references.OfType<SecretRef>()];

    private static SecretScope NetworkCredentialScope(NetworkConnectionId ownerId) =>
        new(SecretScopeKind.NetworkConnection, ownerId.Value);

    private sealed class PendingNetworkCredential(
        SecretRef reference,
        string label,
        SecretKind kind,
        SecretMaterial material) : IDisposable
    {
        public SecretRef Reference { get; } = reference;

        public string Label { get; } = label;

        public SecretKind Kind { get; } = kind;

        public SecretMaterial Material { get; } = material;

        public bool IsStored { get; set; }

        public void Dispose() => Material.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
