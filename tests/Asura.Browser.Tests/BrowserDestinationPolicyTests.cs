using System.Net;
using System.Net.Sockets;
using Asura.Application;

namespace Asura.Browser.Tests;

public sealed class BrowserDestinationPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void LocalRouteRejectsNonPublicLiteralAddresses(string value)
    {
        Assert.False(BrowserDestinationPolicy.IsPublicAddress(
            IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void LocalRouteAcceptsPublicLiteralAddresses(string value)
    {
        Assert.True(BrowserDestinationPolicy.IsPublicAddress(
            IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("https://api.localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    public void LocalRouteRejectsLiteralAndLocalhostNavigationStarts(
        string value)
    {
        Assert.False(BrowserDestinationPolicy.LocalSystem
            .AllowsNavigationStart(Address(value)));
    }

    [Fact]
    public async Task LocalRouteRequiresEveryResolvedAddressToBePublic()
    {
        var policy = BrowserDestinationPolicy.CreateLocal(
            static (_, _) => ValueTask.FromResult<IPAddress[]>(
            [
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("10.0.0.1"),
            ]));

        var allowed = await policy.AllowsResolvedAsync(
            Address("https://mixed.example.test/"),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task LocalRouteAllowsAHostnameWithOnlyPublicAnswers()
    {
        var policy = BrowserDestinationPolicy.CreateLocal(
            static (_, _) => ValueTask.FromResult<IPAddress[]>(
            [
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("2606:4700:4700::1111"),
            ]));

        var allowed = await policy.AllowsResolvedAsync(
            Address("https://public.example.test/"),
            CancellationToken.None);

        Assert.True(allowed);
    }

    [Fact]
    public async Task LocalRouteFailsClosedWhenResolutionFailsOrReturnsNothing()
    {
        var failed = BrowserDestinationPolicy.CreateLocal(
            static (_, _) => throw new SocketException());
        var empty = BrowserDestinationPolicy.CreateLocal(
            static (_, _) => ValueTask.FromResult(Array.Empty<IPAddress>()));
        var address = Address("https://unresolved.example.test/");

        Assert.False(await failed.AllowsResolvedAsync(
            address,
            CancellationToken.None));
        Assert.False(await empty.AllowsResolvedAsync(
            address,
            CancellationToken.None));
    }

    [Fact]
    public async Task LocalRouteResolutionHonorsCancellation()
    {
        var policy = BrowserDestinationPolicy.CreateLocal(
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Array.Empty<IPAddress>());
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.AllowsResolvedAsync(
                    Address("https://cancelled.example.test/"),
                    cancellation.Token)
                .AsTask());
    }

    [Fact(DisplayName = "content.browser.ssh-private-default-deny")]
    [Trait("SecurityCampaignCase", "content.browser.ssh-private-default-deny")]
    public async Task SshRouteDeniesPrivateDestinationsWithoutExactCapability()
    {
        string[] values =
        [
            "http://localhost/",
            "http://127.0.0.1/",
            "http://169.254.169.254/",
        ];

        foreach (var value in values)
        {
            var address = Address(value);
            Assert.False(BrowserDestinationPolicy.SshRouted
                .AllowsNavigationStart(address));
            Assert.False(await BrowserDestinationPolicy.SshRouted
                .AllowsResolvedAsync(address, CancellationToken.None));
        }
    }

    private static BrowserAddress Address(string value)
    {
        Assert.True(BrowserAddress.TryParse(value, out var address));
        return address;
    }
}
