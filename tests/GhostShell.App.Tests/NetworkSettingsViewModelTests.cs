using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class NetworkSettingsViewModelTests
{
    private static readonly NetworkConnectionId ProxyId = new("office-proxy");

    [Fact]
    public void Profile_editor_round_trips_every_connection_kind_without_secret_material()
    {
        NetworkConnectionConfiguration[] configurations =
        [
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "proxy.example.test",
                1080,
                "alice",
                new SecretRef("proxy-password")),
            new NetworkConnectionConfiguration.WireGuard(new SecretRef("wireguard-config")),
            new NetworkConnectionConfiguration.OpenVpn(new SecretRef("openvpn-profile")),
            new NetworkConnectionConfiguration.OpenVpn(
                new SecretRef("openvpn-profile"), "alice", new SecretRef("openvpn-password")),
            new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example.test"),
                "alice",
                new SecretRef("vpn-password"),
                "employees",
                new SecretRef("vpn-certificate")),
            new NetworkConnectionConfiguration.Tailscale(
                "exit-node",
                new Uri("https://control.example.test"),
                new SecretRef("tailscale-auth-key")),
        ];

        foreach (var configuration in configurations)
        {
            var profile = Profile(configuration);
            var editor = new NetworkConnectionProfileEditorViewModel(profile, 17);

            var saved = editor.CreateSaveRequest();

            Assert.Equal(17, saved.ExpectedRevision);
            Assert.Equal(profile, saved.Profile);
        }
    }

    [Fact]
    public void Profile_editor_rejects_a_proxy_password_without_a_username()
    {
        var editor = new NetworkConnectionProfileEditorViewModel
        {
            Name = "Office proxy",
            Host = "proxy.example.test",
            Port = "1080",
            PasswordSecretReference = "proxy-password",
        };

        Assert.False(editor.IsValid);
        Assert.Contains("username", editor.ValidationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => editor.CreateSaveRequest());
    }

    [Fact]
    public void Password_profiles_offer_a_session_only_prompt_instead_of_requiring_storage()
    {
        var editor = new NetworkConnectionProfileEditorViewModel
        {
            Name = "Office VPN",
            Gateway = "https://vpn.example.test",
            Username = "alice",
        };
        editor.SelectedKind = editor.KindOptions.Single(option =>
            option.Kind == NetworkConnectionKind.AnyConnect);

        var prompt = Assert.IsType<NetworkCredentialOption>(
            editor.SelectedPasswordCredential);
        var profile = editor.CreateSaveRequest().Profile;

        Assert.Equal(NetworkCredentialOptionState.Prompt, prompt.State);
        Assert.Equal("Prompt on each connection", prompt.DisplayName);
        Assert.Null(Assert.IsType<NetworkConnectionConfiguration.AnyConnect>(
            profile.Configuration).PasswordSecret);
    }

    [Fact]
    public void OpenVpn_password_can_be_prompted_or_selected_from_the_vault()
    {
        var editor = new NetworkConnectionProfileEditorViewModel(Profile(
            new NetworkConnectionConfiguration.OpenVpn(new SecretRef("openvpn-profile"), "alice")), 1);
        Assert.Equal(NetworkCredentialOptionState.Prompt, editor.SelectedPasswordCredential?.State);
        Assert.Contains(editor.Credential.Targets, target => target.Target == NetworkCredentialTarget.OpenVpnPassword);
        Assert.Null(Assert.IsType<NetworkConnectionConfiguration.OpenVpn>(editor.CreateSaveRequest().Profile.Configuration).PasswordSecret);
        editor.PasswordSecretReference = "openvpn-password";
        var saved = Assert.IsType<NetworkConnectionConfiguration.OpenVpn>(editor.CreateSaveRequest().Profile.Configuration);
        Assert.Equal("alice", saved.Username);
        Assert.Equal(new SecretRef("openvpn-password"), saved.PasswordSecret);
    }

    [Fact]
    public void Policy_editor_keeps_selection_inside_the_available_connection_list()
    {
        var first = Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "first.example.test",
            1080));
        var second = new NetworkConnectionProfile(
            new NetworkConnectionId("second"),
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Second",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "second.example.test",
                8080));
        using var editor = new NetworkPolicyEditorViewModel(
            [first, second],
            new NetworkPolicy([first.Id, second.Id], first.Id, true, true));

        editor.Connections.Single(option => option.Id == first.Id).IsAvailable = false;

        Assert.Equal(second.Id, editor.SelectedConnection?.Id);
        Assert.True(editor.IsEnabled);
        var saved = editor.CreatePolicy();
        Assert.Equal([second.Id], saved.Connections);
        Assert.Equal(second.Id, saved.SelectedConnectionId);
        Assert.True(saved.KillSwitchEnabled);
    }

    [Fact]
    public void Enabling_a_policy_without_a_remembered_route_selects_the_first_available_connection()
    {
        var profile = Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
        using var editor = new NetworkPolicyEditorViewModel(
            [profile],
            new NetworkPolicy([profile.Id], null, false, false));

        editor.IsEnabled = true;

        Assert.Equal(profile.Id, editor.SelectedConnection?.Id);
        Assert.True(editor.CreatePolicy().IsEnabled);
    }

    [Fact]
    public async Task Application_editor_saves_profile_and_policy_with_catalog_revisions()
    {
        var storedProfile = Store(Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080)), 11);
        var storedSettings = Store(new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            new NetworkPolicy([ProxyId], ProxyId, false, true)), 12);
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty with
        {
            NetworkConnections = [storedProfile],
            ApplicationNetworkSettings = [storedSettings],
        });
        var vault = Vault();
        using var viewModel = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);
        var item = Assert.Single(viewModel.Profiles);
        await viewModel.BeginEditProfileAsync(item, CancellationToken.None);
        viewModel.ProfileEditor!.Name = "Office proxy updated";

        Assert.True(await viewModel.SaveProfileAsync(CancellationToken.None));
        Assert.Equal(11, fixture.Proxy.LastProfileRevision);
        Assert.Equal("Office proxy updated", fixture.Proxy.LastProfile?.Name);

        viewModel.Policy.IsEnabled = true;
        Assert.True(await viewModel.SavePolicyAsync(CancellationToken.None));
        Assert.Equal(12, fixture.Proxy.LastSettingsRevision);
        Assert.True(fixture.Proxy.LastSettings?.Policy.IsEnabled);
        Assert.True(fixture.Proxy.LastSettings?.Policy.KillSwitchEnabled);

        using var reloaded = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);
        Assert.True(reloaded.Policy.IsEnabled);
        Assert.True(reloaded.Policy.KillSwitchEnabled);
        Assert.Equal(ProxyId, reloaded.Policy.SelectedConnection?.Id);
    }

    [Fact]
    public async Task Application_policy_derives_available_connections_from_the_global_catalog()
    {
        var secondId = new NetworkConnectionId("second-proxy");
        var first = Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
        var second = Profile(
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "second.example.test",
                8080),
            secondId,
            "Second proxy");
        var storedSettings = Store(new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            new NetworkPolicy([first.Id], first.Id, false, true)), 4);
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty with
        {
            NetworkConnections = [Store(first, 2), Store(second, 3)],
            ApplicationNetworkSettings = [storedSettings],
        });
        var vault = Vault();
        using var viewModel = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);

        Assert.Equal(2, viewModel.Policy.AvailableConnections.Count);
        Assert.All(viewModel.Policy.Connections, option => Assert.True(option.IsAvailable));

        Assert.True(await viewModel.SavePolicyAsync(CancellationToken.None));
        Assert.Equal(
            [first.Id, second.Id],
            fixture.Proxy.LastSettings?.Policy.Connections);
    }

    [Fact]
    public async Task Storing_a_network_credential_keeps_only_its_reference_in_the_profile()
    {
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty);
        var vault = Vault();
        using var viewModel = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);
        viewModel.BeginCreateProfile();
        var editor = Assert.IsType<NetworkConnectionProfileEditorViewModel>(
            viewModel.ProfileEditor);
        editor.SelectedKind = editor.KindOptions.Single(option =>
            option.Kind == NetworkConnectionKind.WireGuard);
        editor.Credential.Label = "Office WireGuard";
        editor.Credential.Value = "private configuration";

        Assert.True(await viewModel.StoreCredentialAsync(CancellationToken.None));
        Assert.Empty(vault.Proxy.CreateRequests);
        var pending = Assert.IsType<NetworkCredentialOption>(
            editor.SelectedConfigurationCredential);
        Assert.Equal(NetworkCredentialOptionState.Pending, pending.State);
        Assert.Equal("Office WireGuard · Ready to store", pending.DisplayName);
        Assert.Contains(
            "Stored when the connection is saved",
            pending.Detail,
            StringComparison.Ordinal);
        Assert.True(await viewModel.SaveProfileAsync(CancellationToken.None));

        var request = Assert.Single(vault.Proxy.CreateRequests);
        Assert.Equal(SecretScopeKind.NetworkConnection, request.Scope.Kind);
        Assert.Equal(editor.Id.Value, request.Scope.OwnerId);
        Assert.Equal(editor.Id.Value, request.Purpose.TargetId);
        var saved = Assert.IsType<NetworkConnectionConfiguration.WireGuard>(
            fixture.Proxy.LastProfile?.Configuration);
        Assert.Equal(request.Reference, saved.ConfigurationSecret);
        Assert.Empty(editor.Credential.Value);
        Assert.DoesNotContain(
            "private configuration",
            saved.ConfigurationSecret.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_and_deleting_a_profile_replaces_then_removes_its_credentials()
    {
        var originalReference = new SecretRef("old-proxy-password");
        var storedProfile = Store(Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080,
            "alice",
            originalReference)), 7);
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty with
        {
            NetworkConnections = [storedProfile],
        });
        var vault = Vault();
        using var viewModel = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);
        await viewModel.BeginEditProfileAsync(
            Assert.Single(viewModel.Profiles),
            CancellationToken.None);
        var editor = Assert.IsType<NetworkConnectionProfileEditorViewModel>(
            viewModel.ProfileEditor);
        editor.Credential.Label = "Replacement proxy password";
        editor.Credential.Value = "replacement password";

        Assert.True(await viewModel.StoreCredentialAsync(CancellationToken.None));
        Assert.True(await viewModel.SaveProfileAsync(CancellationToken.None));

        var replacementReference = Assert.Single(vault.Proxy.CreateRequests).Reference;
        var updated = Assert.IsType<NetworkConnectionConfiguration.Proxy>(
            fixture.Proxy.LastProfile?.Configuration);
        Assert.Equal(replacementReference, updated.PasswordSecret);
        Assert.Contains(vault.Proxy.DeleteRequests, request =>
            request.Reference == originalReference);

        Assert.True(await viewModel.DeleteProfileAsync(
            Assert.Single(viewModel.Profiles),
            CancellationToken.None));
        Assert.Empty(fixture.Proxy.CurrentSnapshot.NetworkConnections);
        Assert.Contains(vault.Proxy.DeleteRequests, request =>
            request.Reference == replacementReference);
    }

    [Fact]
    public async Task A_failed_profile_save_removes_the_unreferenced_vault_credential()
    {
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty);
        fixture.Proxy.FailNextProfileSave = true;
        var vault = Vault();
        using var viewModel = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);
        viewModel.BeginCreateProfile();
        var editor = Assert.IsType<NetworkConnectionProfileEditorViewModel>(
            viewModel.ProfileEditor);
        editor.SelectedKind = editor.KindOptions.Single(option =>
            option.Kind == NetworkConnectionKind.WireGuard);
        editor.Credential.Label = "Office WireGuard";
        editor.Credential.Value = "private configuration";
        Assert.True(await viewModel.StoreCredentialAsync(CancellationToken.None));

        Assert.False(await viewModel.SaveProfileAsync(CancellationToken.None));

        var created = Assert.Single(vault.Proxy.CreateRequests);
        Assert.Equal(created.Reference, Assert.Single(vault.Proxy.DeleteRequests).Reference);
        Assert.NotNull(viewModel.ProfileEditor);

        Assert.True(await viewModel.SaveProfileAsync(CancellationToken.None));
        Assert.Equal(2, vault.Proxy.CreateRequests.Count);
        Assert.Equal(created.Reference, vault.Proxy.CreateRequests[1].Reference);
    }

    [Fact]
    public void A_bound_credential_without_metadata_is_presented_as_unavailable()
    {
        var editor = new NetworkConnectionProfileEditorViewModel(
            Profile(new NetworkConnectionConfiguration.WireGuard(
                new SecretRef("detached-import-reference"))),
            expectedRevision: 3);

        var selected = Assert.IsType<NetworkCredentialOption>(
            editor.SelectedConfigurationCredential);
        Assert.Equal(
            NetworkCredentialOptionState.MetadataUnavailable,
            selected.State);
        Assert.Equal("Credential metadata unavailable", selected.DisplayName);
        Assert.DoesNotContain(
            "detached-import-reference",
            selected.DisplayName,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_a_profile_lists_same_scope_credentials_and_selects_the_bound_label()
    {
        var bound = new SecretRef("wireguard-office");
        var spare = new SecretRef("wireguard-backup");
        var storedProfile = Store(Profile(
            new NetworkConnectionConfiguration.WireGuard(bound)), 7);
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty with
        {
            NetworkConnections = [storedProfile],
        });
        var vault = Vault();
        vault.Proxy.Metadata.AddRange(
        [
            Metadata(bound, "Office WireGuard", SecretKind.Other, ProxyId),
            Metadata(spare, "Backup WireGuard", SecretKind.Other, ProxyId),
            Metadata(
                new SecretRef("other-profile-config"),
                "Other connection",
                SecretKind.Other,
                new NetworkConnectionId("other-profile")),
            Metadata(
                new SecretRef("same-profile-password"),
                "Proxy password",
                SecretKind.Password,
                ProxyId),
        ]);
        using var viewModel = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);

        await viewModel.BeginEditProfileAsync(
            Assert.Single(viewModel.Profiles),
            CancellationToken.None);

        var request = Assert.Single(vault.Proxy.ListRequests);
        Assert.Equal(SecretScopeKind.NetworkConnection, request.Scope?.Kind);
        Assert.Equal(ProxyId.Value, request.Scope?.OwnerId);
        Assert.Equal(SecretUseKind.UserManagement, request.Purpose.Kind);
        Assert.Equal(ProxyId.Value, request.Purpose.TargetId);
        var editor = Assert.IsType<NetworkConnectionProfileEditorViewModel>(
            viewModel.ProfileEditor);
        Assert.Equal(2, editor.ConfigurationCredentialOptions.Count);
        Assert.Contains(editor.ConfigurationCredentialOptions, option =>
            option.Reference == spare && option.DisplayName == "Backup WireGuard · Configuration");
        Assert.DoesNotContain(editor.ConfigurationCredentialOptions, option =>
            option.Label is "Other connection" or "Proxy password");
        var selected = Assert.IsType<NetworkCredentialOption>(
            editor.SelectedConfigurationCredential);
        Assert.Equal(bound, selected.Reference);
        Assert.Equal("Office WireGuard · Configuration", selected.DisplayName);
        Assert.Contains("OS vault", selected.Detail, StringComparison.Ordinal);
        Assert.Contains("Updated", selected.Detail, StringComparison.Ordinal);

        editor.SelectedConfigurationCredential = editor.ConfigurationCredentialOptions
            .Single(option => option.Reference == spare);
        var saved = Assert.IsType<NetworkConnectionConfiguration.WireGuard>(
            editor.CreateSaveRequest().Profile.Configuration);
        Assert.Equal(spare, saved.ConfigurationSecret);
    }

    [Fact]
    public async Task Missing_bound_metadata_is_explicit_after_the_vault_list_completes()
    {
        var storedProfile = Store(Profile(
            new NetworkConnectionConfiguration.WireGuard(
                new SecretRef("missing-wireguard"))), 7);
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty with
        {
            NetworkConnections = [storedProfile],
        });
        var vault = Vault();
        using var viewModel = new NetworkSettingsViewModel(fixture.Catalog, vault.Vault);

        await viewModel.BeginEditProfileAsync(
            Assert.Single(viewModel.Profiles),
            CancellationToken.None);

        var selected = Assert.IsType<NetworkCredentialOption>(
            viewModel.ProfileEditor?.SelectedConfigurationCredential);
        Assert.Equal(NetworkCredentialOptionState.Unavailable, selected.State);
        Assert.Equal("Credential unavailable", selected.DisplayName);
        Assert.Contains("not found", selected.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Testing_a_draft_uses_a_temporary_host_route_and_cleans_pending_credentials()
    {
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty);
        var vault = Vault();
        var runtime = new RecordingNetworkRuntime(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Connected,
            WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:49152")),
            ProxyId));
        using var viewModel = new NetworkSettingsViewModel(
            fixture.Catalog,
            vault.Vault,
            runtime);
        viewModel.BeginCreateProfile();
        var editor = Assert.IsType<NetworkConnectionProfileEditorViewModel>(
            viewModel.ProfileEditor);
        editor.SelectedKind = editor.KindOptions.Single(option =>
            option.Kind == NetworkConnectionKind.WireGuard);
        editor.Credential.Label = "Office WireGuard";
        editor.Credential.Value = "private configuration";
        Assert.True(await viewModel.StoreCredentialAsync(CancellationToken.None));

        Assert.True(await viewModel.TestProfileAsync(CancellationToken.None));

        var request = Assert.Single(runtime.Requests);
        Assert.IsType<WorkspaceNetworkPlacement.HostPlacement>(request.Placement);
        Assert.True(request.InitialPolicy.Policy.IsEnabled);
        Assert.False(request.InitialPolicy.Policy.KillSwitchEnabled);
        Assert.Equal(editor.Id, request.InitialPolicy.Policy.SelectedConnectionId);
        var tested = Assert.Single(request.InitialPolicy.Connections);
        var configuration = Assert.IsType<NetworkConnectionConfiguration.WireGuard>(
            tested.Configuration);
        Assert.Equal(
            Assert.Single(vault.Proxy.CreateRequests).Reference,
            configuration.ConfigurationSecret);
        Assert.Equal(
            configuration.ConfigurationSecret,
            Assert.Single(vault.Proxy.DeleteRequests).Reference);
        Assert.True(runtime.LastSession?.IsDisposed);
        Assert.Null(fixture.Proxy.LastProfile);
        Assert.Equal("Host provider ready", viewModel.ProfileTestStatus);
        Assert.Equal(
            "The host VPN session and local route are ready. Isolated workspace attachment is verified when the connection is selected in a workspace.",
            viewModel.ProfileTestDetail);
        Assert.False(viewModel.ProfileTestHasError);

        Assert.True(await viewModel.SaveProfileAsync(CancellationToken.None));
        Assert.Equal(2, vault.Proxy.CreateRequests.Count);
    }

    [Fact]
    public async Task Testing_an_invalid_draft_does_not_open_the_runtime()
    {
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty);
        var vault = Vault();
        var runtime = new RecordingNetworkRuntime(WorkspaceNetworkSnapshot.Direct);
        using var viewModel = new NetworkSettingsViewModel(
            fixture.Catalog,
            vault.Vault,
            runtime);
        viewModel.BeginCreateProfile();

        Assert.False(await viewModel.TestProfileAsync(CancellationToken.None));

        Assert.Empty(runtime.Requests);
        Assert.Equal("Validation failed", viewModel.ProfileTestStatus);
        Assert.True(viewModel.ProfileTestHasError);
    }

    [Fact]
    public async Task Testing_reports_the_runtime_failure_and_disposes_the_route()
    {
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty);
        var vault = Vault();
        var runtime = new RecordingNetworkRuntime(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Failed,
            WorkspaceNetworkEgress.Direct,
            ProxyId,
            new NetworkConnectionError(
                NetworkConnectionErrorCode.ConnectionFailed,
                "test_route_failed",
                "The gateway rejected the connection.",
                retryable: true)));
        using var viewModel = new NetworkSettingsViewModel(
            fixture.Catalog,
            vault.Vault,
            runtime);
        viewModel.BeginCreateProfile();
        var editor = Assert.IsType<NetworkConnectionProfileEditorViewModel>(
            viewModel.ProfileEditor);
        editor.Name = "Office HTTP proxy";
        editor.Host = "proxy.example.test";
        editor.Port = "8080";

        Assert.False(await viewModel.TestProfileAsync(CancellationToken.None));

        Assert.Equal("Test failed", viewModel.ProfileTestStatus);
        Assert.Equal("The gateway rejected the connection.", viewModel.ProfileTestDetail);
        Assert.True(viewModel.ProfileTestHasError);
        Assert.True(runtime.LastSession?.IsDisposed);
    }

    [Fact]
    public void Workspace_editor_stores_a_complete_override_or_inherits_the_application_policy()
    {
        var profile = Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
        var second = Profile(
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "second.example.test",
                8080),
            new NetworkConnectionId("second-proxy"),
            "Second proxy");
        var appSettings = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            new NetworkPolicy([profile.Id], profile.Id, true, true));
        using var editor = new WorkspaceEditorViewModel(
            Workspace(networkOverride: null),
            4,
            [],
            [],
            [],
            fileProviders: [],
            networkConnections: [profile, second],
            applicationNetworkSettings: appSettings);

        Assert.False(editor.OverridesNetworkSettings);
        Assert.Null(editor.CreateSaveRequest().Definition.NetworkOverride);

        editor.OverridesNetworkSettings = true;
        var saved = Assert.IsType<NetworkPolicy>(
            editor.CreateSaveRequest().Definition.NetworkOverride);
        Assert.Equal([profile.Id, second.Id], saved.Connections);
        Assert.Equal(profile.Id, saved.SelectedConnectionId);
        Assert.True(saved.IsEnabled);
        Assert.True(saved.KillSwitchEnabled);
    }

    [Fact]
    public void Workspace_editor_preserves_an_explicit_connection_subset()
    {
        var first = Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
        var second = Profile(
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "second.example.test",
                8080),
            new NetworkConnectionId("second-proxy"),
            "Second proxy");
        var workspacePolicy = new NetworkPolicy([second.Id], second.Id, true, true);
        using var editor = new WorkspaceEditorViewModel(
            Workspace(workspacePolicy),
            4,
            [],
            [],
            [],
            fileProviders: [],
            networkConnections: [first, second],
            applicationNetworkSettings: ApplicationNetworkSettings.Default);

        var saved = Assert.IsType<NetworkPolicy>(
            editor.CreateSaveRequest().Definition.NetworkOverride);
        Assert.Equal([second.Id], saved.Connections);
        Assert.Equal(second.Id, saved.SelectedConnectionId);
    }

    [Fact]
    public void Open_workspace_override_editor_refreshes_when_a_global_profile_is_added()
    {
        var first = Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
        var second = Profile(
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Http,
                "second.example.test",
                8080),
            new NetworkConnectionId("second-proxy"),
            "Second proxy");
        var workspace = Workspace(new NetworkPolicy(
            [first.Id],
            first.Id,
            isEnabled: true,
            killSwitchEnabled: true));
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty with
        {
            NetworkConnections = [Store(first, 1)],
            Workspaces = [Store(workspace, 2)],
        });
        using var settings = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(settings.TryBeginEdit(workspace.Id, out _, out _));

        var updated = fixture.Proxy.CurrentSnapshot with
        {
            NetworkConnections = [Store(first, 1), Store(second, 1)],
        };
        fixture.Proxy.CurrentSnapshot = updated;
        settings.ApplyCatalog(updated);

        Assert.Equal(2, settings.Editor!.NetworkPolicy.Connections.Count);
        Assert.True(settings.Editor.NetworkPolicy.Connections
            .Single(option => option.Id == first.Id).IsAvailable);
        Assert.False(settings.Editor.NetworkPolicy.Connections
            .Single(option => option.Id == second.Id).IsAvailable);
        Assert.Equal(first.Id, settings.Editor.NetworkPolicy.SelectedConnection?.Id);
    }

    [Fact]
    public async Task Workspace_settings_save_and_reload_a_complete_override_then_restore_inheritance()
    {
        var profile = Profile(new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
        var appSettings = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            new NetworkPolicy([profile.Id], profile.Id, true, true));
        var workspace = Workspace(networkOverride: null);
        var fixture = Catalog(DefinitionCatalogSnapshot.Empty with
        {
            NetworkConnections = [Store(profile, 2)],
            ApplicationNetworkSettings = [Store(appSettings, 3)],
            Workspaces = [Store(workspace, 4)],
        });
        using var settings = new WorkspaceSettingsViewModel(fixture.Catalog);
        Assert.True(settings.TryBeginEdit(workspace.Id, out _, out _));
        settings.Editor!.OverridesNetworkSettings = true;

        Assert.True((await settings.SaveAsync(CancellationToken.None)).IsSuccess);
        Assert.True(settings.TryBeginEdit(workspace.Id, out _, out _));
        Assert.True(settings.Editor!.OverridesNetworkSettings);
        Assert.Equal(profile.Id, settings.Editor.NetworkPolicy.SelectedConnection?.Id);
        Assert.True(settings.Editor.NetworkPolicy.IsEnabled);
        Assert.True(settings.Editor.NetworkPolicy.KillSwitchEnabled);

        settings.Editor.OverridesNetworkSettings = false;
        Assert.True((await settings.SaveAsync(CancellationToken.None)).IsSuccess);
        Assert.True(settings.TryBeginEdit(workspace.Id, out _, out _));
        Assert.False(settings.Editor!.OverridesNetworkSettings);
        Assert.Null(settings.Editor.CreateSaveRequest().Definition.NetworkOverride);
    }

    private static NetworkConnectionProfile Profile(
        NetworkConnectionConfiguration configuration,
        NetworkConnectionId? id = null,
        string name = "Office proxy") => new(
        id ?? ProxyId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        name,
        configuration);

    private static WorkspaceDefinition Workspace(NetworkPolicy? networkOverride) => new(
        new WorkspaceId("network-workspace"),
        WorkspaceDefinition.CurrentSchemaVersion,
        "Network workspace",
        null,
        null,
        [],
        networkOverride: networkOverride);

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static SecretMetadata Metadata(
        SecretRef reference,
        string label,
        SecretKind kind,
        NetworkConnectionId ownerId) => new(
        reference,
        label,
        kind,
        new SecretScope(SecretScopeKind.NetworkConnection, ownerId.Value),
        SecretVaultPersistenceKind.OsProtectedPersistent,
        DateTimeOffset.UnixEpoch,
        new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.Zero));

    private static CatalogFixture Catalog(DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var proxy = (RecordingCatalogProxy)(object)catalog;
        proxy.CurrentSnapshot = snapshot;
        return new(catalog, proxy);
    }

    private static VaultFixture Vault()
    {
        var vault = DispatchProxy.Create<ISecretVault, RecordingVaultProxy>();
        return new(vault, (RecordingVaultProxy)(object)vault);
    }

    private sealed record CatalogFixture(
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Proxy);

    private sealed record VaultFixture(
        ISecretVault Vault,
        RecordingVaultProxy Proxy);

    private sealed class RecordingNetworkRuntime(
        WorkspaceNetworkSnapshot snapshot) : IWorkspaceNetworkRuntime
    {
        public List<WorkspaceNetworkOpenRequest> Requests { get; } = [];

        public RecordingNetworkSession? LastSession { get; private set; }

        public ValueTask<IWorkspaceNetworkSession> OpenAsync(
            WorkspaceNetworkOpenRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            progress?.Report(new NetworkConnectionProgress("Checking the route…"));
            LastSession = new RecordingNetworkSession(snapshot);
            return ValueTask.FromResult<IWorkspaceNetworkSession>(LastSession);
        }
    }

    private sealed class RecordingNetworkSession(
        WorkspaceNetworkSnapshot snapshot) : IWorkspaceNetworkSession
    {
        public WorkspaceNetworkSnapshot Snapshot { get; } = snapshot;

        public bool IsDisposed { get; private set; }

        public event EventHandler<WorkspaceNetworkSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>> ApplyAsync(
            WorkspaceNetworkPolicyUpdate update,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    public class RecordingVaultProxy : DispatchProxy
    {
        public List<CreateSecretRequest> CreateRequests { get; } = [];

        public List<DeleteSecretRequest> DeleteRequests { get; } = [];

        public List<ListSecretMetadataRequest> ListRequests { get; } = [];

        public List<SecretMetadata> Metadata { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            args ??= [];
            if (targetMethod?.Name == nameof(ISecretVault.CreateAsync)
                && args is
                [
                    CreateSecretRequest request,
                    SecretMaterial,
                    CancellationToken,
                ])
            {
                CreateRequests.Add(request);
                var metadata = new SecretMetadata(
                    request.Reference,
                    request.Label,
                    request.Kind,
                    request.Scope,
                    SecretVaultPersistenceKind.OsProtectedPersistent,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch);
                return ValueTask.FromResult(
                    SecretVaultResult<SecretMetadata>.Succeed(metadata));
            }

            if (targetMethod?.Name == nameof(ISecretVault.DeleteAsync)
                && args is
                [
                    DeleteSecretRequest deleteRequest,
                    CancellationToken,
                ])
            {
                DeleteRequests.Add(deleteRequest);
                return ValueTask.FromResult(SecretVaultResult<Unit>.Succeed(Unit.Value));
            }

            if (targetMethod?.Name == nameof(ISecretVault.ListMetadataAsync)
                && args is
                [
                    ListSecretMetadataRequest listRequest,
                    CancellationToken,
                ])
            {
                ListRequests.Add(listRequest);
                return ValueTask.FromResult(
                    SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed(
                        [.. Metadata]));
            }

            return targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.OsProtectedPersistent,
                    SecretVaultCapabilities.All,
                    "test",
                    "test_available",
                    "Test credential vault is available."),
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }
    }

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public NetworkConnectionProfile? LastProfile { get; private set; }

        public long? LastProfileRevision { get; private set; }

        public ApplicationNetworkSettings? LastSettings { get; private set; }

        public long? LastSettingsRevision { get; private set; }

        public WorkspaceDefinition? LastWorkspace { get; private set; }

        public long? LastWorkspaceRevision { get; private set; }

        public DefinitionKey? LastDeletedKey { get; private set; }

        public bool FailNextProfileSave { get; set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) => targetMethod?.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveNetworkConnectionAsync) => SaveProfile(args!),
                nameof(IDefinitionCatalog.SaveApplicationNetworkSettingsAsync) =>
                    SaveSettings(args!),
                nameof(IDefinitionCatalog.SaveWorkspaceAsync) => SaveWorkspace(args!),
                nameof(IDefinitionCatalog.DeleteAsync) => Delete(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private ValueTask<DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>>>
            SaveProfile(object?[] args)
        {
            if (FailNextProfileSave)
            {
                FailNextProfileSave = false;
                return ValueTask.FromResult(
                    DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>>.Failure(
                        new DefinitionStoreError(
                            DefinitionStoreErrorCode.RevisionConflict,
                            "The network connection changed before it could be saved.")));
            }

            LastProfile = Assert.IsType<NetworkConnectionProfile>(args[0]);
            LastProfileRevision = args[1] as long?;
            var stored = Store(LastProfile, (LastProfileRevision ?? 0) + 1);
            CurrentSnapshot = CurrentSnapshot with
            {
                NetworkConnections =
                [
                    .. CurrentSnapshot.NetworkConnections.Where(item =>
                        item.Value.Id != LastProfile.Id),
                    stored,
                ],
            };
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>>.Success(stored));
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<ApplicationNetworkSettings>>>
            SaveSettings(object?[] args)
        {
            LastSettings = Assert.IsType<ApplicationNetworkSettings>(args[0]);
            LastSettingsRevision = Assert.IsType<long>(args[1]);
            var stored = Store(LastSettings, LastSettingsRevision.Value + 1);
            CurrentSnapshot = CurrentSnapshot with
            {
                ApplicationNetworkSettings = [stored],
            };
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<ApplicationNetworkSettings>>.Success(stored));
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
            SaveWorkspace(object?[] args)
        {
            LastWorkspace = Assert.IsType<WorkspaceDefinition>(args[0]);
            LastWorkspaceRevision = args[1] as long?;
            var stored = Store(LastWorkspace, (LastWorkspaceRevision ?? 0) + 1);
            CurrentSnapshot = CurrentSnapshot with
            {
                Workspaces =
                [
                    .. CurrentSnapshot.Workspaces.Where(item =>
                        item.Value.Id != LastWorkspace.Id),
                    stored,
                ],
            };
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(stored));
        }

        private ValueTask<DefinitionStoreResult<Unit>> Delete(object?[] args)
        {
            LastDeletedKey = Assert.IsType<DefinitionKey>(args[0]);
            CurrentSnapshot = CurrentSnapshot with
            {
                NetworkConnections =
                [
                    .. CurrentSnapshot.NetworkConnections.Where(item =>
                        item.Value.Key != LastDeletedKey),
                ],
            };
            return ValueTask.FromResult(DefinitionStoreResult<Unit>.Success(Unit.Value));
        }
    }
}
