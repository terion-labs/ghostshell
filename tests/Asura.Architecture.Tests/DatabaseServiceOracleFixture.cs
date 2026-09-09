using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace Asura.Architecture.Tests;

// A listener-protocol fixture, not an Oracle database emulator. It receives the
// initial CONNECT, optionally redirects, and closes before authentication.
internal sealed class DatabaseServiceOracleFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _redirect;
    private readonly Task _accept;
    internal X509Certificate2? Certificate { get; }
    internal Channel<(string? Sni, bool Connect)> Observed { get; } = Channel.CreateUnbounded<(string?, bool)>();
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal DatabaseServiceOracleFixture(string? redirect = null, string? tlsHost = null)
    {
        _redirect = redirect;
        if (tlsHost is not null)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + tlsHost, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(tlsHost);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        }
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
        {
            string? sni = null;
            var connected = false;
            try
            {
                if (Certificate is { } certificate)
                {
                    using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, name) => { sni = name; return certificate; },
                        EnabledSslProtocols = SslProtocols.None,
                    }, token);
                    connected = await ReadConnectAsync(tls, token);
                }
                else
                {
                    var stream = client.GetStream();
                    connected = await ReadConnectAsync(stream, token);
                    if (connected && _redirect is { } target)
                    {
                        // Oracle's own thin driver defines REDIRECT as a type-5
                        // packet with a uint16 big-endian descriptor byte count.
                        // https://github.com/oracle/python-oracledb/blob/main/src/oracledb/impl/thin/messages/connect.pyx
                        var descriptor = Encoding.ASCII.GetBytes(target);
                        var packet = new byte[10 + descriptor.Length];
                        BinaryPrimitives.WriteUInt16BigEndian(packet, checked((ushort)packet.Length));
                        packet[4] = 5;
                        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), checked((ushort)descriptor.Length));
                        descriptor.CopyTo(packet, 10);
                        await stream.WriteAsync(packet, token);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or OperationCanceledException) { }
            finally { Observed.Writer.TryWrite((sni, connected)); }
        }
    }

    private static async Task<bool> ReadConnectAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (header[4] != 1 || length < 8) { return false; }
        var body = new byte[length - 8];
        await stream.ReadExactlyAsync(body, token);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _accept;
        _listener.Stop();
        Certificate?.Dispose();
        _stop.Dispose();
    }
}
