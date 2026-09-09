using System.Reflection;
using System.Text;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class BrowserProfileSettingsEditorViewModelTests
{
    [Fact]
    public void SharingChoiceAppliesImmediatelyToNewBrowserProfiles()
    {
        var preferences = new InMemoryBrowserProfilePreferences();
        var editor = new BrowserProfileSettingsEditorViewModel(preferences);

        editor.SelectedSharing = Assert.Single(
            editor.SharingOptions,
            option => option.Sharing == BrowserProfileSharing.PerWorkspace);

        Assert.Equal(BrowserProfileSharing.PerWorkspace, preferences.Current.Sharing);
    }

    [Fact]
    public async Task CookieClearTargetsOnlyTheSelectedNamedProfileRevision()
    {
        var profile = Profile("browser.operations", BrowserProfilePersistence.DurableMetadata);
        var data = new RecordingBrowserProfileDataControl();
        var catalog = Catalog(Store(BuiltInBrowserProfiles.Default, 1), Store(profile, 17));
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            data,
            catalog.Catalog)
        {
            SelectedProfile = null,
        };
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);

        editor.ClearCookiesCommand.Execute(null);
        await data.Cleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => editor.OperationStatus is not null);

        var request = Assert.IsType<BrowserProfileClearRequest>(data.LastRequest);
        Assert.Equal(profile.Id, request.Selection.ProfileId);
        Assert.Equal(BrowserProfileKey.ForNamed(profile.Id.Value), request.Selection.Partition);
        Assert.Equal(17, request.ExpectedRevision);
        Assert.Equal(BrowserProfileDataCategory.Cookies, request.Categories);
        Assert.Equal("Cookies cleared.", editor.OperationStatus);
    }

    [Fact]
    public async Task InProgressClearCanBeCancelledFromSettings()
    {
        var profile = Profile("browser.cancel-clear", BrowserProfilePersistence.DurableMetadata);
        var data = new CancellableBrowserProfileDataControl();
        var catalog = Catalog(Store(BuiltInBrowserProfiles.Default, 1), Store(profile, 9));
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            data,
            catalog.Catalog);
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);

        editor.ClearCookiesCommand.Execute(null);
        await data.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(editor.IsClearing);
        Assert.True(editor.CancelClearCommand.CanExecute(null));

        editor.CancelClearCommand.Execute(null);
        await WaitForAsync(() => !editor.IsClearing);

        Assert.Equal("Browser profile clearing was cancelled.", editor.OperationStatus);
    }

    [Fact]
    public void PrivateSessionClearIsUnavailableBecauseEachPanelHasItsOwnPartition()
    {
        var profile = Profile("browser.private", BrowserProfilePersistence.PrivateSession);
        var catalog = Catalog(Store(BuiltInBrowserProfiles.Default, 1), Store(profile, 4));
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            new RecordingBrowserProfileDataControl(),
            catalog.Catalog);
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);

        Assert.False(editor.ClearCookiesCommand.CanExecute(null));
        Assert.False(editor.ResetEphemeralContentCommand.CanExecute(null));
        Assert.Contains("Close that panel", editor.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerWorkspaceBuiltInClearDoesNotInventABroadOrFakePartition()
    {
        var preferences = new InMemoryBrowserProfilePreferences();
        await preferences.ApplyAsync(
            new BrowserProfileSettings(BrowserProfileSharing.PerWorkspace),
            CancellationToken.None);
        var catalog = Catalog(Store(BuiltInBrowserProfiles.Default, 1));
        var editor = new BrowserProfileSettingsEditorViewModel(
            preferences,
            new RecordingBrowserProfileDataControl(),
            catalog.Catalog);

        Assert.False(editor.ClearAuthenticationCommand.CanExecute(null));
        Assert.Contains("per workspace", editor.StateText, StringComparison.Ordinal);
        Assert.Contains("will not issue a broad", editor.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleRefreshesTheCatalogProjectionAfterSave()
    {
        var profile = Profile("browser.toggle", BrowserProfilePersistence.DurableMetadata);
        var fixture = Catalog(Store(BuiltInBrowserProfiles.Default, 1), Store(profile, 6));
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            catalog: fixture.Catalog);
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);

        editor.ToggleEnabledCommand.Execute(null);
        await fixture.Proxy.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => editor.SelectedProfile?.IsEnabled == false);

        Assert.False(editor.SelectedProfile!.IsEnabled);
        Assert.Contains("disabled", editor.OperationStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpAuthenticationCreatesAnExactBrowserScopedVaultSecret()
    {
        var profile = Profile(
            "browser.auth",
            BrowserProfilePersistence.DurableMetadata);
        var fixture = Catalog(Store(BuiltInBrowserProfiles.Default, 1), Store(profile, 7));
        var vault = Vault();
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            catalog: fixture.Catalog,
            secretVault: vault.Vault);
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);
        editor.AuthenticationHost = "Internal.Example.";
        editor.AuthenticationPort = "8443";
        editor.AuthenticationRealm = "Operations";
        editor.AuthenticationScheme = BrowserAuthenticationScheme.Digest;
        editor.AuthenticationUsername = "operator";
        editor.AuthenticationPassword = "correct horse battery staple";

        editor.SaveAuthenticationCommand.Execute(null);
        await vault.Proxy.Created.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Proxy.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => editor.HasAuthentication);

        var request = Assert.IsType<CreateSecretRequest>(vault.Proxy.CreateRequest);
        Assert.Equal(SecretKind.Password, request.Kind);
        Assert.Equal(SecretScopeKind.BrowserProfile, request.Scope.Kind);
        Assert.Equal(profile.Id.Value, request.Scope.OwnerId);
        Assert.Equal(SecretUseKind.UserManagement, request.Purpose.Kind);
        Assert.Equal(profile.Id.Value, request.Purpose.TargetId);
        Assert.Equal("correct horse battery staple", vault.Proxy.CreatedValue);
        var authentication = Assert.IsType<BrowserHttpAuthentication>(
            fixture.Proxy.LastSaved!.Authentication);
        Assert.Equal("internal.example", authentication.Host);
        Assert.Equal(8443, authentication.Port);
        Assert.Equal("Operations", authentication.Realm);
        Assert.Equal(BrowserAuthenticationScheme.Digest, authentication.Scheme);
        Assert.Equal("operator", authentication.Username);
        Assert.Equal(request.Reference, authentication.PasswordSecret);
        Assert.Equal("https", authentication.OriginScheme);
        Assert.Equal("local", authentication.RouteIdentity);
        Assert.Equal(string.Empty, editor.AuthenticationPassword);
    }

    [Fact]
    public async Task HttpAuthenticationReplacementSavesNewReferenceBeforeDeletingOldSecret()
    {
        var oldReference = new SecretRef("browser-old-password");
        var profile = Profile(
            "browser.rotate",
            BrowserProfilePersistence.DurableMetadata,
            new BrowserHttpAuthentication(
                "old.example",
                null,
                null,
                BrowserAuthenticationScheme.Basic,
                "old-user",
                oldReference));
        var fixture = Catalog(Store(BuiltInBrowserProfiles.Default, 1), Store(profile, 4));
        var vault = Vault();
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            catalog: fixture.Catalog,
            secretVault: vault.Vault);
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);
        editor.AuthenticationHost = "new.example";
        editor.AuthenticationUsername = "new-user";
        editor.AuthenticationPassword = "replacement";

        editor.SaveAuthenticationCommand.Execute(null);
        await fixture.Proxy.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vault.Proxy.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var authentication = Assert.IsType<BrowserHttpAuthentication>(
            fixture.Proxy.LastSaved!.Authentication);
        Assert.NotEqual(oldReference, authentication.PasswordSecret);
        Assert.Equal(oldReference, vault.Proxy.DeleteRequest!.Reference);
        var operations = vault.Proxy.Operations.Concat(fixture.Proxy.Operations)
            .OrderBy(item => item.Sequence)
            .Select(item => item.Name);
        Assert.True(
            new[] { "create", "save", "delete" }.SequenceEqual(
                operations,
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task Http_authentication_editor_saves_explicit_transport_and_ssh_authority()
    {
        var profile = Profile("browser.route", BrowserProfilePersistence.DurableMetadata);
        var connection = new ConnectionProfile(new ConnectionId("ssh"), 1, "Work SSH",
            new ConnectionEndpoint.Ssh("bastion.example", 22, "alice"), new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        var fixture = Catalog(Store(profile, 1));
        var network = new NetworkConnectionProfile(new NetworkConnectionId("vpn"), 1, "Work VPN",
            new NetworkConnectionConfiguration.AnyConnect(new Uri("https://vpn.example")));
        fixture.Proxy.CurrentSnapshot = fixture.Proxy.CurrentSnapshot with
        {
            Connections = [Store(connection, 1)],
            NetworkConnections = [Store(network, 1)],
        };
        var vault = Vault();
        var editor = new BrowserProfileSettingsEditorViewModel(new InMemoryBrowserProfilePreferences(),
            catalog: fixture.Catalog, secretVault: vault.Vault);
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);
        Assert.Contains(editor.AuthenticationRoutes, route => route.DisplayName == "Workspace network · Work VPN"
            && string.Equals(route.Identity, BrowserHttpAuthentication.NetworkRouteIdentity(network), StringComparison.Ordinal));
        editor.AuthenticationOriginScheme = "http";
        editor.AuthenticationRoute = Assert.Single(editor.AuthenticationRoutes, route => route.DisplayName == "SSH · Work SSH");
        editor.AuthenticationHost = "internal.example";
        editor.AuthenticationUsername = "operator";
        editor.AuthenticationPassword = "password";

        editor.SaveAuthenticationCommand.Execute(null);
        await fixture.Proxy.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var saved = Assert.IsType<BrowserHttpAuthentication>(fixture.Proxy.LastSaved!.Authentication);
        Assert.Equal("http", saved.OriginScheme);
        Assert.Equal(BrowserHttpAuthentication.SshRouteIdentity(connection), saved.RouteIdentity);
        Assert.NotEqual("local", saved.RouteIdentity, StringComparer.Ordinal);
    }

    [Fact]
    public async Task DetachRemovesDefinitionBindingBeforeDeletingVaultSecret()
    {
        var reference = new SecretRef("browser-detach-password");
        var profile = Profile(
            "browser.detach",
            BrowserProfilePersistence.DurableMetadata,
            new BrowserHttpAuthentication(
                "internal.example",
                null,
                null,
                BrowserAuthenticationScheme.Basic,
                "operator",
                reference));
        var fixture = Catalog(Store(BuiltInBrowserProfiles.Default, 1), Store(profile, 8));
        var vault = Vault();
        var editor = new BrowserProfileSettingsEditorViewModel(
            new InMemoryBrowserProfilePreferences(),
            catalog: fixture.Catalog,
            secretVault: vault.Vault);
        editor.SelectedProfile = Assert.Single(editor.Profiles, item => item.Id == profile.Id);

        editor.DetachAuthenticationCommand.Execute(null);
        await fixture.Proxy.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vault.Proxy.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(fixture.Proxy.LastSaved!.Authentication);
        Assert.Equal(reference, vault.Proxy.DeleteRequest!.Reference);
        Assert.False(editor.HasAuthentication);
    }

    private static BrowserProfileDefinition Profile(
        string id,
        BrowserProfilePersistence persistence,
        BrowserHttpAuthentication? authentication = null) => new(
        new BrowserProfileId(id),
        BrowserProfileDefinition.CurrentSchemaVersion,
        id,
        persistence,
        BrowserProfilePrivacyPolicy.Strict,
        authentication);

    private static VaultFixture Vault()
    {
        var vault = DispatchProxy.Create<ISecretVault, RecordingVaultProxy>();
        return new(vault, (RecordingVaultProxy)(object)vault);
    }

    private static StoredDefinition<T> Store<T>(T definition, long revision)
        where T : IDurableDefinition =>
        new(definition, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static CatalogFixture Catalog(
        params StoredDefinition<BrowserProfileDefinition>[] profiles)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var proxy = (RecordingCatalogProxy)(object)catalog;
        proxy.CurrentSnapshot = DefinitionCatalogSnapshot.Empty with
        {
            BrowserProfiles = profiles,
        };
        return new(catalog, proxy);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "The settings command did not finish.");
            await Task.Delay(10);
        }
    }

    private sealed record CatalogFixture(
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Proxy);

    private sealed record VaultFixture(
        ISecretVault Vault,
        RecordingVaultProxy Proxy);

    public sealed record RecordedOperation(long Sequence, string Name);

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public TaskCompletionSource Saved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BrowserProfileDefinition? LastSaved { get; private set; }

        public List<RecordedOperation> Operations { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveBrowserProfileAsync) => Save(
                    (BrowserProfileDefinition)args[0]!,
                    (long?)args[1]),
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<BrowserProfileDefinition>>> Save(
            BrowserProfileDefinition definition,
            long? expectedRevision)
        {
            LastSaved = definition;
            Operations.Add(new(RecordingSequence.Next(), "save"));
            var stored = Store(definition, (expectedRevision ?? 0) + 1);
            CurrentSnapshot = CurrentSnapshot with
            {
                BrowserProfiles =
                [
                    .. CurrentSnapshot.BrowserProfiles
                        .Where(item => item.Value.Id != definition.Id),
                    stored,
                ],
            };
            Saved.TrySetResult();
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<BrowserProfileDefinition>>.Success(stored));
        }
    }

    public class RecordingVaultProxy : DispatchProxy
    {
        public CreateSecretRequest? CreateRequest { get; private set; }

        public DeleteSecretRequest? DeleteRequest { get; private set; }

        public string? CreatedValue { get; private set; }

        public List<RecordedOperation> Operations { get; } = [];

        public TaskCompletionSource Created { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Deleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            args ??= [];
            if (targetMethod?.Name == nameof(ISecretVault.CreateAsync)
                && args is
                [
                    CreateSecretRequest createRequest,
                    SecretMaterial material,
                    CancellationToken,
                ])
            {
                var bytes = new byte[material.Length];
                try
                {
                    material.CopyTo(bytes);
                    CreatedValue = Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                }
                CreateRequest = createRequest;
                Operations.Add(new(RecordingSequence.Next(), "create"));
                Created.TrySetResult();
                return ValueTask.FromResult(
                    SecretVaultResult<SecretMetadata>.Succeed(new SecretMetadata(
                        createRequest.Reference,
                        createRequest.Label,
                        createRequest.Kind,
                        createRequest.Scope,
                        SecretVaultPersistenceKind.OsProtectedPersistent,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));
            }

            if (targetMethod?.Name == nameof(ISecretVault.DeleteAsync)
                && args is [DeleteSecretRequest deleteRequest, CancellationToken])
            {
                DeleteRequest = deleteRequest;
                Operations.Add(new(RecordingSequence.Next(), "delete"));
                Deleted.TrySetResult();
                return ValueTask.FromResult(
                    SecretVaultResult<Unit>.Succeed(Unit.Value));
            }

            return targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.OsProtectedPersistent,
                    SecretVaultCapabilities.All,
                    "test",
                    "test_available",
                    "Test vault is available."),
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }
    }

    private static class RecordingSequence
    {
        private static long _value;

        public static long Next() => Interlocked.Increment(ref _value);
    }

    private sealed class RecordingBrowserProfileDataControl :
        IBrowserProfileDataControl
    {
        public TaskCompletionSource Cleared { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BrowserProfileClearRequest? LastRequest { get; private set; }

        public BrowserProfileDataState ReadState(
            BrowserProfileSelection selection,
            long expectedRevision) => new(selection, expectedRevision, 1, 1);

        public ValueTask<BrowserProfileClearResult> ClearAsync(
            BrowserProfileClearRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            Cleared.TrySetResult();
            return ValueTask.FromResult(new BrowserProfileClearResult(
                BrowserProfileClearStatus.Cleared,
                0,
                "Cookies cleared."));
        }
    }

    private sealed class CancellableBrowserProfileDataControl :
        IBrowserProfileDataControl
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BrowserProfileDataState ReadState(
            BrowserProfileSelection selection,
            long expectedRevision) => new(selection, expectedRevision, 1, 1);

        public async ValueTask<BrowserProfileClearResult> ClearAsync(
            BrowserProfileClearRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The clear cancellation test did not cancel.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.Cancelled,
                    0,
                    "Browser profile clearing was cancelled.");
            }
        }
    }
}
