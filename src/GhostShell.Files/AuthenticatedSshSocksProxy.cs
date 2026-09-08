using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using Renci.SshNet;

namespace GhostShell.Files;

/// <summary>
/// A single authenticated loopback SOCKS5 listener. Only a complete RFC 1929
/// handshake followed by CONNECT can acquire an SSH channel; there is no backend
/// SOCKS listener to bypass. Existing browser traffic remains streaming.
/// </summary>
internal sealed partial class AuthenticatedSshSocksProxy : IForwardedPort
{
    private const int MaximumPendingAuthentication = 32;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Socket, TaskCompletionSource> _connections = new();
    private readonly Func<ISshDirectTcpipChannel> _createChannel;
    private readonly byte[] _usernameHash;
    private readonly byte[] _passwordHash;
    private readonly TimeSpan _handshakeTimeout;
    private readonly Task _acceptLoop;
    private int _pendingAuthentication;
    private int _disposed;

    public AuthenticatedSshSocksProxy(
        WorkspaceNetworkProxyCredentials credentials,
        Func<ISshDirectTcpipChannel> createChannel,
        TimeSpan handshakeTimeout)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(createChannel);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(handshakeTimeout, TimeSpan.Zero);
        _usernameHash = HashCredential(credentials.Username);
        _passwordHash = HashCredential(credentials.Password);
        _createChannel = createChannel;
        _handshakeTimeout = handshakeTimeout;
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int LocalPort { get; }

    public event EventHandler? Closing;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_lifetime.Token).ConfigureAwait(false);
                StartConnection(socket);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            Dispose();
        }
    }

    private void StartConnection(Socket socket)
    {
        // Ownership transfers from the accept loop to the registered worker here.
        // Rejected sockets are disposed immediately; ServeAsync owns accepted ones.
        if (_lifetime.IsCancellationRequested)
        {
            socket.Dispose();
            return;
        }
        if (Interlocked.Increment(ref _pendingAuthentication) > MaximumPendingAuthentication)
        {
            Interlocked.Decrement(ref _pendingAuthentication);
            socket.Dispose();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections.TryAdd(socket, completion);
        _ = ServeAsync(socket, completion);
    }

    private async Task ServeAsync(Socket socket, TaskCompletionSource completion)
    {
        var holdsAuthenticationSlot = true;
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            handshake.CancelAfter(_handshakeTimeout);
            using var closeOnCancellation = handshake.Token.UnsafeRegister(
                static state => ((Socket)state!).Dispose(), socket);
            if (!await AuthenticateAsync(stream, ReleaseAuthenticationSlot, handshake.Token).ConfigureAwait(false))
            {
                return;
            }

            var destination = await ReadDestinationAsync(stream, handshake.Token).ConfigureAwait(false);
            if (destination is not { } target)
            {
                return;
            }

            // SSH.NET's Open/Bind are synchronous. Open is bounded by its existing
            // connection timeout; closing the owning SSH client aborts session waits.
            // Keep these calls off the caller/UI thread without buffering payloads.
            await Task.Run(async () =>
            {
                handshake.Token.ThrowIfCancellationRequested();
                using var channel = _createChannel();
                var opened = channel.Open(target.Host, target.Port, this, socket);
                await WriteReplyAsync(stream, opened ? (byte)0 : (byte)5, handshake.Token).ConfigureAwait(false);
                if (!opened)
                {
                    return;
                }

                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                channel.Bind();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed client exchange is terminated. Never log browser destinations,
            // authentication bytes or vendor exception text from this boundary.
        }
        finally
        {
            socket.Dispose();
            ReleaseAuthenticationSlot();
            completion.SetResult();
            _connections.TryRemove(socket, out _);
        }

        void ReleaseAuthenticationSlot()
        {
            if (holdsAuthenticationSlot)
            {
                holdsAuthenticationSlot = false;
                Interlocked.Decrement(ref _pendingAuthentication);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _listener.Stop();
        foreach (var handler in Closing?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // Continue closing the remaining owned channels if one is already broken.
            }
        }
        foreach (var socket in _connections.Keys)
        {
            socket.Dispose();
        }
        _ = ReleaseAfterStopAsync();
    }

    private async Task ReleaseAfterStopAsync()
    {
        await WaitForStoppedAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
        CryptographicOperations.ZeroMemory(_usernameHash);
        CryptographicOperations.ZeroMemory(_passwordHash);
    }

    internal async Task WaitForStoppedAsync(CancellationToken cancellationToken)
    {
        await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(_connections.Values.Select(value => value.Task))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] HashCredential(string credential)
    {
        var bytes = Encoding.UTF8.GetBytes(credential);
        try
        {
            if (bytes.Length is < 1 or > 255)
            {
                throw new ArgumentException("SOCKS credentials must contain between 1 and 255 UTF-8 bytes.");
            }
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
