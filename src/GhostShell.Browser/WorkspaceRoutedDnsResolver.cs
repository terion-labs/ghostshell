using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Resolves public agent-web destinations over DNS-over-TLS carried by the workspace route.
/// Resolver endpoints are IP literals so bootstrapping never consults the host resolver.
/// </summary>
internal sealed class WorkspaceRoutedDnsResolver
{
    private const int DnsOverTlsPort = 853;
    private const int MaximumDnsMessageBytes = 16 * 1024;
    private const string ResolverServerName = "cloudflare-dns.com";
    private static readonly string[] ResolverAddresses = ["1.1.1.1", "1.0.0.1"];
    private readonly IWorkspaceNetworkConnector _connector;
    private readonly Func<Stream, string, CancellationToken, ValueTask<Stream>> _secureStream;

    public WorkspaceRoutedDnsResolver(IWorkspaceNetworkConnector connector)
        : this(connector, AuthenticateAsync)
    {
    }

    internal WorkspaceRoutedDnsResolver(
        IWorkspaceNetworkConnector connector,
        Func<Stream, string, CancellationToken, ValueTask<Stream>> secureStream)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _secureStream = secureStream ?? throw new ArgumentNullException(nameof(secureStream));
    }

    public async ValueTask<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var canonicalHost = CanonicalizeHost(host);
        Exception? lastFailure = null;
        foreach (var resolverAddress in ResolverAddresses)
        {
            try
            {
                await using var transport = await _connector.ConnectTcpAsync(
                        resolverAddress,
                        DnsOverTlsPort,
                        cancellationToken)
                    .ConfigureAwait(false);
                await using var secured = await _secureStream(
                        transport,
                        ResolverServerName,
                        cancellationToken)
                    .ConfigureAwait(false);
                var ipv4 = await QueryAsync(
                        secured,
                        canonicalHost,
                        DnsRecordType.A,
                        cancellationToken)
                    .ConfigureAwait(false);
                var ipv6 = await QueryAsync(
                        secured,
                        canonicalHost,
                        DnsRecordType.Aaaa,
                        cancellationToken)
                    .ConfigureAwait(false);
                return [.. ipv4, .. ipv6];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                AuthenticationException
                or IOException
                or SocketException)
            {
                lastFailure = exception;
            }
        }

        throw new IOException(
            "DNS resolution through the workspace route failed.",
            lastFailure);
    }

    private static async ValueTask<Stream> AuthenticateAsync(
        Stream transport,
        string serverName,
        CancellationToken cancellationToken)
    {
        var secured = new SslStream(transport, leaveInnerStreamOpen: true);
        try
        {
            await secured.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = serverName,
                        EnabledSslProtocols = SslProtocols.None,
                        CertificateChainPolicy = new X509ChainPolicy
                        {
                            DisableCertificateDownloads = true,
                            RevocationMode = X509RevocationMode.NoCheck,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return secured;
        }
        catch
        {
            await secured.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<IReadOnlyList<IPAddress>> QueryAsync(
        Stream stream,
        string host,
        DnsRecordType recordType,
        CancellationToken cancellationToken)
    {
        var identifier = checked((ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
        var query = CreateQuery(host, recordType, identifier);
        var frame = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)query.Length));
        query.CopyTo(frame, 2);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        if (responseLength is < 12 or > MaximumDnsMessageBytes)
        {
            throw new IOException("The routed DNS resolver returned an invalid message length.");
        }

        var response = new byte[responseLength];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, host, recordType, identifier);
    }

    private static byte[] CreateQuery(
        string host,
        DnsRecordType recordType,
        ushort identifier)
    {
        var labels = host.Split('.');
        var nameLength = labels.Sum(static label => label.Length + 1) + 1;
        var query = new byte[12 + nameLength + 4];
        BinaryPrimitives.WriteUInt16BigEndian(query, identifier);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4), 1);
        var offset = 12;
        foreach (var label in labels)
        {
            query[offset++] = checked((byte)label.Length);
            Encoding.ASCII.GetBytes(label, query.AsSpan(offset));
            offset += label.Length;
        }

        query[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset), (ushort)recordType);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset + 2), 1);
        return query;
    }

    private static IReadOnlyList<IPAddress> ParseResponse(
        ReadOnlySpan<byte> response,
        string expectedHost,
        DnsRecordType expectedType,
        ushort expectedIdentifier)
    {
        if (response.Length < 12
            || BinaryPrimitives.ReadUInt16BigEndian(response) != expectedIdentifier)
        {
            throw new IOException("The routed DNS resolver returned an unrelated response.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((flags & 0x8000) == 0
            || (flags & 0x0200) != 0
            || (flags & 0x000F) != 0)
        {
            throw new IOException("The routed DNS resolver could not answer the query.");
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        if (questionCount != 1)
        {
            throw new IOException("The routed DNS resolver returned an invalid question.");
        }

        var offset = 12;
        var questionName = ReadName(response, ref offset);
        EnsureAvailable(response, offset, 4);
        var questionType = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
        var questionClass = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
        offset += 4;
        if (!string.Equals(questionName, expectedHost, StringComparison.OrdinalIgnoreCase)
            || questionType != (ushort)expectedType
            || questionClass != 1)
        {
            throw new IOException("The routed DNS resolver answered a different question.");
        }

        var answers = new List<DnsAnswer>(answerCount);
        for (var index = 0; index < answerCount; index++)
        {
            var owner = ReadName(response, ref offset);
            EnsureAvailable(response, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;
            EnsureAvailable(response, offset, dataLength);
            answers.Add(new DnsAnswer(owner, type, recordClass, offset, dataLength));
            offset += dataLength;
        }

        var acceptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            expectedHost,
        };
        for (var pass = 0; pass < answers.Count; pass++)
        {
            var changed = false;
            foreach (var answer in answers)
            {
                if (answer.RecordClass != 1
                    || answer.Type != (ushort)DnsRecordType.Cname
                    || !acceptedNames.Contains(answer.Owner))
                {
                    continue;
                }

                var nameOffset = answer.DataOffset;
                var target = ReadName(response, ref nameOffset);
                if (acceptedNames.Add(target))
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        var addresses = new List<IPAddress>();
        foreach (var answer in answers)
        {
            if (answer.RecordClass != 1
                || answer.Type != (ushort)expectedType
                || !acceptedNames.Contains(answer.Owner))
            {
                continue;
            }

            var expectedLength = expectedType == DnsRecordType.A ? 4 : 16;
            if (answer.DataLength == expectedLength)
            {
                addresses.Add(new IPAddress(
                    response.Slice(answer.DataOffset, answer.DataLength)));
            }
        }

        return addresses;
    }

    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        int? resumeOffset = null;
        for (var steps = 0; steps < 128; steps++)
        {
            EnsureAvailable(message, cursor, 1);
            var length = message[cursor++];
            if (length == 0)
            {
                offset = resumeOffset ?? cursor;
                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(message, cursor, 1);
                var pointer = ((length & 0x3F) << 8) | message[cursor++];
                if (pointer >= message.Length)
                {
                    throw new IOException("The routed DNS resolver returned an invalid name pointer.");
                }

                resumeOffset ??= cursor;
                cursor = pointer;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new IOException("The routed DNS resolver returned an invalid name label.");
            }

            EnsureAvailable(message, cursor, length);
            labels.Add(Encoding.ASCII.GetString(message.Slice(cursor, length)));
            cursor += length;
        }

        throw new IOException("The routed DNS resolver returned a cyclic name.");
    }

    private static string CanonicalizeHost(string host)
    {
        var trimmed = host.Trim().TrimEnd('.');
        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(trimmed);
        }
        catch (ArgumentException exception)
        {
            throw new IOException("The destination DNS name is invalid.", exception);
        }

        if (ascii.Length is 0 or > 253
            || ascii.Split('.').Any(static label => label.Length is 0 or > 63))
        {
            throw new IOException("The destination DNS name is invalid.");
        }

        return ascii.ToLowerInvariant();
    }

    private static void EnsureAvailable(
        ReadOnlySpan<byte> message,
        int offset,
        int length)
    {
        if (offset < 0 || length < 0 || offset > message.Length - length)
        {
            throw new IOException("The routed DNS resolver returned a truncated message.");
        }
    }

    private enum DnsRecordType : ushort
    {
        A = 1,
        Cname = 5,
        Aaaa = 28,
    }

    private sealed record DnsAnswer(
        string Owner,
        ushort Type,
        ushort RecordClass,
        int DataOffset,
        int DataLength);
}
