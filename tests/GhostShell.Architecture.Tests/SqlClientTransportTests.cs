using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;

namespace GhostShell.Architecture.Tests;

[Collection(SqlClientRouteCollection.Name)]
public sealed class SqlClientTransportTests
{
    private static readonly Assembly Provider = typeof(SqlConnection).Assembly;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyTlsAsync(bool wrongPin)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=tls.logical.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("tls.logical.invalid");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var otherKey = RSA.Create(2048);
        using var otherCertificate = new CertificateRequest("CN=other.invalid", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pinPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(pinPath, (wrongPin ? otherCertificate : certificate).Export(X509ContentType.Cert));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string? requestedName = null;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptSocketAsync(timeout.Token);
                using var stream = new SslStream(new NetworkStream(accepted, true));
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, name) => { requestedName = name; return certificate; },
                        EnabledSslProtocols = SslProtocols.None,
                    }, timeout.Token);
                }
                catch (AuthenticationException) when (wrongPin) { }
                catch (IOException) when (wrongPin) { }
            });
            var route = new SqlConnectionTcpTransport(async (host, requestedPort, parallel, preference, token) =>
            {
                Check(host == "tls.logical.invalid" && requestedPort == 1433, "TLS dial retains logical target");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, port, token); return socket; }
                catch { socket.Dispose(); throw; }
            }, timeout.Token);
            var handleType = Provider.GetType("Microsoft.Data.SqlClient.SNI.SNITCPHandle", throwOnError: true)!;
            var handle = Activator.CreateInstance(handleType,
                "tls.logical.invalid", 1433, Timer(3000), false, SqlConnectionIPAddressPreference.UsePlatformDefault,
                "tls.logical.invalid", null, true, "", pinPath, route);
            try
            {
                var status = Assert.IsType<uint>(handleType.GetMethod("EnableSsl")!.Invoke(handle, [1u]));
                Check(wrongPin ? status != 0 : status == 0, wrongPin ? "TLS rejects wrong server certificate pin" : "TLS accepts exact server certificate pin over route");
                await server.WaitAsync(timeout.Token);
                Check(requestedName == "tls.logical.invalid", "TLS SNI uses logical hostname, not loopback relay");
            }
            finally { handleType.GetMethod("Dispose")!.Invoke(handle, null); }
        }
        finally { File.Delete(pinPath); }
    }

    [Fact]
    public async Task VerifyLateSocketAsync()
    {
        var pending = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var transport = new SqlConnectionTcpTransport((_, _, _, _, token) => { observed = token; return pending.Task; }, CancellationToken.None);
        var method = typeof(SqlConnectionTcpTransport).GetMethod("Connect", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            method.Invoke(transport, ["late.synthetic.invalid", 1433, true, SqlConnectionIPAddressPreference.UsePlatformDefault, Timer(100)]);
            throw new InvalidOperationException("Transport deadline was ignored.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException) { }
        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(2) && observed.IsCancellationRequested, "route deadline bounds pending callback and cancels it");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var late = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await late.ConnectAsync(listener.LocalEndpoint);
        using var accepted = await listener.AcceptSocketAsync();
        pending.SetResult(late);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!late.SafeHandle.IsClosed)
        {
            await Task.Delay(10, deadline.Token);
        }
        Check(late.SafeHandle.IsClosed, "socket returned after deadline is disposed");
    }

    private static object Timer(long milliseconds) => Provider.GetType("Microsoft.Data.ProviderBase.TimeoutTimer", throwOnError: true)!
        .GetMethod("StartMillisecondsTimeout", PrivateStatic)!.Invoke(null, [milliseconds])!;

    private static void Check(bool condition, string message) => Assert.True(condition, message);
}
