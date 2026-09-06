using System.Buffers.Binary;
using System.Net;
using GhostShell.Application;

namespace GhostShell.Browser.Tests;

public sealed class WorkspaceRoutedDnsResolverTests
{
    [Fact]
    public async Task Resolution_uses_only_a_workspace_routed_literal_resolver()
    {
        var connector = new RecordingConnector(
            new DnsResponseStream(
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")));
        var authenticatedNames = new List<string>();
        var resolver = new WorkspaceRoutedDnsResolver(
            connector,
            (stream, serverName, _) =>
            {
                authenticatedNames.Add(serverName);
                return ValueTask.FromResult(stream);
            });

        var addresses = await resolver.ResolveAsync(
            "example.com",
            CancellationToken.None);

        Assert.Equal(
            [
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"),
            ],
            addresses);
        Assert.Equal([("1.1.1.1", 853)], connector.Destinations);
        Assert.Equal(["cloudflare-dns.com"], authenticatedNames);
        Assert.Equal(["example.com", "example.com"], connector.Stream.Questions);
    }

    [Fact]
    public async Task Resolver_fallback_remains_on_the_workspace_route()
    {
        var connector = new RecordingConnector(
            new DnsResponseStream(
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")),
            failFirstConnection: true);
        var resolver = new WorkspaceRoutedDnsResolver(
            connector,
            static (stream, _, _) => ValueTask.FromResult(stream));

        _ = await resolver.ResolveAsync("example.com", CancellationToken.None);

        Assert.Equal(
            [("1.1.1.1", 853), ("1.0.0.1", 853)],
            connector.Destinations);
    }

    [Fact]
    public async Task Resolver_port_block_is_fail_closed_without_a_host_fallback()
    {
        var connector = new RecordingConnector(
            new DnsResponseStream(
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")),
            failAllConnections: true);
        var resolver = new WorkspaceRoutedDnsResolver(
            connector,
            static (stream, _, _) => ValueTask.FromResult(stream));

        await Assert.ThrowsAsync<IOException>(async () =>
            await resolver.ResolveAsync("example.com", CancellationToken.None));

        Assert.Equal(
            [("1.1.1.1", 853), ("1.0.0.1", 853)],
            connector.Destinations);
        Assert.Empty(connector.Stream.Questions);
    }

    private sealed class RecordingConnector(
        DnsResponseStream stream,
        bool failFirstConnection = false,
        bool failAllConnections = false) : IWorkspaceNetworkConnector
    {
        private bool _failed;

        public List<(string Host, int Port)> Destinations { get; } = [];

        public DnsResponseStream Stream { get; } = stream;

        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Attached;

        public Uri LocalProxyEndpoint { get; } =
            new("socks5://127.0.0.1:45678", UriKind.Absolute);

        public ValueTask<Stream> ConnectTcpAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Destinations.Add((host, port));
            if (failAllConnections || (failFirstConnection && !_failed))
            {
                _failed = true;
                throw new IOException("The first routed resolver is unavailable.");
            }

            return ValueTask.FromResult<Stream>(Stream);
        }
    }

    private sealed class DnsResponseStream(
        IPAddress ipv4,
        IPAddress ipv6) : Stream
    {
        private readonly Queue<byte> _response = new();

        public List<string> Questions { get; } = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _response.Count);
            for (var index = 0; index < read; index++)
            {
                buffer[offset + index] = _response.Dequeue();
            }

            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, _response.Count);
            for (var index = 0; index < read; index++)
            {
                buffer.Span[index] = _response.Dequeue();
            }

            return ValueTask.FromResult(read);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            QueueResponse(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueueResponse(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void QueueResponse(ReadOnlySpan<byte> frame)
        {
            var queryLength = BinaryPrimitives.ReadUInt16BigEndian(frame);
            var query = frame.Slice(2, queryLength);
            var nameOffset = 12;
            var labels = new List<string>();
            while (query[nameOffset] != 0)
            {
                var length = query[nameOffset++];
                labels.Add(System.Text.Encoding.ASCII.GetString(
                    query.Slice(nameOffset, length)));
                nameOffset += length;
            }

            nameOffset++;
            Questions.Add(string.Join('.', labels));
            var recordType = BinaryPrimitives.ReadUInt16BigEndian(query[nameOffset..]);
            var address = recordType == 1 ? ipv4 : ipv6;
            var addressBytes = address.GetAddressBytes();
            var response = new byte[query.Length + 12 + addressBytes.Length];
            query.CopyTo(response);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8180);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), 1);
            var answerOffset = query.Length;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset), 0xC00C);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 2), recordType);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(answerOffset + 6), 60);
            BinaryPrimitives.WriteUInt16BigEndian(
                response.AsSpan(answerOffset + 10),
                checked((ushort)addressBytes.Length));
            addressBytes.CopyTo(response, answerOffset + 12);
            var responseFrame = new byte[response.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(
                responseFrame,
                checked((ushort)response.Length));
            response.CopyTo(responseFrame, 2);
            foreach (var value in responseFrame)
            {
                _response.Enqueue(value);
            }
        }
    }
}
