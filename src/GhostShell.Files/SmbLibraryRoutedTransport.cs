using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GhostShell.Application;
using SMBLibrary;
using SMBLibrary.Client;

namespace GhostShell.Files;

/// <summary>
/// Adapts SMBLibrary's fixed Socket transport to one workspace-routed stream. SMBLibrary 1.5.7.1
/// exposes a non-default TCP port to subclasses, but not a Socket or Stream factory, so the
/// client connects to this per-session unprivileged loopback relay.
/// </summary>
internal sealed class SmbLibraryRoutedTransport : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Stream _upstream;
    private readonly Task _relay;
    private TcpClient? _client;
    private Exception? _failure;
    private int _disposed;

    private SmbLibraryRoutedTransport(Stream upstream)
    {
        _upstream = upstream;
        _listener.Start(1);
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _relay = RelayAsync();
    }

    public int LocalPort { get; }

    public static async ValueTask<SmbLibraryRoutedTransport> OpenAsync(
        IWorkspaceNetworkConnector connector,
        string server,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        cancellationToken.ThrowIfCancellationRequested();
        var upstream = await connector.ConnectTcpAsync(
                server,
                SMB2Client.DirectTCPPort,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new SmbLibraryRoutedTransport(upstream);
        }
        catch
        {
            await upstream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is not null)
        {
            throw new IOException("The routed SMB transport failed.");
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
        _client?.Dispose();
        _upstream.Dispose();
        _lifetime.Dispose();
    }

    private async Task RelayAsync()
    {
        try
        {
            var client = await _listener.AcceptTcpClientAsync(_lifetime.Token)
                .ConfigureAwait(false);
            _client = client;
            _listener.Stop();
            client.NoDelay = true;
            using (client)
            {
                var downstream = client.GetStream();
                await Task.WhenAll(
                        downstream.CopyToAsync(_upstream, _lifetime.Token),
                        _upstream.CopyToAsync(downstream, _lifetime.Token))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (SocketException exception)
        {
            Volatile.Write(ref _failure, exception);
        }
        catch (IOException exception)
        {
            Volatile.Write(ref _failure, exception);
        }
    }

}

/// <summary>
/// Narrow compatibility boundary for SMBLibrary 1.5.7.1. The package does not expose a logical
/// server-name plus endpoint overload, but its authentication and tree paths require the logical
/// name even when the actual endpoint is GhostSHELL's loopback relay.
/// </summary>
internal sealed class RoutedSmb2Client : SMB2Client
{
    private static readonly Lazy<FieldInfo?> ServerNameField = new(
        ResolveServerNameField,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public RoutedSmb2Client(int responseTimeoutInMilliseconds)
        : base(responseTimeoutInMilliseconds, enableSMB311Support: true)
    {
    }

    public static bool IsCompatible => ServerNameField.Value is not null;

    public bool Connect(string logicalServerName, int relayPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalServerName);
        PrepareLogicalServerName(logicalServerName);
        return Connect(
            IPAddress.Loopback,
            SMBTransportType.DirectTCPTransport,
            relayPort);
    }

    internal void PrepareLogicalServerName(string logicalServerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalServerName);
        if (ServerNameField.Value is not { } field)
        {
            throw new NotSupportedException(
                "The installed SMBLibrary version cannot use a workspace-routed transport.");
        }

        field.SetValue(this, logicalServerName);
        if (!string.Equals(
                field.GetValue(this) as string,
                logicalServerName,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "The installed SMBLibrary version cannot preserve the SMB server identity.");
        }
    }

    internal string? LogicalServerName =>
        ServerNameField.Value?.GetValue(this) as string;

    private static FieldInfo? ResolveServerNameField()
    {
        var field = typeof(SMB2Client).GetField(
            "m_serverName",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null
            || field.FieldType != typeof(string)
            || field.IsInitOnly)
        {
            return null;
        }

        const string probeServerName = "ghostshell-smb-compatibility-probe";
        try
        {
            var probe = new SMB2Client();
            field.SetValue(probe, probeServerName);
            return string.Equals(
                field.GetValue(probe) as string,
                probeServerName,
                StringComparison.Ordinal)
                    ? field
                    : null;
        }
        catch (MemberAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (TargetException)
        {
            return null;
        }
    }
}
