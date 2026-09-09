using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Asura.Application;
using Asura.Infrastructure;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspacePacketChannelTests
{
    [Fact]
    public async Task HandshakeNegotiatesGuestNetworkConfigurationAndCarriesPackets()
    {
        await using var streams = await ConnectedStreams.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (host, guest) = await OpenChannelsAsync(streams, timeout.Token);
        await using var ownedHost = host;
        await using var ownedGuest = guest;

        Assert.Equal("gsnet0", guest.Configuration.InterfaceName);
        Assert.Equal(1420, guest.Configuration.Mtu);
        Assert.Equal(IPAddress.Parse("100.64.0.2"), guest.Configuration.Ipv4Address);
        Assert.Equal(30, guest.Configuration.Ipv4PrefixLength);
        Assert.Equal(IPAddress.Parse("fd00::2"), guest.Configuration.Ipv6Address);
        Assert.Equal(126, guest.Configuration.Ipv6PrefixLength);
        Assert.Equal(
            [IPAddress.Parse("100.64.0.1"), IPAddress.Parse("fd00::1")],
            guest.Configuration.DnsServers);
        Assert.Equal(
            [new IPNetwork(IPAddress.Any, 0), new IPNetwork(IPAddress.IPv6Any, 0)],
            guest.Configuration.DefaultRoutes);

        var expectedPacket = CreateIpv4Packet();
        await guest.SendPacketAsync(expectedPacket, timeout.Token);
        var receivedPacket = await host.ReceivePacketAsync(timeout.Token);
        Assert.Equal(expectedPacket.Packet.ToArray(), receivedPacket.Packet.ToArray());
    }

    [Fact]
    public async Task HostHandshakeRejectsHelloAuthenticatedWithAnotherKey()
    {
        var expectedKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var suppliedKey = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var guestWriteKey = DeriveGuestToHostKey(suppliedKey);
        var hello = new byte[34];
        hello[0] = WorkspacePacketFrameCodec.CurrentVersion;
        hello[1] = WorkspacePacketFrameCodec.CurrentVersion;
        using var stream = new MemoryStream();
        await WorkspacePacketFrameCodec.WriteAsync(
            stream,
            guestWriteKey,
            WorkspacePacketFrameCodec.MessageKind.GuestHello,
            0,
            hello,
            CancellationToken.None);
        stream.Position = 0;

        var exception = await Assert.ThrowsAsync<WorkspacePacketChannelProtocolException>(
            () => WorkspacePacketChannel.AcceptHostAsync(
                stream,
                expectedKey,
                CreateConfiguration(),
                CancellationToken.None).AsTask());

        Assert.Equal(WorkspacePacketChannelFailure.AuthenticationFailed, exception.Failure);
    }

    [Fact]
    public async Task FrameReaderRejectsMalformedHeaderBeforeReadingPayload()
    {
        var key = Enumerable.Repeat((byte)0x33, 32).ToArray();
        var bytes = await WriteFrameAsync(key, [1, 2, 3]);
        bytes[0] ^= 0xff;
        using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<WorkspacePacketChannelProtocolException>(
            () => WorkspacePacketFrameCodec.ReadAsync(
                stream,
                key,
                0,
                CancellationToken.None).AsTask());

        Assert.Equal(WorkspacePacketChannelFailure.InvalidFrame, exception.Failure);
    }

    [Fact]
    public async Task FrameReaderRejectsOversizedDeclaredPayloadBeforeAllocatingIt()
    {
        var key = Enumerable.Repeat((byte)0x44, 32).ToArray();
        var bytes = await WriteFrameAsync(key, []);
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(16),
            WorkspacePacketFrameCodec.MaximumPayloadLength + 1U);
        using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<WorkspacePacketChannelProtocolException>(
            () => WorkspacePacketFrameCodec.ReadAsync(
                stream,
                key,
                0,
                CancellationToken.None).AsTask());

        Assert.Equal(WorkspacePacketChannelFailure.FrameTooLarge, exception.Failure);
    }

    [Fact]
    public async Task FrameReaderRejectsInvalidAuthenticationTag()
    {
        var key = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var bytes = await WriteFrameAsync(key, [1, 2, 3]);
        bytes[^1] ^= 0xff;
        using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<WorkspacePacketChannelProtocolException>(
            () => WorkspacePacketFrameCodec.ReadAsync(
                stream,
                key,
                0,
                CancellationToken.None).AsTask());

        Assert.Equal(WorkspacePacketChannelFailure.AuthenticationFailed, exception.Failure);
    }

    [Fact]
    public async Task ReplayedFrameIsRejectedByItsSequenceNumber()
    {
        var key = Enumerable.Repeat((byte)0x66, 32).ToArray();
        var bytes = await WriteFrameAsync(key, [1, 2, 3]);
        using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<WorkspacePacketChannelProtocolException>(
            () => WorkspacePacketFrameCodec.ReadAsync(
                stream,
                key,
                1,
                CancellationToken.None).AsTask());

        Assert.Equal(WorkspacePacketChannelFailure.InvalidSequence, exception.Failure);
    }

    [Fact]
    public async Task PacketCapturedFromEarlierHandshakeCannotReplayInNewSession()
    {
        var authenticationKey = WorkspacePacketChannel.CreateAuthenticationKey();
        try
        {
            await using var firstStreams = await ConnectedStreams.OpenAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var recordingGuestStream = new RecordingWriteStream(firstStreams.Guest);
            var (firstHost, firstGuest) = await OpenChannelsAsync(
                firstStreams.Host,
                recordingGuestStream,
                authenticationKey,
                timeout.Token);
            await using var ownedFirstHost = firstHost;
            await using var ownedFirstGuest = firstGuest;
            var captureOffset = recordingGuestStream.WrittenLength;
            await firstGuest.SendPacketAsync(CreateIpv4Packet(), timeout.Token);
            var capturedFrame = recordingGuestStream.WrittenSince(captureOffset);

            await using var secondStreams = await ConnectedStreams.OpenAsync();
            var (secondHost, secondGuest) = await OpenChannelsAsync(
                secondStreams.Host,
                secondStreams.Guest,
                authenticationKey,
                timeout.Token);
            await using var ownedSecondHost = secondHost;
            await using var ownedSecondGuest = secondGuest;
            var receive = secondHost.ReceivePacketAsync(timeout.Token).AsTask();
            await secondStreams.Guest.WriteAsync(capturedFrame, timeout.Token);

            var exception = await Assert.ThrowsAsync<WorkspacePacketChannelProtocolException>(
                () => receive);
            Assert.Equal(
                WorkspacePacketChannelFailure.AuthenticationFailed,
                exception.Failure);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authenticationKey);
        }
    }

    [Fact]
    public async Task CancelledReadFaultsChannelInsteadOfReusingPartialFrameState()
    {
        await using var streams = await ConnectedStreams.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (host, guest) = await OpenChannelsAsync(streams, timeout.Token);
        await using var ownedHost = host;
        await using var ownedGuest = guest;
        using var cancellation = new CancellationTokenSource();

        var receive = host.ReceivePacketAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        var exception = await Assert.ThrowsAsync<WorkspacePacketChannelProtocolException>(
            () => host.ReceivePacketAsync(CancellationToken.None).AsTask());

        Assert.Equal(WorkspacePacketChannelFailure.ChannelFaulted, exception.Failure);
    }

    [Fact]
    public async Task DisposalCancelsActiveReadBeforeErasingSessionKeys()
    {
        await using var streams = await ConnectedStreams.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (host, guest) = await OpenChannelsAsync(streams, timeout.Token);
        await using var ownedGuest = guest;

        var receive = host.ReceivePacketAsync(CancellationToken.None).AsTask();
        var disposal = host.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        await disposal.WaitAsync(timeout.Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => host.ReceivePacketAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public void ConfigurationRejectsDnsOutsideConfiguredAddressFamilies()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new WorkspaceGuestNetworkConfiguration(
                "gsnet0",
                1280,
                IPAddress.Parse("100.64.0.2"),
                30,
                null,
                null,
                [IPAddress.Parse("fd00::1")]));

        Assert.Equal("dnsServers", exception.ParamName);
    }

    private static WorkspaceGuestNetworkConfiguration CreateConfiguration() =>
        new(
            "gsnet0",
            1420,
            IPAddress.Parse("100.64.0.2"),
            30,
            IPAddress.Parse("fd00::2"),
            126,
            [IPAddress.Parse("100.64.0.1"), IPAddress.Parse("fd00::1")]);

    private static async Task<(WorkspacePacketChannel Host, WorkspacePacketChannel Guest)>
        OpenChannelsAsync(ConnectedStreams streams, CancellationToken cancellationToken)
    {
        var authenticationKey = WorkspacePacketChannel.CreateAuthenticationKey();
        try
        {
            return await OpenChannelsAsync(
                streams.Host,
                streams.Guest,
                authenticationKey,
                cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authenticationKey);
        }
    }

    private static async Task<(WorkspacePacketChannel Host, WorkspacePacketChannel Guest)>
        OpenChannelsAsync(
            Stream hostStream,
            Stream guestStream,
            byte[] authenticationKey,
            CancellationToken cancellationToken)
    {
        var hostTask = WorkspacePacketChannel.AcceptHostAsync(
            hostStream,
            authenticationKey,
            CreateConfiguration(),
            cancellationToken).AsTask();
        var guestTask = WorkspacePacketChannel.ConnectGuestAsync(
            guestStream,
            authenticationKey,
            cancellationToken).AsTask();
        await Task.WhenAll(hostTask, guestTask);
        return (await hostTask, await guestTask);
    }

    private static WorkspaceIpPacketFrame CreateIpv4Packet()
    {
        var bytes = new byte[20];
        bytes[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)bytes.Length);
        return new WorkspaceIpPacketFrame(bytes);
    }

    private static async Task<byte[]> WriteFrameAsync(byte[] key, byte[] payload)
    {
        using var stream = new MemoryStream();
        await WorkspacePacketFrameCodec.WriteAsync(
            stream,
            key,
            WorkspacePacketFrameCodec.MessageKind.IpPacket,
            0,
            payload,
            CancellationToken.None);
        return stream.ToArray();
    }

    private static byte[] DeriveGuestToHostKey(byte[] key) =>
        System.Security.Cryptography.HMACSHA256.HashData(
            key,
            "Asura workspace packet channel guest to host v1"u8);

    private sealed class ConnectedStreams : IAsyncDisposable
    {
        private readonly TcpClient _hostClient;
        private readonly TcpClient _guestClient;

        private ConnectedStreams(TcpClient hostClient, TcpClient guestClient)
        {
            _hostClient = hostClient;
            _guestClient = guestClient;
            Host = hostClient.GetStream();
            Guest = guestClient.GetStream();
        }

        public NetworkStream Host { get; }

        public NetworkStream Guest { get; }

        public static async ValueTask<ConnectedStreams> OpenAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
            var guestClient = new TcpClient(AddressFamily.InterNetwork);
            var connectTask = guestClient.ConnectAsync(
                IPAddress.Loopback,
                endpoint.Port,
                CancellationToken.None).AsTask();
            var hostClient = await listener.AcceptTcpClientAsync();
            await connectTask;
            return new ConnectedStreams(hostClient, guestClient);
        }

        public ValueTask DisposeAsync()
        {
            Host.Dispose();
            Guest.Dispose();
            _hostClient.Dispose();
            _guestClient.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWriteStream(Stream inner) : Stream
    {
        private readonly MemoryStream _written = new();

        public int WrittenLength => checked((int)_written.Length);

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public byte[] WrittenSince(int offset) => _written.ToArray()[offset..];

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _written.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            await _written.WriteAsync(buffer, cancellationToken);
        }
    }
}
