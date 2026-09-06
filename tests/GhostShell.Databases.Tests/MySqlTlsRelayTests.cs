using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MySqlConnector;

namespace GhostShell.Databases.Tests;

public sealed class MySqlTlsRelayTests
{
    [Theory]
    [InlineData("db.internal", true, false, true, false, false)]
    [InlineData("wrong.internal", true, false, false, false, false)]
    [InlineData("db.internal", false, false, false, false, false)]
    [InlineData("db.internal", true, true, false, false, false)]
    [InlineData("db.internal", true, false, false, true, false)]
    [InlineData("*.internal", true, false, true, false, false)]
    [InlineData("db.internal", true, false, false, false, true)]
    public async Task VerifyFull_requires_logical_hostname_trust_and_validity_before_sending_credentials(
        string certificateHost, bool trustCertificate, bool expired, bool expectedAuthenticated,
        bool wrongAuthority, bool clientAuthenticationOnly)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={certificateHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(certificateHost);
        request.CertificateExtensions.Add(names.Build());
        var purposes = new OidCollection
        {
            new(clientAuthenticationOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1"),
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
        var rootPath = Path.Combine(Path.GetTempPath(), $"ghostshell-mysql-ca-{Guid.NewGuid():N}.pem");
        using var otherKey = RSA.Create(2048);
        var otherRequest = new CertificateRequest("CN=another-authority", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var otherCertificate = otherRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await File.WriteAllTextAsync(rootPath, (wrongAuthority ? otherCertificate : certificate).ExportCertificatePem());
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var receivedCredentials = ServeHandshakeAsync(listener, certificate, lifetime.Token);
            var options = new MySqlConnectionStringBuilder
            {
                Server = "db.internal",
                UserID = "test",
                Password = "test-only-password",
                SslMode = MySqlSslMode.VerifyFull,
                SslCa = trustCertificate ? rootPath : string.Empty,
                Pooling = false,
                ConnectionTimeout = 3,
            };
            var driver = BuiltInDatabaseDrivers.All.Single(item => item.Descriptor.Id == "mysql");
            await using var connection = driver.CreateRoutedConnection(options.ConnectionString,
                "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            // The fixture closes after the TLS-authenticated MySQL response;
            // it never offers a database session or processes a query.
            await Assert.ThrowsAsync<MySqlException>(() => connection.OpenAsync(lifetime.Token));
            Assert.Equal(expectedAuthenticated, await receivedCredentials);
        }
        finally
        {
            File.Delete(rootPath);
        }
    }

    private static async Task<bool> ServeHandshakeAsync(
        TcpListener listener, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = peer.GetStream();
        // MySQL protocol 10, Protocol41 + SSL + SecureConnection + PluginAuth.
        using var hello = new MemoryStream();
        using (var writer = new BinaryWriter(hello, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)10);
            writer.Write("8.0.36\0"u8);
            writer.Write(1);
            writer.Write("12345678"u8);
            writer.Write((byte)0);
            writer.Write((ushort)0x8A01);
            writer.Write((byte)45);
            writer.Write((ushort)2);
            writer.Write((ushort)8);
            writer.Write((byte)21);
            writer.Write(new byte[10]);
            writer.Write("abcdefghijkl\0mysql_native_password\0"u8);
        }
        await stream.WriteAsync(new byte[] { (byte)hello.Length, 0, 0, 0 }, cancellationToken);
        await stream.WriteAsync(hello.ToArray(), cancellationToken);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var sslRequestLength = header[0] | (header[1] << 8) | (header[2] << 16);
        Assert.Equal(32, sslRequestLength);
        await stream.ReadExactlyAsync(new byte[sslRequestLength], cancellationToken);
        await using var tls = new SslStream(stream);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            }, cancellationToken);
            return await tls.ReadAsync(new byte[1], cancellationToken) != 0;
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            return false;
        }
    }
}
