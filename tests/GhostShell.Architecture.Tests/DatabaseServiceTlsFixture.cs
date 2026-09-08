using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace GhostShell.Architecture.Tests;

// Terminates TLS, then observes one TDS byte. It deliberately does not answer
// TDS: the test can distinguish certificate acceptance from provider login.
internal sealed class DatabaseServiceTlsFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly RSA _key = RSA.Create(2048);
    private readonly Task _accept;
    public X509Certificate2 Certificate { get; }
    public Channel<(string? Name, bool ReceivedTds)> Handshakes { get; } = Channel.CreateUnbounded<(string?, bool)>();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public DatabaseServiceTlsFixture()
    {
        var request = new CertificateRequest("CN=tls.synthetic.invalid", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("tls.synthetic.invalid");
        request.CertificateExtensions.Add(names.Build());
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        var connections = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token), _stop.Token));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { await Task.WhenAll(connections); }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = new SslStream(client.GetStream()))
        {
            string? name = null;
            var receivedTds = false;
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, value) => { name = value; return Certificate; },
                    EnabledSslProtocols = SslProtocols.None,
                    ApplicationProtocols = [new SslApplicationProtocol("tds/8.0")],
                }, token);
                receivedTds = await stream.ReadAsync(new byte[1], token) == 1;
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException) { }
            finally { Handshakes.Writer.TryWrite((name, receivedTds)); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _accept;
        _listener.Stop();
        Certificate.Dispose();
        _key.Dispose();
        _stop.Dispose();
    }
}
