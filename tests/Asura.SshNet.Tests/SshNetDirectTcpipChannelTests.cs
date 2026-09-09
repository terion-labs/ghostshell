using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Files;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace Asura.SshNet.Tests;

public sealed class SshNetDirectTcpipChannelTests
{
    [Theory]
    [InlineData(2026, 0, 0, 0)]
    [InlineData(2026, 0, 1, 1)]
    [InlineData(2027, 0, 0, 1)]
    public void Different_assembly_version_is_rejected_before_channel_access(int major, int minor, int build, int revision) =>
        Assert.Throws<NotSupportedException>(() => SshNetDirectTcpipChannel.ValidateVersion(new Version(major, minor, build, revision)));

    [Fact]
    public void Missing_assembly_version_is_rejected() =>
        Assert.Throws<NotSupportedException>(() => SshNetDirectTcpipChannel.ValidateVersion(null));

    [Fact]
    public void Actual_stock_assembly_matches_the_pinned_adapter() =>
        SshNetDirectTcpipChannel.ValidateVersion();

    [Fact]
    public void Actual_private_channel_surface_resolves_without_network_or_signed_friend()
    {
        using var client = new SshClient("127.0.0.1", "synthetic", "not-used");
        Assert.Throws<InvalidOperationException>(() => SshNetDirectTcpipChannel.Create(client));
        var factory = CreateServiceFactory();
        var session = CreateSession(factory, client.ConnectionInfo, CreateSocketFactory(factory));
        SetSession(client, session);
        using var channel = SshNetDirectTcpipChannel.Create(client);
        channel.Bind();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var owner = new TestForward();
        // This is the real library's expected state rejection, after the pinned
        // accessor dispatched to its real Open implementation. No connect occurs.
        Assert.Equal("Session is not connected.", Assert.Throws<SshException>(() => channel.Open("127.0.0.1", 1, owner, socket)).Message);
        channel.Dispose();
        Assert.Throws<ObjectDisposedException>(() => channel.Bind());
    }

    [Fact]
    public async Task Graceful_session_disconnect_stops_listener_without_client_error_event()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(listener.LocalEndpoint, timeout.Token);
        using var peer = await listener.AcceptSocketAsync(timeout.Token);
        using var client = new SshClient("127.0.0.1", "synthetic", "not-used");
        var factory = CreateServiceFactory();
        var session = (Session)CreateSession(factory, client.ConnectionInfo, CreateSocketFactory(factory));
        SetSession(client, session);
        // Test-only connected-state setup: no SSH credentials or service. Invoke
        // the real received-disconnect handler, not a mock ErrorOccurred event.
        SessionSocket(session) = socket;
        SessionAuthenticated(session) = true;
        var completed = SessionListenerCompleted(session);
        completed.Reset();
        SshNetBrowserTunnelFactory.SshBrowserTunnel? tunnel = null;
        try
        {
            Assert.True(client.IsConnected);
            var errors = 0;
            client.ErrorOccurred += (_, _) => errors++;
            var credentials = new WorkspaceNetworkProxyCredentials("u", "p");
            using var proxy = new AuthenticatedSshSocksProxy(credentials,
                () => throw new InvalidOperationException("No channel is expected."), TimeSpan.FromSeconds(1));
            tunnel = new SshNetBrowserTunnelFactory.SshBrowserTunnel(
                client, proxy, credentials, "synthetic-identity", [], []);
            using var pending = new TcpClient();
            await pending.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, timeout.Token);
            await pending.GetStream().WriteAsync(new byte[] { 5, 1, 2 }, timeout.Token);
            var selected = new byte[2];
            await pending.GetStream().ReadExactlyAsync(selected, timeout.Token);
            Assert.Equal(new byte[] { 5, 2 }, selected);

            ReceiveDisconnect(session, new DisconnectMessage(DisconnectReason.ByApplication, "synthetic disconnect"));
            // There is no message-loop thread in this offline fixture to signal it.
            completed.Set();
            await proxy.WaitForStoppedAsync(timeout.Token);
            Assert.Equal(0, errors);
            using var rejected = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () =>
                await rejected.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, timeout.Token));
        }
        finally
        {
            completed.Set();
            SessionAuthenticated(session) = false;
            tunnel?.Dispose();
        }
    }

    // Offline setup only. Production owns five channel accessors and does not
    // construct sessions or modify BaseClient state through these test calls.
    private const string SessionType = "Renci.SshNet.ISession, Renci.SshNet";
    private const string FactoryType = "Renci.SshNet.ServiceFactory, Renci.SshNet";
    private const string SocketFactoryType = "Renci.SshNet.Connection.ISocketFactory, Renci.SshNet";

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType(FactoryType)]
    private static extern object CreateServiceFactory();

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CreateSocketFactory")]
    [return: UnsafeAccessorType(SocketFactoryType)]
    private static extern object CreateSocketFactory([UnsafeAccessorType(FactoryType)] object factory);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CreateSession")]
    [return: UnsafeAccessorType(SessionType)]
    private static extern object CreateSession(
        [UnsafeAccessorType(FactoryType)] object factory,
        ConnectionInfo info,
        [UnsafeAccessorType(SocketFactoryType)] object socketFactory);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Session")]
    private static extern void SetSession(BaseClient client, [UnsafeAccessorType(SessionType)] object session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_socket")]
    private static extern ref Socket SessionSocket(Session session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isAuthenticated")]
    private static extern ref bool SessionAuthenticated(Session session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_messageListenerCompleted")]
    private static extern ref ManualResetEvent SessionListenerCompleted(Session session);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnDisconnectReceived")]
    private static extern void ReceiveDisconnect(Session session, DisconnectMessage message);

    private sealed class TestForward : IForwardedPort
    {
        public event EventHandler? Closing;

        public void Dispose() => Closing?.Invoke(this, EventArgs.Empty);
    }
}
