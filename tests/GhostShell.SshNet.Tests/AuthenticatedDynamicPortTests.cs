using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Renci.SshNet;
using Renci.SshNet.Channels;

namespace GhostShell.SshNet.Tests;

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
        fixture.Channel.Verify(channel => channel.Open(
            It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<IForwardedPort>(), It.IsAny<Socket>()), Times.Never);
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
        fixture.Channel.Verify(channel => channel.Open(
            It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<IForwardedPort>(), It.IsAny<Socket>()), Times.Never);
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
        fixture.Channel.Verify(channel => channel.Open(
            It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<IForwardedPort>(), It.IsAny<Socket>()), Times.Never);

        await stream.WriteAsync(Convert.FromHexString("05010001010203040050"));
        await AssertReplyAsync(stream, "05000001000000000000");
        fixture.Channel.Verify(channel => channel.Open("1.2.3.4", 80, fixture.Port, It.IsAny<Socket>()), Times.Once);
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
        fixture.Channel.Verify(channel => channel.Open(
            It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<IForwardedPort>(), It.IsAny<Socket>()), Times.Never);
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
            fixture.Channel.Verify(channel => channel.Open(
                It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<IForwardedPort>(), It.IsAny<Socket>()), Times.Never);
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
        fixture.Channel.Verify(channel => channel.Open(
            It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<IForwardedPort>(), It.IsAny<Socket>()), Times.Never);
    }

    [Fact]
    public async Task Existing_constructor_retains_upstream_noauth_behavior()
    {
        using var fixture = new ListenerFixture(requireCredentials: false);
        using var client = await fixture.ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        await AssertReplyAsync(stream, "0500");
        await stream.WriteAsync(Convert.FromHexString("05010001010203040050"));
        await AssertReplyAsync(stream, "05000001000000000000");
        fixture.Channel.Verify(channel => channel.Open("1.2.3.4", 80, fixture.Port, It.IsAny<Socket>()), Times.Once);
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
        fixture.Channel.Verify(channel => channel.Open(
            It.IsAny<string>(), It.IsAny<uint>(), It.IsAny<IForwardedPort>(), It.IsAny<Socket>()), Times.Never);
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

    private static async Task AssertReplyAsync(NetworkStream stream, string expected)
    {
        var bytes = Convert.FromHexString(expected);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var actual = new byte[bytes.Length];
        await stream.ReadExactlyAsync(actual, timeout.Token);
        Assert.Equal(bytes, actual);
    }

    private sealed class ListenerFixture : IDisposable
    {
        public ListenerFixture(bool requireCredentials = true, TimeSpan? handshakeTimeout = null)
        {
            var info = new Mock<IConnectionInfo>();
            info.SetupGet(value => value.Timeout).Returns(handshakeTimeout ?? TimeSpan.FromSeconds(1));
            var session = new Mock<ISession>();
            session.SetupGet(value => value.ConnectionInfo).Returns(info.Object);
            session.SetupGet(value => value.IsConnected).Returns(true);
            session.SetupGet(value => value.SessionLoggerFactory).Returns(NullLoggerFactory.Instance);
            session.Setup(value => value.CreateChannelDirectTcpip()).Returns(Channel.Object);
            Channel.SetupGet(value => value.IsOpen).Returns(true);
            Port = requireCredentials
                ? new ForwardedPortDynamic("127.0.0.1", 0, "u", "p")
                : new ForwardedPortDynamic("127.0.0.1", 0);
            Port.Session = session.Object;
            Port.Start();
        }

        public Mock<IChannelDirectTcpip> Channel { get; } = new();

        public ForwardedPortDynamic Port { get; }

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(45));

        public async Task<TcpClient> ConnectAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, checked((int)Port.BoundPort), Timeout.Token);
            return client;
        }

        public void Dispose()
        {
            Port.Dispose();
            Timeout.Dispose();
        }
    }
}
