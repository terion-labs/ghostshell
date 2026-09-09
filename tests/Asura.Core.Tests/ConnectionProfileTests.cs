using System.Text.Json;

namespace Asura.Core.Tests;

public sealed class ConnectionProfileTests
{
    [Fact]
    public void Ssh_profile_keeps_only_durable_configuration_and_secret_references()
    {
        var password = new SecretRef("vault-password-42");
        var profile = CreateSsh(new ConnectionAuthentication.Password(password));

        Assert.Equal(ConnectionKind.Ssh, profile.ConnectionKind);
        Assert.Equal(new DefinitionKey(DefinitionKind.Connection, "production"), profile.Key);
        Assert.Equal(password, Assert.IsType<ConnectionAuthentication.Password>(profile.Authentication).PasswordSecret);
        Assert.Equal("production", profile.Tags[0]);

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<ConnectionProfile>(json);

        Assert.NotNull(restored);
        Assert.Equal(profile.Id, restored.Id);
        Assert.Equal(ConnectionKind.Ssh, restored.ConnectionKind);
        Assert.Equal(password, Assert.IsType<ConnectionAuthentication.Password>(restored.Authentication).PasswordSecret);
    }

    [Fact]
    public void A_git_preferred_profile_defaults_to_the_git_panel_and_round_trips()
    {
        var profile = new ConnectionProfile(
            new ConnectionId("repo"),
            ConnectionProfile.CurrentSchemaVersion,
            "Asura repo",
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            new ConnectionStartup("/repo/asura"),
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable,
            preferredPanel: PanelKind.Git);

        Assert.Equal(PanelKind.Git, profile.PreferredPanel);
        Assert.Equal(PanelKind.Git, profile.PanelLaunchCapabilities.DefaultPanel);
        Assert.Equal(
            profile.Endpoint.PanelLaunchCapabilities.SupportedPanels,
            profile.PanelLaunchCapabilities.SupportedPanels);

        var restored = JsonSerializer.Deserialize<ConnectionProfile>(
            JsonSerializer.Serialize(profile));

        Assert.NotNull(restored);
        Assert.Equal(PanelKind.Git, restored.PreferredPanel);
        Assert.Equal("/repo/asura", restored.Startup.Directory);
    }

    [Fact]
    public void A_profile_without_a_preference_keeps_the_endpoint_default()
    {
        var profile = CreateSsh(new ConnectionAuthentication.SshAgent());

        Assert.Null(profile.PreferredPanel);
        Assert.Same(
            profile.Endpoint.PanelLaunchCapabilities,
            profile.PanelLaunchCapabilities);
    }

    [Fact]
    public void The_preferred_panel_must_be_one_the_endpoint_can_launch()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfile(
            new ConnectionId("container"),
            ConnectionProfile.CurrentSchemaVersion,
            "Container",
            new ConnectionEndpoint.Docker("api-dev"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable,
            preferredPanel: PanelKind.Git));
    }

    [Fact]
    public void A_host_connection_reference_round_trips_and_rejects_self_reference()
    {
        var hostId = new ConnectionId("bastion");
        var profile = CreateGitReference(new ConnectionId("repo"), hostId);

        Assert.Equal(hostId, profile.HostConnectionId);

        var restored = JsonSerializer.Deserialize<ConnectionProfile>(
            JsonSerializer.Serialize(profile));
        Assert.Equal(hostId, restored!.HostConnectionId);

        // A standalone profile round-trips a null reference — old rows carry
        // no hostConnectionId at all.
        var standalone = JsonSerializer.Deserialize<ConnectionProfile>(
            JsonSerializer.Serialize(CreateSsh(new ConnectionAuthentication.SshAgent())));
        Assert.Null(standalone!.HostConnectionId);

        Assert.Throws<ArgumentException>(() =>
            CreateGitReference(new ConnectionId("repo"), new ConnectionId("repo")));
    }

    [Fact]
    public void Resolving_a_host_reference_merges_the_host_endpoint_at_call_time()
    {
        var host = CreateSsh(new ConnectionAuthentication.SshAgent());
        var profile = CreateGitReference(new ConnectionId("repo"), host.Id);

        var resolved = profile.ResolveHostConnection(
            id => id == host.Id ? host : null);

        Assert.NotNull(resolved);
        Assert.Equal(profile.Id, resolved!.Id);
        Assert.Equal(profile.Name, resolved.Name);
        Assert.Equal(PanelKind.Git, resolved.PreferredPanel);
        Assert.Equal("/repo/app", resolved.Startup.Directory);
        Assert.Null(resolved.HostConnectionId);
        Assert.Equal(host.Endpoint, resolved.Endpoint);
        Assert.Equal(host.Authentication, resolved.Authentication);
        Assert.Equal(host.HostKeyPolicy, resolved.HostKeyPolicy);

        // A standalone profile resolves to itself; a missing or cyclic
        // reference resolves to null instead of throwing.
        Assert.Same(host, host.ResolveHostConnection(_ => null));
        Assert.Null(profile.ResolveHostConnection(_ => null));
        var first = CreateGitReference(new ConnectionId("first"), new ConnectionId("second"));
        var second = CreateGitReference(new ConnectionId("second"), new ConnectionId("first"));
        Assert.Null(first.ResolveHostConnection(
            id => id == first.Id ? first : id == second.Id ? second : null));
    }

    private static ConnectionProfile CreateGitReference(
        ConnectionId id,
        ConnectionId hostConnectionId) => new(
        id,
        ConnectionProfile.CurrentSchemaVersion,
        "Repo over SSH",
        ConnectionProfile.DelegatedSshEndpoint,
        new ConnectionAuthentication.None(),
        new ConnectionStartup("/repo/app"),
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.Strict,
        preferredPanel: PanelKind.Git,
        hostConnectionId: hostConnectionId);

    [Fact]
    public void Non_ssh_profile_rejects_ssh_authentication()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfile(
            new ConnectionId("local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local shell",
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable));
    }

    [Fact]
    public void Ssh_profile_requires_an_explicit_host_key_policy()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfile(
            new ConnectionId("server"),
            ConnectionProfile.CurrentSchemaVersion,
            "Server",
            new ConnectionEndpoint.Ssh("server.example"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(15)),
            SshHostKeyPolicy.NotApplicable));
    }

    [Fact]
    public void Startup_environment_rejects_duplicate_names()
    {
        var variables = new[]
        {
            new ConnectionEnvironmentVariable("REGION", new ConnectionEnvironmentValue.PlainText("west")),
            new ConnectionEnvironmentVariable("REGION", new ConnectionEnvironmentValue.PlainText("east")),
        };

        Assert.Throws<ArgumentException>(() => new ConnectionStartup(environment: variables));
    }

    [Fact]
    public void Startup_command_is_trimmed_and_rejects_multiline_input()
    {
        Assert.Equal("npm run dev", new ConnectionStartup(command: "  npm run dev  ").Command);
        Assert.Null(new ConnectionStartup(command: "   ").Command);
        Assert.Throws<ArgumentException>(() => new ConnectionStartup(command: "ls\nrm -rf /"));
    }

    [Fact]
    public void Enabled_keepalive_requires_positive_timing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ConnectionKeepAlive.EnabledEvery(TimeSpan.Zero));
    }

    private static ConnectionProfile CreateSsh(ConnectionAuthentication authentication) =>
        new(
            new ConnectionId("production"),
            ConnectionProfile.CurrentSchemaVersion,
            "Production",
            new ConnectionEndpoint.Ssh("prod.example", username: "deploy"),
            authentication,
            new ConnectionStartup(
                "/srv/app",
                [new("REGION", new ConnectionEnvironmentValue.PlainText("us-east"))]),
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(30)),
            SshHostKeyPolicy.Strict,
            ["production"]);
}
