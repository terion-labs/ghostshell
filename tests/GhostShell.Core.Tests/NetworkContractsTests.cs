using System.Text.Json;

namespace GhostShell.Core.Tests;

public sealed class NetworkContractsTests
{
    private static readonly NetworkConnectionId ProxyId = new("office-proxy");

    [Fact]
    public void OpenVpn_authentication_is_optional_and_legacy_profiles_still_deserialize()
    {
        var legacy = JsonSerializer.Deserialize<NetworkConnectionConfiguration.OpenVpn>(
            """{"ConfigurationSecret":{"Value":"vpn-profile"}}""");
        Assert.NotNull(legacy);
        Assert.Null(legacy.Username);
        Assert.Null(legacy.PasswordSecret);
        var configuration = new NetworkConnectionConfiguration.OpenVpn(
            new SecretRef("vpn-profile"), " alice ", new SecretRef("vpn-password"));
        Assert.Equal("alice", configuration.Username);
        Assert.Equal(configuration, JsonSerializer.Deserialize<NetworkConnectionConfiguration.OpenVpn>(
            JsonSerializer.Serialize(configuration)));
        Assert.Throws<ArgumentException>(() => new NetworkConnectionConfiguration.OpenVpn(
            new SecretRef("vpn-profile"), passwordSecret: new SecretRef("vpn-password")));
    }

    [Theory]
    [InlineData(NetworkProxyProtocol.Socks5, "socks5://proxy.example.test:1080/")]
    [InlineData(NetworkProxyProtocol.Http, "http://proxy.example.test:8080/")]
    [InlineData(NetworkProxyProtocol.Https, "https://proxy.example.test:8443/")]
    public void Proxy_configuration_builds_the_expected_endpoint(
        NetworkProxyProtocol protocol,
        string expected)
    {
        var proxy = new NetworkConnectionConfiguration.Proxy(
            protocol,
            "proxy.example.test",
            protocol == NetworkProxyProtocol.Socks5
                ? 1080
                : protocol == NetworkProxyProtocol.Http ? 8080 : 8443);

        Assert.Equal(expected, proxy.Endpoint.AbsoluteUri);
        Assert.Equal(NetworkConnectionKind.Proxy, proxy.Kind);
    }

    [Fact]
    public void Proxy_credentials_are_references_and_require_a_username()
    {
        var secret = new SecretRef("proxy-password");
        var proxy = new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080,
            "alice",
            secret);

        Assert.Equal(secret, proxy.PasswordSecret);
        _ = Assert.Throws<ArgumentException>(() =>
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "proxy.example.test",
                1080,
                passwordSecret: secret));
    }

    [Fact]
    public void Vpn_configuration_keeps_private_material_behind_secret_references()
    {
        var profileSecret = new SecretRef("vpn-profile");
        var wireGuard = new NetworkConnectionConfiguration.WireGuard(profileSecret);
        var openVpn = new NetworkConnectionConfiguration.OpenVpn(profileSecret);
        var anyConnect = new NetworkConnectionConfiguration.AnyConnect(
            new Uri("https://vpn.example.test"),
            "alice",
            new SecretRef("vpn-password"));
        var tailscale = new NetworkConnectionConfiguration.Tailscale(
            "exit-node",
            new Uri("https://control.example.test"),
            new SecretRef("tailscale-auth-key"));

        Assert.Equal(NetworkConnectionKind.WireGuard, wireGuard.Kind);
        Assert.Equal(NetworkConnectionKind.OpenVpn, openVpn.Kind);
        Assert.Equal(NetworkConnectionKind.AnyConnect, anyConnect.Kind);
        Assert.Equal(NetworkConnectionKind.Tailscale, tailscale.Kind);
    }

    [Fact]
    public void Policy_snapshots_connections_and_retains_the_selection_when_disabled()
    {
        var source = new List<NetworkConnectionId> { ProxyId };
        var policy = new NetworkPolicy(
            source,
            ProxyId,
            isEnabled: false,
            killSwitchEnabled: true);
        source.Clear();

        Assert.Equal(ProxyId, Assert.Single(policy.Connections));
        Assert.Equal(ProxyId, policy.SelectedConnectionId);
        Assert.False(policy.IsEnabled);
        Assert.True(policy.KillSwitchEnabled);
    }

    [Fact]
    public void Enabled_policy_requires_a_selected_member()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new NetworkPolicy([ProxyId], null, isEnabled: true, killSwitchEnabled: false));
        _ = Assert.Throws<ArgumentException>(() =>
            new NetworkPolicy(
                [ProxyId],
                new NetworkConnectionId("other"),
                isEnabled: true,
                killSwitchEnabled: false));
    }

    [Fact]
    public void Workspace_override_replaces_the_complete_application_policy()
    {
        var secondId = new NetworkConnectionId("second-proxy");
        var application = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            new NetworkPolicy([ProxyId], ProxyId, true, true));
        var directWorkspace = Workspace(networkOverride: NetworkPolicy.Direct);
        var inheritedWorkspace = Workspace(networkOverride: null);
        var connections = new[]
        {
            Profile(ProxyId, "Office proxy"),
            Profile(secondId, "Second proxy"),
        };

        Assert.Same(
            NetworkPolicy.Direct,
            NetworkPolicyResolver.Resolve(application, directWorkspace, connections));
        var inherited = NetworkPolicyResolver.Resolve(
            application,
            inheritedWorkspace,
            connections);
        Assert.Equal([ProxyId, secondId], inherited.Connections);
        Assert.Equal(ProxyId, inherited.SelectedConnectionId);
        Assert.True(inherited.IsEnabled);
        Assert.True(inherited.KillSwitchEnabled);
    }

    [Fact]
    public void Application_policy_replaces_a_missing_selection_and_supports_the_full_catalog()
    {
        var connections = Enumerable.Range(0, 33)
            .Select(index =>
            {
                var id = new NetworkConnectionId($"proxy-{index}");
                return Profile(id, $"Proxy {index}");
            })
            .ToArray();
        var stalePolicy = new NetworkPolicy(
            [new NetworkConnectionId("removed-proxy")],
            new NetworkConnectionId("removed-proxy"),
            isEnabled: true,
            killSwitchEnabled: true);

        var resolved = NetworkPolicyResolver.ResolveApplication(stalePolicy, connections);

        Assert.Equal(33, resolved.Connections.Count);
        Assert.Equal(connections[0].Id, resolved.SelectedConnectionId);
        Assert.True(resolved.IsEnabled);
        Assert.True(resolved.KillSwitchEnabled);
    }

    [Fact]
    public void Application_policy_disables_when_no_global_connections_remain()
    {
        var staleId = new NetworkConnectionId("removed-proxy");
        var stalePolicy = new NetworkPolicy(
            [staleId],
            staleId,
            isEnabled: true,
            killSwitchEnabled: true);

        var resolved = NetworkPolicyResolver.ResolveApplication(stalePolicy, []);

        Assert.Empty(resolved.Connections);
        Assert.Null(resolved.SelectedConnectionId);
        Assert.False(resolved.IsEnabled);
        Assert.True(resolved.KillSwitchEnabled);
    }

    [Fact]
    public void Network_profiles_and_application_settings_round_trip()
    {
        var profile = new NetworkConnectionProfile(
            ProxyId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Office proxy",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Https,
                "proxy.example.test",
                8443,
                "alice",
                new SecretRef("proxy-password")));
        var settings = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            new NetworkPolicy([ProxyId], ProxyId, true, true));

        var restoredProfile = RoundTrip(profile);
        var restoredSettings = RoundTrip(settings);

        Assert.Equal(profile, restoredProfile);
        Assert.Equal(settings.Id, restoredSettings.Id);
        Assert.Equal(settings.Name, restoredSettings.Name);
        Assert.Equal(settings.Policy.Connections, restoredSettings.Policy.Connections);
        Assert.Equal(settings.Policy.SelectedConnectionId, restoredSettings.Policy.SelectedConnectionId);
        Assert.Equal(settings.Policy.IsEnabled, restoredSettings.Policy.IsEnabled);
        Assert.Equal(settings.Policy.KillSwitchEnabled, restoredSettings.Policy.KillSwitchEnabled);
    }

    private static WorkspaceDefinition Workspace(NetworkPolicy? networkOverride) => new(
        new WorkspaceId("network-workspace"),
        WorkspaceDefinition.CurrentSchemaVersion,
        "Network workspace",
        null,
        null,
        [],
        networkOverride: networkOverride);

    private static NetworkConnectionProfile Profile(NetworkConnectionId id, string name) => new(
        id,
        NetworkConnectionProfile.CurrentSchemaVersion,
        name,
        new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            $"{id.Value}.example.test",
            1080));

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
    }
}
