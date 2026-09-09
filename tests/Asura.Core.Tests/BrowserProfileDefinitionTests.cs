using System.Text.Json;

namespace Asura.Core.Tests;

public sealed class BrowserProfileDefinitionTests
{
    [Fact]
    public void DurableProfilePromisesEncryptedChromiumStateBetweenRuns()
    {
        var profile = new BrowserProfileDefinition(
            new BrowserProfileId("browser.work"),
            BrowserProfileDefinition.CurrentSchemaVersion,
            "Work",
            BrowserProfilePersistence.DurableMetadata,
            BrowserProfilePrivacyPolicy.Strict);

        Assert.Equal(
            BrowserWebContentRetention.EncryptedBetweenRuns,
            profile.Privacy.WebContent);
        Assert.Equal(
            BrowserPermissionRetention.DenyAll,
            profile.Privacy.Permissions);
        Assert.Equal(BrowserActivityRetention.DoNotRecord, profile.Privacy.History);
        Assert.Equal(BrowserActivityRetention.DoNotRecord, profile.Privacy.Downloads);
    }

    [Fact]
    public void HttpAuthenticationNormalizesAndBoundsTheExactChallengeTarget()
    {
        var authentication = new BrowserHttpAuthentication(
            "Example.COM.",
            8443,
            " Protected ",
            BrowserAuthenticationScheme.Basic,
            " operator ",
            new SecretRef("secret.browser.password"));

        Assert.Equal("example.com", authentication.Host);
        Assert.Equal("Protected", authentication.Realm);
        Assert.Equal("operator", authentication.Username);
        Assert.Throws<ArgumentException>(() => new BrowserHttpAuthentication(
            "example.com",
            null,
            new string('r', BrowserHttpAuthentication.MaximumRealmLength + 1),
            BrowserAuthenticationScheme.Basic,
            "operator",
            new SecretRef("secret.browser.password")));
    }

    [Fact]
    public void Legacy_http_authentication_defaults_to_https_and_local_without_losing_credentials()
    {
        var authentication = new BrowserHttpAuthentication("internal.example", 8443, "realm",
            BrowserAuthenticationScheme.Basic, "operator", new SecretRef("saved-password"));
        var json = JsonSerializer.Serialize(authentication);
        json = json.Replace(",\"OriginScheme\":\"https\"", string.Empty, StringComparison.Ordinal)
            .Replace(",\"RouteIdentity\":\"local\"", string.Empty, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<BrowserHttpAuthentication>(json);

        Assert.NotNull(restored);
        Assert.Equal("https", restored.OriginScheme);
        Assert.Equal("local", restored.RouteIdentity);
        Assert.Equal(authentication.PasswordSecret, restored.PasswordSecret);
        Assert.Equal(authentication.Port, restored.Port);
    }

    [Theory]
    [InlineData("ftp")]
    [InlineData("")]
    public void Unsupported_authentication_origin_transport_is_rejected(string scheme)
    {
        Assert.Throws<ArgumentException>(() => new BrowserHttpAuthentication("internal.example", null, null,
            BrowserAuthenticationScheme.Basic, "operator", new SecretRef("password"), scheme));
    }

    [Fact]
    public void Network_authentication_authority_tracks_destination_not_name_or_rotated_password()
    {
        var id = new NetworkConnectionId("vpn");
        var first = new NetworkConnectionProfile(id, 1, "Work",
            new NetworkConnectionConfiguration.AnyConnect(new Uri("https://vpn.example"), "alice", new SecretRef("old-password")));
        var renamedAndRotated = new NetworkConnectionProfile(id, 1, "Renamed",
            new NetworkConnectionConfiguration.AnyConnect(new Uri("https://vpn.example"), "alice", new SecretRef("new-password")));
        var editedDestination = new NetworkConnectionProfile(id, 1, "Work",
            new NetworkConnectionConfiguration.AnyConnect(new Uri("https://other.example"), "alice", new SecretRef("old-password")));

        Assert.Equal(BrowserHttpAuthentication.NetworkRouteIdentity(first), BrowserHttpAuthentication.NetworkRouteIdentity(renamedAndRotated));
        Assert.NotEqual(BrowserHttpAuthentication.NetworkRouteIdentity(first), BrowserHttpAuthentication.NetworkRouteIdentity(editedDestination), StringComparer.Ordinal);
    }
}
