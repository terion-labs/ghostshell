using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Asura.Application;
using Asura.Files;
using Renci.SshNet;

namespace Asura.SshNet.Tests;

public sealed class AuthenticatedDynamicPortTests
{
    [Theory]
    [InlineData("040100500102030400", "")]
    [InlineData("050100", "05FF")]
    [InlineData("0500", "")]
    public async Task Unauthenticated_protocols_cannot_open_an_ssh_channel(string request, string reply)
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(request));
        await AssertReplyAsync(stream, reply);
        await AssertClosedAsync(stream, fixture.Timeout.Token);
        Assert.Empty(fixture.Channel.OpenCalls);
    }

    [Theory]
    [InlineData("0101750178")]
    [InlineData("0101780170")]
    [InlineData("0201750170")]
    [InlineData("0100")]
    [InlineData("01017500")]
    public async Task Wrong_or_malformed_credentials_cannot_open_an_ssh_channel(string authentication)
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 2, 0, 2 });
        await AssertReplyAsync(stream, "0502");
        await stream.WriteAsync(Convert.FromHexString(authentication));
        await AssertReplyAsync(stream, "0101");
        await AssertClosedAsync(stream, fixture.Timeout.Token);
        Assert.Empty(fixture.Channel.OpenCalls);
    }

    [Fact]
    public async Task Valid_credentials_open_only_the_requested_destination()
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        await AssertReplyAsync(stream, "0502");
        await stream.WriteAsync(Convert.FromHexString("0101750170"));
        await AssertReplyAsync(stream, "0100");
        Assert.Empty(fixture.Channel.OpenCalls);

        await stream.WriteAsync(Convert.FromHexString("05010001010203040050"));
        await AssertReplyAsync(stream, "05000001000000000000");
        Assert.Equal(("1.2.3.4", 80u, (IForwardedPort)fixture.Port), Assert.Single(fixture.Channel.OpenCalls));
    }

    [Fact]
    public async Task Partial_handshake_times_out_without_opening_a_channel()
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        await AssertReplyAsync(stream, "0502");
        await AssertClosedAsync(stream, fixture.Timeout.Token);
        Assert.Empty(fixture.Channel.OpenCalls);
    }

    [Fact]
    public async Task Pending_authenticated_connections_are_bounded_and_slots_are_reclaimed()
    {
        using var fixture = new ListenerFixture(handshakeTimeout: TimeSpan.FromSeconds(30));
        var clients = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                var client = await fixture.ConnectAsync();
                clients.Add(client);
                await client.GetStream().WriteAsync(new byte[] { 5, 1, 2 });
                await AssertReplyAsync(client.GetStream(), "0502");
            }

            using var excess = await fixture.ConnectAsync();
            await AssertClosedAsync(excess.GetStream(), fixture.Timeout.Token);
            Assert.Empty(fixture.Channel.OpenCalls);
            var firstStream = clients[0].GetStream();
            await firstStream.WriteAsync(Convert.FromHexString("0101750170"));
            await AssertReplyAsync(firstStream, "0100");

            using var replacement = await fixture.ConnectAsync();
            await replacement.GetStream().WriteAsync(new byte[] { 5, 1, 2 });
            await AssertReplyAsync(replacement.GetStream(), "0502");
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Truncated_authentication_does_not_open_a_channel()
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        await AssertReplyAsync(stream, "0502");
        await stream.WriteAsync(Convert.FromHexString("0101750270"));
        client.Client.Shutdown(SocketShutdown.Send);
        await AssertReplyAsync(stream, "0101");
        Assert.Empty(fixture.Channel.OpenCalls);
    }

    [Fact]
    public async Task Remote_open_refusal_never_returns_success()
    {
        using var fixture = new ListenerFixture(openSucceeds: false);
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        await AssertReplyAsync(stream, "0502");
        await stream.WriteAsync(Convert.FromHexString("0101750170"));
        await AssertReplyAsync(stream, "0100");
        await stream.WriteAsync(Convert.FromHexString("05010001010203040050"));
        await AssertReplyAsync(stream, "05050001000000000000");
        await AssertClosedAsync(stream, fixture.Timeout.Token);
        Assert.Equal(("1.2.3.4", 80u, (IForwardedPort)fixture.Port), Assert.Single(fixture.Channel.OpenCalls));
    }

    [Fact]
    public async Task Disposing_listener_closes_incomplete_authentication_without_opening_a_channel()
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 });
        await AssertReplyAsync(stream, "0502");
        fixture.Port.Dispose();
        await AssertClosedAsync(stream, fixture.Timeout.Token);
        Assert.Empty(fixture.Channel.OpenCalls);
    }

    [Theory]
    [InlineData("05020001010203040050", "05070001000000000000")]
    [InlineData("05030001010203040050", "05070001000000000000")]
    [InlineData("05010101010203040050", "05070001000000000000")]
    [InlineData("04010001010203040050", "05070001000000000000")]
    [InlineData("05010002010203040050", "05080001000000000000")]
    [InlineData("0501000300", "05080001000000000000")]
    [InlineData("05010001010203040000", "05080001000000000000")]
    public async Task Malformed_or_unsupported_connect_does_not_open_channel(string request, string reply)
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString("0501020101750170"));
        await AssertReplyAsync(stream, "05020100");
        await stream.WriteAsync(Convert.FromHexString(request));
        await AssertReplyAsync(stream, reply);
        await AssertClosedAsync(stream, fixture.Timeout.Token);
        Assert.Empty(fixture.Channel.OpenCalls);
    }

    [Theory]
    [InlineData("050100030C746573742E696E76616C696401BB", "test.invalid", 443)]
    [InlineData("05010004000000000000000000000000000000010050", "::1", 80)]
    public async Task Domain_and_ipv6_targets_are_forwarded_without_local_resolution(string request, string host, uint port)
    {
        using var fixture = new ListenerFixture();
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString("0501020101750170"));
        await AssertReplyAsync(stream, "05020100");
        await stream.WriteAsync(Convert.FromHexString(request));
        await AssertReplyAsync(stream, "05000001000000000000");
        Assert.Equal((host, port, (IForwardedPort)fixture.Port), Assert.Single(fixture.Channel.OpenCalls));
    }

    private static async Task AssertClosedAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellationToken));
        }
        catch (IOException exception) when (exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // Closing with unread attacker bytes can reset rather than FIN the socket.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_closes_active_bind_or_open_and_observes_worker_exit(bool blockDuringOpen)
    {
        using var fixture = new ListenerFixture(handshakeTimeout: TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Block(Socket socket)
        {
            entered.SetResult();
            _ = socket.Receive(new byte[1]);
        }
        if (blockDuringOpen)
        {
            fixture.Channel.OpenCallback = Block;
        }
        else
        {
            fixture.Channel.BindCallback = Block;
        }
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString("050102010175017005010001010203040050"));
        await AssertReplyAsync(stream, "05020100");
        if (!blockDuringOpen)
        {
            await AssertReplyAsync(stream, "05000001000000000000");
        }
        await entered.Task.WaitAsync(fixture.Timeout.Token);
        fixture.Port.Dispose();
        await AssertClosedAsync(stream, fixture.Timeout.Token);
        await fixture.Port.WaitForStoppedAsync(fixture.Timeout.Token);
        Assert.Equal(1, fixture.Channel.DisposalCount);
    }

    private static async Task AssertReplyAsync(NetworkStream stream, string expected)
    {
        var bytes = Convert.FromHexString(expected);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var actual = new byte[bytes.Length];
        await stream.ReadExactlyAsync(actual, timeout.Token);
        Assert.Equal(bytes, actual);
    }

    private sealed class TestChannel(bool openSucceeds) : ISshDirectTcpipChannel
    {
        private Socket? _socket;
        private int _disposalCount;

        public ConcurrentQueue<(string Host, uint Port, IForwardedPort Owner)> OpenCalls { get; } = new();

        public Action<Socket>? OpenCallback { get; set; }

        public Action<Socket>? BindCallback { get; set; }

        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public bool Open(string host, uint port, IForwardedPort owner, Socket socket)
        {
            OpenCalls.Enqueue((host, port, owner));
            _socket = socket;
            OpenCallback?.Invoke(socket);
            return openSucceeds;
        }

        public void Bind() => BindCallback?.Invoke(_socket!);

        public void Dispose() => Interlocked.Increment(ref _disposalCount);
    }

    private sealed class ListenerFixture : IDisposable
    {
        public ListenerFixture(bool openSucceeds = true, TimeSpan? handshakeTimeout = null)
        {
            Channel = new TestChannel(openSucceeds);
            Port = new AuthenticatedSshSocksProxy(
                new WorkspaceNetworkProxyCredentials("u", "p"),
                () => Channel,
                handshakeTimeout ?? TimeSpan.FromSeconds(1));
        }

        public TestChannel Channel { get; }

        public AuthenticatedSshSocksProxy Port { get; }

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(45));

        public async Task<TcpClient> ConnectAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port.LocalPort, Timeout.Token);
            return client;
        }

        public void Dispose()
        {
            Port.Dispose();
            Timeout.Dispose();
        }
    }
}
