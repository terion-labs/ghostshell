using System.Net;
using GhostShell.Application;
using GhostShell.Core;
using StackExchange.Redis.Configuration;

namespace GhostShell.Redis;

/// <summary>
/// Maps every logical Redis endpoint, including discovered replicas and Sentinel
/// primaries, onto an owned relay. TLS and Redis topology retain the original name.
/// </summary>
internal sealed class RedisConnectionTunnel(
    IDatabaseTunnelFactory factory,
    ConnectionProfile connection) : Tunnel, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(string Host, int Port), IDatabaseTunnelLease> _leases = [];
    private int _disposing;

    public override async ValueTask<EndPoint?> GetSocketConnectEndpointAsync(
        EndPoint endpoint,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var target = endpoint switch
        {
            DnsEndPoint dns => (Host: dns.Host, dns.Port),
            IPEndPoint ip => (Host: ip.Address.ToString(), ip.Port),
            _ => throw new NotSupportedException("The Redis endpoint is not a TCP endpoint."),
        };
        await _gate.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
            if (!_leases.TryGetValue(target, out var lease))
            {
                lease = await factory.OpenAsync(connection, target.Host, target.Port, pending.Token)
                    .ConfigureAwait(false);
                _leases.Add(target, lease);
            }

            return new IPEndPoint(IPAddress.Loopback, lease.LocalPort);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Dispose every forward even if another forward's teardown fails.
            await Task.WhenAll(_leases.Values.Select(lease => lease.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
            _leases.Clear();
        }
        finally
        {
            _gate.Release();
            _lifetime.Dispose();
        }
    }
}
