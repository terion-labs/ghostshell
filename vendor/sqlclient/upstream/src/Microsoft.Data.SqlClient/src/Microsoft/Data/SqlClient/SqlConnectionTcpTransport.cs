// GhostShell extension to the pinned MIT-licensed SqlClient source.
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.ProviderBase;

namespace Microsoft.Data.SqlClient
{
    /// <summary>Owns the physical TCP route and pool identity for a connection.</summary>
    public sealed class SqlConnectionTcpTransport
    {
        private readonly Func<string, int, bool, SqlConnectionIPAddressPreference, CancellationToken, Task<Socket>> _connect;

        /// <summary>The factory must honor cancellation and return a connected socket owned by SqlClient.</summary>
        public SqlConnectionTcpTransport(
            Func<string, int, bool, SqlConnectionIPAddressPreference, CancellationToken, Task<Socket>> connect,
            CancellationToken lifetime)
        {
            _connect = connect ?? throw new ArgumentNullException(nameof(connect));
            Lifetime = lifetime;
        }

        /// <summary>Cancelling the route invalidates its pooled connections and future opens.</summary>
        public CancellationToken Lifetime { get; }

        internal Socket Connect(string host, int port, bool parallel, SqlConnectionIPAddressPreference preference, TimeoutTimer timeout)
        {
            Lifetime.ThrowIfCancellationRequested();
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
            if (!timeout.IsInfinite)
            {
                deadline.CancelAfter(TimeSpan.FromMilliseconds(timeout.MillisecondsRemaining));
            }

            Task<Socket> pending = _connect(host, port, parallel, preference, deadline.Token)
                ?? throw new InvalidOperationException("The routed TCP factory returned no operation.");
            Socket socket;
            try
            {
                socket = pending.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // A factory which completes after the deadline must not leak its socket.
                _ = DisposeLateSocketAsync(pending);
                throw;
            }

            if (socket == null)
            {
                throw new InvalidOperationException("The routed TCP factory returned no socket.");
            }
            if (deadline.IsCancellationRequested || !socket.Connected)
            {
                socket.Dispose();
                deadline.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The routed TCP factory returned a disconnected socket.");
            }
            return socket;
        }

        private static async Task DisposeLateSocketAsync(Task<Socket> pending)
        {
            try
            {
                Socket socket = await pending.ConfigureAwait(false);
                socket?.Dispose();
            }
            catch
            {
                // Observe a failed abandoned connect. Its original failure is already returned.
            }
        }
    }
}
