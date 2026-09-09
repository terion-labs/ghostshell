using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Renci.SshNet;

namespace Asura.Files;

/// <summary>
/// The only dependency on SSH.NET's private channel API. Package upgrades must
/// revalidate these five signatures in JIT and NativeAOT before changing the pin.
/// No runtime type-name input, dynamic code generation or private socket listener is used.
/// </summary>
internal sealed class SshNetDirectTcpipChannel : ISshDirectTcpipChannel
{
    private const string SessionType =
        "Renci.SshNet.ISession, Renci.SshNet, Version=2026.0.0.1, Culture=neutral, PublicKeyToken=1cee9f8bde3db106";
    private const string ChannelType =
        "Renci.SshNet.Channels.IChannelDirectTcpip, Renci.SshNet, Version=2026.0.0.1, Culture=neutral, PublicKeyToken=1cee9f8bde3db106";
    private object? _channel;

    private SshNetDirectTcpipChannel(object channel) => _channel = channel;

    internal static void ValidateVersion(Version? version)
    {
        // Assembly binding alone does not enforce the version in UnsafeAccessorType.
        if (version != new Version(2026, 0, 0, 1))
        {
            throw new NotSupportedException("The installed SSH channel implementation is not supported.");
        }
    }

    internal static void ValidateVersion() => ValidateVersion(typeof(SshClient).Assembly.GetName().Version);

    internal static Session GetConnectedSession(SshClient client)
    {
        ValidateVersion();
        return GetSession(client) as Session
            ?? throw new InvalidOperationException("The SSH session is not connected.");
    }

    public static SshNetDirectTcpipChannel Create(SshClient client)
    {
        var session = GetConnectedSession(client);
        return new SshNetDirectTcpipChannel(CreateChannel(session));
    }

    public bool Open(string host, uint port, IForwardedPort owner, Socket socket)
    {
        var channel = _channel ?? throw new ObjectDisposedException(nameof(SshNetDirectTcpipChannel));
        OpenChannel(channel, host, port, owner, socket);
        // A server refusal can return from Open without throwing. Never send SOCKS
        // success until the actual SSH channel reports an accepted open.
        return IsOpen(channel);
    }

    public void Bind() => BindChannel(
        _channel ?? throw new ObjectDisposedException(nameof(SshNetDirectTcpipChannel)));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _channel, null) is IDisposable channel)
        {
            channel.Dispose();
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Session")]
    [return: UnsafeAccessorType(SessionType)]
    private static extern object? GetSession(BaseClient client);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CreateChannelDirectTcpip")]
    [return: UnsafeAccessorType(ChannelType)]
    private static extern object CreateChannel([UnsafeAccessorType(SessionType)] object session);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsOpen")]
    private static extern bool IsOpen([UnsafeAccessorType(ChannelType)] object channel);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Open")]
    private static extern void OpenChannel(
        [UnsafeAccessorType(ChannelType)] object channel,
        string host,
        uint port,
        IForwardedPort owner,
        Socket socket);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Bind")]
    private static extern void BindChannel([UnsafeAccessorType(ChannelType)] object channel);
}

/// <summary>Owns one SSH direct-tcpip channel after an authenticated SOCKS request.</summary>
internal interface ISshDirectTcpipChannel : IDisposable
{
    bool Open(string host, uint port, IForwardedPort owner, Socket socket);

    void Bind();
}
