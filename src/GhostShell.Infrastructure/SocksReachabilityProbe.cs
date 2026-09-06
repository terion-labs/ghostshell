using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GhostShell.Infrastructure;

internal interface ISocksReachabilityProbe
{
    ValueTask<SocksReachabilityResult> ProbeAsync(
        int socksPort,
        CancellationToken cancellationToken);
}

internal enum SocksReachabilityFailure
{
    None,
    ListenerUnavailable,
    SocksHandshakeRejected,
    DestinationRejected,
    TlsRejected,
    InvalidHttpResponse,
    TimedOut,
    TransportFailed,
}

internal readonly record struct SocksReachabilityResult(SocksReachabilityFailure Failure)
{
    public static SocksReachabilityResult Reachable { get; } =
        new(SocksReachabilityFailure.None);

    public bool IsReachable => Failure == SocksReachabilityFailure.None;
}

/// <summary>
/// Proves that a loopback SOCKS route can carry TLS and HTTP to a public peer.
/// All remote destinations are IP literals, so the probe cannot invoke the host resolver.
/// </summary>
internal sealed class SocksReachabilityProbe : ISocksReachabilityProbe
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);
    private static readonly IPAddress[] ProbeAddresses =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("1.0.0.1"),
    ];

    public async ValueTask<SocksReachabilityResult> ProbeAsync(
        int socksPort,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(socksPort);
        var lastFailure = SocksReachabilityFailure.TransportFailed;
        foreach (var address in ProbeAddresses)
        {
            var result = await ProbeAddressAsync(socksPort, address, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsReachable)
            {
                return result;
            }

            lastFailure = result.Failure;
            if (lastFailure is SocksReachabilityFailure.ListenerUnavailable
                or SocksReachabilityFailure.SocksHandshakeRejected)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(lastFailure);
    }

    private static async ValueTask<SocksReachabilityResult> ProbeAddressAsync(
        int socksPort,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(AttemptTimeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
            };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, socksPort, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return new(SocksReachabilityFailure.ListenerUnavailable);
            }

            var stream = client.GetStream();
            var socksFailure = await ConnectSocksAsync(stream, address, deadline.Token)
                .ConfigureAwait(false);
            if (socksFailure != SocksReachabilityFailure.None)
            {
                return new(socksFailure);
            }

            using var secured = new SslStream(stream, leaveInnerStreamOpen: true);
            try
            {
                await secured.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = address.ToString(),
                            EnabledSslProtocols = SslProtocols.None,
                            CertificateChainPolicy = new X509ChainPolicy
                            {
                                DisableCertificateDownloads = true,
                                RevocationMode = X509RevocationMode.NoCheck,
                            },
                        },
                        deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (AuthenticationException)
            {
                return new(SocksReachabilityFailure.TlsRejected);
            }

            var request = Encoding.ASCII.GetBytes(
                $"HEAD /cdn-cgi/trace HTTP/1.1\r\nHost: {address}\r\nConnection: close\r\n\r\n");
            await secured.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            await secured.FlushAsync(deadline.Token).ConfigureAwait(false);
            var prefix = new byte[9];
            await secured.ReadExactlyAsync(prefix, deadline.Token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(prefix).StartsWith(
                    "HTTP/1.",
                    StringComparison.Ordinal)
                ? SocksReachabilityResult.Reachable
                : new(SocksReachabilityFailure.InvalidHttpResponse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(SocksReachabilityFailure.TimedOut);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            return new(SocksReachabilityFailure.TransportFailed);
        }
    }

    private static async ValueTask<SocksReachabilityFailure> ConnectSocksAsync(
        Stream stream,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken)
            .ConfigureAwait(false);
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != 5 || greeting[1] != 0)
        {
            return SocksReachabilityFailure.SocksHandshakeRejected;
        }

        var addressBytes = address.GetAddressBytes();
        var request = new byte[addressBytes.Length + 6];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = address.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        addressBytes.CopyTo(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4 + addressBytes.Length), 443);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new byte[4];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 5 || response[1] != 0)
        {
            return response[0] == 5
                ? SocksReachabilityFailure.DestinationRejected
                : SocksReachabilityFailure.SocksHandshakeRejected;
        }

        var addressLength = response[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };
        if (addressLength <= 0)
        {
            return SocksReachabilityFailure.SocksHandshakeRejected;
        }

        var remainder = new byte[addressLength + 2];
        await stream.ReadExactlyAsync(remainder, cancellationToken).ConfigureAwait(false);
        return SocksReachabilityFailure.None;
    }

    private static async ValueTask<int> ReadDomainLengthAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        return length[0];
    }
}
