using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal interface IWorkspaceTcpConnector
{
    ValueTask<Stream> ConnectAsync(
        WorkspaceNetworkPlacement placement,
        string host,
        int port,
        CancellationToken cancellationToken);
}

internal sealed class WorkspaceTcpConnector(
    IWorkspaceIsolationProvider? isolationProvider) : IWorkspaceTcpConnector
{
    public async ValueTask<Stream> ConnectAsync(
        WorkspaceNetworkPlacement placement,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        cancellationToken.ThrowIfCancellationRequested();
        return placement switch
        {
            WorkspaceNetworkPlacement.HostPlacement =>
                await ConnectHostAsync(host, port, cancellationToken).ConfigureAwait(false),
            WorkspaceNetworkPlacement.IsolatedPlacement isolated =>
                ConnectIsolated(isolated.Binding, host, port),
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, null),
        };
    }

    private static async ValueTask<Stream> ConnectHostAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new OwnedTcpStream(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private Stream ConnectIsolated(
        WorkspaceIsolationBinding binding,
        string host,
        int port)
    {
        if (isolationProvider is null)
        {
            throw new WorkspaceNetworkTransportException(
                "The workspace isolation runtime is unavailable.");
        }

        var launch = isolationProvider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                [
                    "-c",
                    "if command -v nc >/dev/null 2>&1; then exec nc \"$1\" \"$2\"; elif [ -x /bin/bash ]; then exec /bin/bash -c 'exec 3<>\"/dev/tcp/$1/$2\"; cat <&3 & cat >&3; wait' ghostshell-tcp \"$1\" \"$2\"; else exit 127; fi",
                    "ghostshell-tcp",
                    host,
                    port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                mode: WorkspaceProcessMode.Interactive));
        if (launch is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure failure)
        {
            throw new WorkspaceNetworkTransportException(failure.Error.Message);
        }

        var processLaunch =
            ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)launch).Value;
        var start = new ProcessStartInfo
        {
            FileName = processLaunch.Executable,
            WorkingDirectory = processLaunch.HostWorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in processLaunch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in processLaunch.Environment)
        {
            start.Environment[name] = value;
        }

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new WorkspaceNetworkTransportException(
                    "The workspace network relay could not be started.");
            }

            process.BeginErrorReadLine();
            return new ProcessDuplexStream(process);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            process.Dispose();
            throw new WorkspaceNetworkTransportException(
                "The workspace network relay could not be started.",
                exception);
        }
    }

    private sealed class OwnedTcpStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProcessDuplexStream(Process process) : Stream
    {
        private readonly Stream _input = process.StandardInput.BaseStream;
        private readonly Stream _output = process.StandardOutput.BaseStream;
        private int _disposed;

        public override bool CanRead => _output.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _input.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _input.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _input.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            _output.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _output.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            _input.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _input.Dispose();
                _output.Dispose();
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class WorkspaceNetworkTransportException : IOException
{
    public WorkspaceNetworkTransportException(string message)
        : base(message)
    {
    }

    public WorkspaceNetworkTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
