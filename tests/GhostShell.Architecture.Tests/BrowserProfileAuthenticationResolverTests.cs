using System.Reflection;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class BrowserProfileAuthenticationResolverTests
{
    [Fact]
    public async Task ExactChallengeResolvesThroughTheProfilesVaultScope()
    {
        var profileId = new BrowserProfileId("browser.auth");
        var reference = new SecretRef("browser-auth-password");
        var binding = Binding(
            profileId,
            new BrowserHttpAuthentication(
                "Internal.Example.",
                8443,
                "Operations",
                BrowserAuthenticationScheme.Digest,
                "operator",
                reference));
        var vault = Vault("correct horse battery staple");
        var resolver = new BrowserProfileAuthenticationResolver(vault.Vault);

        var credentials = await resolver.ResolveAsync(
            binding,
            new BrowserAuthenticationChallenge(
                IsProxy: false,
                Host: "INTERNAL.EXAMPLE.",
                Port: 8443,
                Realm: "Operations",
                Scheme: "DIGEST",
                OriginUrl: "https://internal.example:8443/private",
                RouteIdentity: "local"),
            CancellationToken.None);

        Assert.NotNull(credentials);
        Assert.Equal("operator", credentials.Username);
        Assert.Equal("correct horse battery staple", credentials.Password);
        var request = Assert.IsType<ResolveSecretRequest>(vault.Proxy.ResolveRequest);
        Assert.Equal(reference, request.Reference);
        Assert.Equal(SecretScopeKind.BrowserProfile, request.Scope.Kind);
        Assert.Equal(profileId.Value, request.Scope.OwnerId);
        Assert.Equal(SecretUseKind.BrowserProfileAuthentication, request.Purpose.Kind);
        Assert.Equal(profileId.Value, request.Purpose.TargetId);
    }

    [Fact]
    public async Task EveryNonExactOrProxyChallengeIsRejectedBeforeVaultAccess()
    {
        var profileId = new BrowserProfileId("browser.auth.reject");
        var binding = Binding(
            profileId,
            new BrowserHttpAuthentication(
                "internal.example",
                8443,
                "Operations",
                BrowserAuthenticationScheme.Basic,
                "operator",
                new SecretRef("browser-auth-reject-password")));
        var vault = Vault("must not be read");
        var resolver = new BrowserProfileAuthenticationResolver(vault.Vault);
        BrowserAuthenticationChallenge[] challenges =
        [
            new(true, "internal.example", 8443, "Operations", "basic"),
            new(false, "other.example", 8443, "Operations", "basic"),
            new(false, "internal.example", 443, "Operations", "basic"),
            new(false, "internal.example", 8443, "operations", "basic"),
            new(false, "internal.example", 8443, "Operations", "digest"),
        ];

        foreach (var challenge in challenges)
        {
            Assert.Null(await resolver.ResolveAsync(
                binding,
                challenge with { OriginUrl = "https://internal.example:8443/private", RouteIdentity = "local" },
                CancellationToken.None));
        }

        Assert.Equal(0, vault.Proxy.ResolveCount);
    }

    [Theory]
    [InlineData(null, "local")]
    [InlineData("http://internal.example:8443/private", "local")]
    [InlineData("https://other.example:8443/private", "local")]
    [InlineData("https://internal.example:443/private", "local")]
    [InlineData("https://internal.example:8443/private", null)]
    [InlineData("https://internal.example:8443/private", "ssh-other-route")]
    public async Task Origin_and_route_mismatches_never_resolve_the_password(string? origin, string? route)
    {
        var binding = Binding(new BrowserProfileId("origin-bound"),
            new BrowserHttpAuthentication("internal.example", 8443, null,
                BrowserAuthenticationScheme.Basic, "operator", new SecretRef("bound-password")));
        var vault = Vault("must not be read");
        var resolver = new BrowserProfileAuthenticationResolver(vault.Vault);

        Assert.Null(await resolver.ResolveAsync(binding,
            new(false, "internal.example", 8443, "realm", "basic", origin, route), CancellationToken.None));
        Assert.Equal(0, vault.Proxy.ResolveCount);
    }

    [Fact]
    public async Task Explicit_http_and_ssh_binding_is_supported_without_authorizing_local_or_https()
    {
        var binding = Binding(new BrowserProfileId("explicit-http"),
            new BrowserHttpAuthentication("internal.example", null, null,
                BrowserAuthenticationScheme.Basic, "operator", new SecretRef("bound-password"), "http", "ssh-selected"));
        var vault = Vault("password");
        var resolver = new BrowserProfileAuthenticationResolver(vault.Vault);
        var challenge = new BrowserAuthenticationChallenge(false, "internal.example", 80, "realm", "basic",
            "http://internal.example/private", "ssh-selected");

        Assert.NotNull(await resolver.ResolveAsync(binding, challenge, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(binding, challenge with { RouteIdentity = "local" }, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(binding, challenge with { OriginUrl = "https://internal.example", Port = 443 }, CancellationToken.None));
        Assert.Equal(1, vault.Proxy.ResolveCount);
    }

    private static BrowserProfileBinding Binding(
        BrowserProfileId profileId,
        BrowserHttpAuthentication authentication)
    {
        var definition = new BrowserProfileDefinition(
            profileId,
            BrowserProfileDefinition.CurrentSchemaVersion,
            "Authentication test",
            BrowserProfilePersistence.DurableMetadata,
            BrowserProfilePrivacyPolicy.Strict,
            authentication);
        return new BrowserProfileBinding(
            new BrowserProfileSelection(
                profileId,
                BrowserProfileKey.ForNamed(profileId.Value)),
            definition,
            revision: 1);
    }

    private static VaultFixture Vault(string password)
    {
        var vault = DispatchProxy.Create<ISecretVault, RecordingSecretVaultProxy>();
        var proxy = (RecordingSecretVaultProxy)(object)vault;
        proxy.Password = password;
        return new(vault, proxy);
    }

    private sealed record VaultFixture(
        ISecretVault Vault,
        RecordingSecretVaultProxy Proxy);

    public class RecordingSecretVaultProxy : DispatchProxy
    {
        public string Password { get; set; } = string.Empty;

        public ResolveSecretRequest? ResolveRequest { get; private set; }

        public int ResolveCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            args ??= [];
            if (targetMethod?.Name == nameof(ISecretVault.ResolveAsync)
                && args is [ResolveSecretRequest request, CancellationToken])
            {
                ResolveRequest = request;
                ResolveCount++;
                using var material = SecretMaterial.CopyFrom(
                    Encoding.UTF8.GetBytes(Password));
                return ValueTask.FromResult(
                    SecretVaultResult<SecretMaterial>.Succeed(material.Clone()));
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
}
