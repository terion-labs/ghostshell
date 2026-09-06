using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

/// <summary>
/// Presents database and Redis clients with a loopback endpoint while opening
/// every real socket through the workspace route. Explicit SSH tunnels keep
/// their SSH semantics, but their SSH transport uses the same connector.
/// </summary>
internal sealed class WorkspaceNetworkDatabaseTunnelFactory(
    IWorkspaceNetworkConnector networkConnector,
    IDatabaseTunnelFactory sshTunnelFactory) : IDatabaseTunnelFactory
{
    public ValueTask<IDatabaseTunnelLease> OpenAsync(
        ConnectionProfile connection,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return connection.Endpoint switch
        {
            ConnectionEndpoint.Local => ValueTask.FromResult<IDatabaseTunnelLease>(
                new WorkspaceNetworkDatabaseTunnel(
                    networkConnector,
                    targetHost,
                    targetPort)),
            ConnectionEndpoint.Ssh => sshTunnelFactory.OpenAsync(
                connection,
                targetHost,
                targetPort,
                cancellationToken),
            _ => ValueTask.FromException<IDatabaseTunnelLease>(
                new InvalidOperationException(
                    $"{connection.ConnectionKind} connections cannot tunnel a database.")),
        };
    }

    private sealed class WorkspaceNetworkDatabaseTunnel : IDatabaseTunnelLease
    {
        private readonly ConcurrentDictionary<long, Task> _connections = [];
        private readonly IWorkspaceNetworkConnector _networkConnector;
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _acceptLoop;
        private long _sequence;
        private int _disposed;

        public WorkspaceNetworkDatabaseTunnel(
            IWorkspaceNetworkConnector networkConnector,
            string targetHost,
            int targetPort)
        {
            _networkConnector = networkConnector
                ?? throw new ArgumentNullException(nameof(networkConnector));
            ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
            ArgumentOutOfRangeException.ThrowIfLessThan(targetPort, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPort, 65_535);
            _targetHost = targetHost;
            _targetPort = targetPort;
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        public int LocalPort { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _lifetime.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            await IgnoreExpectedFailureAsync(_acceptLoop).ConfigureAwait(false);
            await Task.WhenAll(_connections.Values.Select(IgnoreExpectedFailureAsync))
                .ConfigureAwait(false);
            _lifetime.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient downstream;
                try
                {
                    downstream = await _listener
                        .AcceptTcpClientAsync(_lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                var id = Interlocked.Increment(ref _sequence);
                var task = RelayAsync(downstream, _lifetime.Token);
                _connections.TryAdd(id, task);
                _ = task.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        _connections.TryRemove(id, out _);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private async Task RelayAsync(
            TcpClient downstream,
            CancellationToken cancellationToken)
        {
            using (downstream)
            await using (var upstream = await _networkConnector
                .ConnectTcpAsync(_targetHost, _targetPort, cancellationToken)
                .ConfigureAwait(false))
            {
                var downstreamStream = downstream.GetStream();
                await Task.WhenAll(
                        CopyIgnoringExpectedFailureAsync(
                            downstreamStream,
                            upstream,
                            cancellationToken),
                        CopyIgnoringExpectedFailureAsync(
                            upstream,
                            downstreamStream,
                            cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        private static async Task CopyIgnoringExpectedFailureAsync(
            Stream source,
            Stream destination,
            CancellationToken cancellationToken)
        {
            try
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or SocketException or ObjectDisposedException
                    or OperationCanceledException)
            {
            }
        }

        private static async Task IgnoreExpectedFailureAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or SocketException or ObjectDisposedException
                    or OperationCanceledException)
            {
            }
        }
    }
}
