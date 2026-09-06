using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Redis;
using StackExchange.Redis;

namespace GhostShell.Redis.Tests;

public sealed class RedisConnectionTunnelTests
{
    [Fact]
    public async Task Each_seed_and_discovered_endpoint_gets_its_own_reusable_relay()
    {
        var factory = new RecordingFactory(45000);
        var tunnel = new RedisConnectionTunnel(factory, Profile());
        var seeds = new[] { new DnsEndPoint("seed-a.internal", 6379), new DnsEndPoint("seed-b.internal", 6380) };
        var discovered = new DnsEndPoint("primary.internal", 6381);
        var first = await tunnel.GetSocketConnectEndpointAsync(seeds[0], CancellationToken.None);
        var second = await tunnel.GetSocketConnectEndpointAsync(seeds[1], CancellationToken.None);
        var primary = await tunnel.GetSocketConnectEndpointAsync(discovered, CancellationToken.None);
        var reused = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => tunnel.GetSocketConnectEndpointAsync(discovered, CancellationToken.None).AsTask()));

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, primary);
        Assert.All(reused, endpoint => Assert.Equal(primary, endpoint));
        Assert.Equal(["seed-a.internal", "seed-b.internal", "primary.internal"], factory.Hosts, StringComparer.Ordinal);
        await tunnel.DisposeAsync();
        Assert.All(factory.Leases, lease => Assert.True(lease.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            tunnel.GetSocketConnectEndpointAsync(discovered, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("redis.internal", true)]
    [InlineData("wrong.internal", false)]
    public async Task Actual_Redis_TLS_uses_logical_name_and_rejects_a_wrong_certificate(
        string certificateHost,
        bool expectedAuthenticated)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={certificateHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(certificateHost);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var factory = new RecordingFactory(((IPEndPoint)listener.LocalEndpoint).Port - 1);
        await using var tunnel = new RedisConnectionTunnel(factory, Profile());
        string? serverName = null;
        var receivedRedisBytes = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
            await using var tls = new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) =>
                    {
                        serverName = name;
                        return certificate;
                    },
                }, lifetime.Token);
                return await tls.ReadAsync(new byte[1], lifetime.Token) != 0;
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException)
            {
                return false;
            }
        });
        var options = new ConfigurationOptions
        {
            Ssl = true,
            Tunnel = tunnel,
            ConnectTimeout = 1000,
            ConnectRetry = 0,
            AbortOnConnectFail = true,
            SslClientAuthenticationOptions = host => new SslClientAuthenticationOptions
            {
                TargetHost = host,
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    CustomTrustStore = { certificate },
                    RevocationMode = X509RevocationMode.NoCheck,
                },
            },
        };
        options.EndPoints.Add("redis.internal", 6379);
        try
        {
            await using var connection = await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(lifetime.Token);
        }
        catch (RedisConnectionException)
        {
            // This peer intentionally speaks only TLS, not a complete Redis server.
        }

        Assert.Equal(expectedAuthenticated, await receivedRedisBytes);
        Assert.Equal("redis.internal", serverName);
        Assert.Equal("redis.internal", Assert.Single(factory.Hosts));
    }

    private static ConnectionProfile Profile() => new(
        new ConnectionId("workspace"), ConnectionProfile.CurrentSchemaVersion, "Workspace",
        new ConnectionEndpoint.Local(), new ConnectionAuthentication.None(),
        ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.NotApplicable);

    private sealed class RecordingFactory(int initialPort) : IDatabaseTunnelFactory
    {
        public List<string> Hosts { get; } = [];
        public List<Lease> Leases { get; } = [];

        public ValueTask<IDatabaseTunnelLease> OpenAsync(ConnectionProfile connection, string targetHost,
            int targetPort, CancellationToken cancellationToken)
        {
            Hosts.Add(targetHost);
            var lease = new Lease(initialPort + Hosts.Count);
            Leases.Add(lease);
            return ValueTask.FromResult<IDatabaseTunnelLease>(lease);
        }
    }

    private sealed class Lease(int port) : IDatabaseTunnelLease
    {
        public int LocalPort => port;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
