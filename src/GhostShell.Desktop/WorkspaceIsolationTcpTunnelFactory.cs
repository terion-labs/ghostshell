using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class WorkspaceIsolationTcpTunnelFactory(
    IConnectionCommandRuntime commandRuntime) : IDatabaseTunnelFactory
{
    public ValueTask<IDatabaseTunnelLease> OpenAsync(
        ConnectionProfile connection,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IDatabaseTunnelLease>(new Tunnel(
            commandRuntime,
            connection,
            targetHost,
            targetPort));
    }

    private sealed class Tunnel : IDatabaseTunnelLease
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Thread _acceptThread;
        private readonly IConnectionCommandRuntime _commandRuntime;
        private readonly ConnectionProfile _connection;
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly TcpListener _listener;
        private int _disposed;

        public Tunnel(
            IConnectionCommandRuntime commandRuntime,
            ConnectionProfile connection,
            string targetHost,
            int targetPort)
        {
            _commandRuntime = commandRuntime;
            _connection = connection;
            _targetHost = targetHost;
            _targetPort = targetPort;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "GhostShell workspace database tunnel",
            };
            _acceptThread.Start();
        }

        public int LocalPort { get; }
        public bool IsClosed => Volatile.Read(ref _disposed) != 0 || !_acceptThread.IsAlive;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            _lifetime.Cancel();
            _listener.Stop();
            _acceptThread.Join();
            _lifetime.Dispose();
            await ValueTask.CompletedTask;
        }

        private void AcceptLoop()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                var connectionThread = new Thread(() =>
                    PumpSafely(client, _lifetime.Token))
                {
                    IsBackground = true,
                    Name = "GhostShell workspace database connection",
                };
                connectionThread.Start();
            }
        }

        private void PumpSafely(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                PumpAsync(client, cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
            }
            catch (Exception exception) when (exception is
                IOException or SocketException or ObjectDisposedException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                client.Dispose();
            }
        }

        private async Task PumpAsync(
            TcpClient client,
            CancellationToken cancellationToken)
        {
            using (client)
            {
                var planned = await _commandRuntime.PlanDuplexCommandAsync(
                    _connection,
                    "/bin/sh",
                    [
                        "-c",
                        "if command -v nc >/dev/null 2>&1; then exec nc \"$1\" \"$2\"; elif [ -x /bin/bash ]; then exec /bin/bash -c 'exec 3<>\"/dev/tcp/$1/$2\"; cat <&3 & cat >&3; wait' ghostshell-tcp \"$1\" \"$2\"; else echo 'TCP relay unavailable in workspace image' >&2; exit 127; fi",
                        "ghostshell-tcp",
                        _targetHost,
                        _targetPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ],
                    cancellationToken).ConfigureAwait(false);
                if (planned is not ConnectionRuntimeResult<TerminalLaunchRequest>.Success success)
                {
                    return;
                }

                var start = new ProcessStartInfo
                {
                    FileName = success.Value.Executable
                        ?? throw new InvalidOperationException("The tunnel plan has no executable."),
                    WorkingDirectory = success.Value.WorkingDirectory ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in success.Value.Arguments)
                {
                    start.ArgumentList.Add(argument);
                }

                foreach (var (name, value) in success.Value.Environment)
                {
                    start.Environment[name] = value;
                }

                var process = new Process { StartInfo = start };
                if (!process.Start())
                {
                    process.Dispose();
                    return;
                }

                process.BeginErrorReadLine();

                using var stream = client.GetStream();
                using var cancellation = cancellationToken.Register(() =>
                {
                    client.Dispose();
                    TryKill(process);
                });
                var upstream = new Thread(() => Copy(stream, process.StandardInput.BaseStream))
                {
                    IsBackground = true,
                    Name = "GhostShell workspace database upload",
                };
                var downstream = new Thread(() => Copy(process.StandardOutput.BaseStream, stream))
                {
                    IsBackground = true,
                    Name = "GhostShell workspace database download",
                };
                upstream.Start();
                downstream.Start();
                upstream.Join();
                process.StandardInput.Close();
                downstream.Join();
                TryKill(process);
                process.Dispose();
            }
        }

        private static void Copy(Stream source, Stream destination)
        {
            try
            {
                source.CopyTo(destination);
                destination.Flush();
            }
            catch (Exception exception) when (exception is
                IOException or ObjectDisposedException)
            {
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
