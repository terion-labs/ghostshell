using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GhostShell.Databases;
using Npgsql;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseTlsRelayTests
{
    [Fact]
    public async Task ClickHouse_keeps_logical_SNI_and_rejects_untrusted_certificates()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=db.internal", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string? serverName = null;
        var receivedHttp = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(lifetime.Token);
            await using var tls = new SslStream(peer.GetStream());
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
        var driver = BuiltInDatabaseDrivers.All.Single(item => item.Descriptor.Id == "clickhouse");
        await using var connection = driver.CreateRoutedConnection(
            "Host=db.internal;Protocol=https;Port=8443;Compression=false", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.OpenAsync(lifetime.Token));
        Assert.False(await receivedHttp);
        Assert.Equal("db.internal", serverName);
    }

    [Theory]
    [InlineData("db.internal", true)]
    [InlineData("wrong.internal", false)]
    public async Task Postgres_VerifyFull_validates_the_logical_server_over_the_relay(
        string certificateHost, bool expectedAuthenticated)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={certificateHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(certificateHost);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var rootPath = Path.Combine(Path.GetTempPath(), $"ghostshell-test-ca-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(rootPath, certificate.ExportCertificatePem());
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            string? serverName = null;
            var receivedStartup = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(lifetime.Token);
                var stream = peer.GetStream();
                await stream.ReadExactlyAsync(new byte[8], lifetime.Token);
                await stream.WriteAsync(new byte[] { (byte)'S' }, lifetime.Token);
                await using var tls = new SslStream(stream);
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
            var driver = BuiltInDatabaseDrivers.All.Single(item => item.Descriptor.Id == "postgres");
            var options = new NpgsqlConnectionStringBuilder
            {
                Host = "db.internal",
                Username = "test",
                Database = "test",
                SslMode = SslMode.VerifyFull,
                RootCertificate = rootPath,
                Pooling = false,
                Timeout = 2,
            };
            await using var connection = driver.CreateRoutedConnection(options.ConnectionString,
                "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            await Assert.ThrowsAsync<NpgsqlException>(() => connection.OpenAsync(lifetime.Token));
            Assert.Equal(expectedAuthenticated, await receivedStartup);
            Assert.Equal("db.internal", serverName);
        }
        finally
        {
            File.Delete(rootPath);
        }
    }
}
