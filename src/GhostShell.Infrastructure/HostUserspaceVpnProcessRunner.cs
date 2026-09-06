using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GhostShell.Infrastructure;

internal sealed record HostVpnProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    ReadOnlyMemory<byte> StandardInput);

internal sealed record HostVpnCommandResult(int ExitCode, string StandardOutput, string StandardError = "")
{
    public string Diagnostic => string.Concat(StandardOutput, "\n", StandardError);
}

internal interface IHostVpnProcess : IAsyncDisposable
{
    bool HasExited { get; }

    int? ExitCode { get; }

    string Diagnostic { get; }

    string StandardOutput { get; }

    string StandardError { get; }

    Task<bool> RouteReady { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IHostVpnProcessRunner
{
    ValueTask<IHostVpnProcess> StartAsync(
        HostVpnProcessRequest request,
        CancellationToken cancellationToken);

    ValueTask<HostVpnCommandResult> RunAsync(
        HostVpnProcessRequest request,
        CancellationToken cancellationToken);

    ValueTask<bool> WaitForTcpListenerAsync(
        IHostVpnProcess process,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class HostUserspaceVpnProcessRunner : IHostVpnProcessRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async ValueTask<IHostVpnProcess> StartAsync(
        HostVpnProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = CreateStartInfo(request) };
        try
        {
            if (!process.Start())
            {
                throw new IOException("The userspace VPN process could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new IOException("The userspace VPN process could not be started.", exception);
        }

        var running = new RunningHostVpnProcess(process);
        try
        {
            await running.WriteStandardInputAsync(request.StandardInput, cancellationToken)
                .ConfigureAwait(false);
            return running;
        }
        catch
        {
            await running.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<HostVpnCommandResult> RunAsync(
        HostVpnProcessRequest request,
        CancellationToken cancellationToken)
    {
        await using var process = await StartAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new HostVpnCommandResult(
            process.ExitCode ?? -1,
            process.StandardOutput,
            process.StandardError);
    }

    public async ValueTask<bool> WaitForTcpListenerAsync(
        IHostVpnProcess process,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (!process.HasExited)
            {
                using var client = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token)
                        .ConfigureAwait(false);
                    return true;
                }
                catch (SocketException)
                {
                }

                await Task.Delay(PollInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static ProcessStartInfo CreateStartInfo(HostVpnProcessRequest request)
    {
        var start = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private sealed class RunningHostVpnProcess : IHostVpnProcess
    {
        private const int MaximumDiagnosticCharacters = 32 * 1024;
        private readonly Process _process;
        private readonly object _diagnosticGate = new();
        private readonly StringBuilder _output = new(MaximumDiagnosticCharacters);
        private readonly StringBuilder _error = new(MaximumDiagnosticCharacters);
        private readonly Task _standardOutput;
        private readonly Task _standardError;
        private readonly TaskCompletionSource<bool> _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public RunningHostVpnProcess(Process process)
        {
            _process = process;
            _standardOutput = DrainAsync(process.StandardOutput, _output, _routeReady);
            _standardError = DrainAsync(process.StandardError, _error, readiness: null);
        }

        public bool HasExited => _process.HasExited;

        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public Task<bool> RouteReady => _routeReady.Task;

        public string Diagnostic
        {
            get
            {
                lock (_diagnosticGate)
                {
                    return string.Concat(_output.ToString(), "\n", _error.ToString());
                }
            }
        }

        public string StandardOutput
        {
            get
            {
                lock (_diagnosticGate)
                {
                    return _output.ToString();
                }
            }
        }

        public string StandardError
        {
            get
            {
                lock (_diagnosticGate)
                {
                    return _error.ToString();
                }
            }
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(_standardOutput, _standardError).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                }
            }

            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(_standardOutput, _standardError).ConfigureAwait(false);
            }
            finally
            {
                _process.Dispose();
            }
        }

        public async ValueTask WriteStandardInputAsync(
            ReadOnlyMemory<byte> input,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!input.IsEmpty)
                {
                    await _process.StandardInput.BaseStream.WriteAsync(input, cancellationToken)
                        .ConfigureAwait(false);
                    await _process.StandardInput.BaseStream.FlushAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _process.StandardInput.Close();
            }
        }

        private async Task DrainAsync(StreamReader reader, StringBuilder destination, TaskCompletionSource<bool>? readiness)
        {
            var buffer = new char[2048];
            var line = new StringBuilder(128);
            var overlong = false;
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    if (readiness is not null && !readiness.Task.IsCompleted)
                    {
                        for (var index = 0; index < read; index++)
                        {
                            var character = buffer[index];
                            if (character == '\n')
                            {
                                if (!overlong && string.Equals(line.ToString().TrimEnd('\r'),
                                        "READY v1 socks5 split-routes", StringComparison.Ordinal))
                                {
                                    readiness.TrySetResult(true);
                                }

                                line.Clear();
                                overlong = false;
                            }
                            else if (line.Length < 128)
                            {
                                line.Append(character);
                            }
                            else
                            {
                                overlong = true;
                            }
                        }
                    }

                    lock (_diagnosticGate)
                    {
                        var remaining = MaximumDiagnosticCharacters - destination.Length;
                        if (remaining > 0)
                        {
                            destination.Append(buffer, 0, Math.Min(read, remaining));
                        }
                    }
                }
            }
            finally
            {
                readiness?.TrySetResult(false);
            }
        }
    }
}
