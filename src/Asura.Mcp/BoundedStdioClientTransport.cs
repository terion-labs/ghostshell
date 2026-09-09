using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Asura.Mcp;

/// <summary>
/// Process and framing boundary for the official MCP client.
/// </summary>
/// <remarks>
/// ModelContextProtocol.Core 1.3.0 owns JSON-RPC and MCP lifecycle, but its stdio
/// transport always inherits ambient environment variables and does not bound input
/// lines or retained stderr. This transport supplies those missing boundary controls.
/// </remarks>
internal sealed class BoundedStdioClientTransport(
    McpStdioServerLaunch launch,
    McpSessionOptions options) : IMcpClientTransportBoundary
{
    private BoundedStdioSessionTransport? _session;
    private int _cleanupUncertain;

    public string Name => "Asura MCP stdio";

    public McpStderrDiagnostics Diagnostics =>
        _session?.Diagnostics ?? new(0, 0, false, false);

    public bool CleanupUncertain =>
        Volatile.Read(ref _cleanupUncertain) != 0
        || _session?.CleanupUncertain == true;

    public void ResetIncomingMessageBudget()
    {
        var session = _session
            ?? throw new InvalidOperationException(
                "The MCP transport is not connected.");
        session.ResetIncomingMessageBudget();
    }

    public ValueTask DisposeAsync() =>
        _session?.DisposeAsync() ?? ValueTask.CompletedTask;

    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null)
        {
            throw new InvalidOperationException("The MCP transport can only be connected once.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory,
            StandardInputEncoding = BoundedStdioSessionTransport.StrictUtf8,
            StandardOutputEncoding = BoundedStdioSessionTransport.StrictUtf8,
            StandardErrorEncoding = BoundedStdioSessionTransport.StrictUtf8,
        };

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var pair in launch.Environment)
        {
            startInfo.Environment.Add(pair.Key, pair.Value);
        }

        Process? process = null;
        var processStarted = false;
        try
        {
            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            if (!process.Start())
            {
                throw new IOException("The process API did not start the MCP server.");
            }

            processStarted = true;
            _session = new BoundedStdioSessionTransport(process, options);
            return Task.FromResult<ITransport>(_session);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (processStarted && !TryTerminate(process))
            {
                Volatile.Write(ref _cleanupUncertain, 1);
            }

            process?.Dispose();
            throw new McpTransportFailureException(
                McpErrorCode.LaunchFailed,
                "The MCP server process could not be started.",
                exception);
        }
        finally
        {
            startInfo.Environment.Clear();
            launch.ForgetEnvironment();
        }
    }

    private static bool TryTerminate(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return false;
        }
    }
}

internal sealed class BoundedStdioSessionTransport : ITransport
{
    private static readonly byte[] Newline = [(byte)'\n'];
    private readonly Process _process;
    private readonly McpSessionOptions _options;
    private readonly McpStderrMonitor _stderr;
    private readonly Channel<JsonRpcMessage> _messages;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private int _disposed;
    private int _cleanupUncertain;
    private int _remainingIncomingMessages;

    public BoundedStdioSessionTransport(Process process, McpSessionOptions options)
    {
        _process = process;
        _options = options;
        _stderr = new(options.MaxStderrBytes, options.MaxStderrLines);
        _messages = Channel.CreateBounded<JsonRpcMessage>(
            new BoundedChannelOptions(options.MaxControlMessagesPerResponse)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        _remainingIncomingMessages = options.MaxControlMessagesPerResponse;
        _stdoutTask = ReadStdoutAsync();
        _stderrTask = DrainStderrAsync();
    }

    internal static UTF8Encoding StrictUtf8 { get; } =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string? SessionId => null;

    public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;

    public McpStderrDiagnostics Diagnostics => _stderr.Snapshot();

    public bool CleanupUncertain => Volatile.Read(ref _cleanupUncertain) != 0;

    internal void ResetIncomingMessageBudget()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Interlocked.Exchange(
            ref _remainingIncomingMessages,
            _options.MaxControlMessagesPerResponse);
    }

    public async Task SendMessageAsync(
        JsonRpcMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(message);

        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(
                message,
                McpSdkJson.TypeInfo<JsonRpcMessage>());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new McpTransportFailureException(
                McpErrorCode.InvalidMessage,
                "An outgoing MCP message could not be serialized.",
                exception);
        }

        if (json.Length > _options.MaxMessageBytes)
        {
            throw new McpTransportFailureException(
                McpErrorCode.MessageTooLarge,
                "An outgoing MCP message exceeded the configured byte limit.");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.BaseStream
                .WriteAsync(json, cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.BaseStream
                .WriteAsync(Newline, cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.BaseStream
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new McpTransportFailureException(
                McpErrorCode.TransportClosed,
                "The MCP transport closed while sending a message.",
                exception);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or IOException
                    or ObjectDisposedException)
            {
            }

            var exited = await WaitForExitAsync(_options.ShutdownGracePeriod)
                .ConfigureAwait(false);
            if (!exited)
            {
                if (!TryKillProcessTree())
                {
                    Volatile.Write(ref _cleanupUncertain, 1);
                }

                if (!await WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                {
                    Volatile.Write(ref _cleanupUncertain, 1);
                }
            }

            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            await AwaitReaderAsync(_stdoutTask).ConfigureAwait(false);
            await AwaitReaderAsync(_stderrTask).ConfigureAwait(false);
            _messages.Writer.TryComplete();
            try
            {
                _process.Dispose();
            }
            finally
            {
                _shutdown.Dispose();
                _sendLock.Dispose();
            }
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    private async Task ReadStdoutAsync()
    {
        Exception? completionError = null;
        try
        {
            var readBuffer = new byte[8192];
            var lineBuffer = new byte[_options.MaxMessageBytes];
            var lineLength = 0;
            while (true)
            {
                var read = await _process.StandardOutput.BaseStream
                    .ReadAsync(readBuffer, _shutdown.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    if (lineLength != 0)
                    {
                        throw new McpTransportFailureException(
                            McpErrorCode.InvalidMessage,
                            "The MCP server ended stdout in the middle of a message.");
                    }

                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        throw new McpTransportFailureException(
                            McpErrorCode.ProcessExited,
                            "The MCP server process closed stdout.");
                    }

                    break;
                }

                var consumed = 0;
                while (consumed < read)
                {
                    var newlineOffset = readBuffer
                        .AsSpan(consumed, read - consumed)
                        .IndexOf((byte)'\n');
                    var segmentLength = newlineOffset < 0
                        ? read - consumed
                        : newlineOffset;
                    if (lineLength + segmentLength > lineBuffer.Length)
                    {
                        throw new McpTransportFailureException(
                            McpErrorCode.MessageTooLarge,
                            "An incoming MCP message exceeded the configured byte limit.");
                    }

                    readBuffer.AsSpan(consumed, segmentLength)
                        .CopyTo(lineBuffer.AsSpan(lineLength));
                    lineLength += segmentLength;
                    consumed += segmentLength;
                    if (newlineOffset < 0)
                    {
                        continue;
                    }

                    consumed++;
                    if (lineLength > 0 && lineBuffer[lineLength - 1] == (byte)'\r')
                    {
                        lineLength--;
                    }

                    await PublishLineAsync(lineBuffer.AsMemory(0, lineLength))
                        .ConfigureAwait(false);
                    lineLength = 0;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            completionError = exception;
            _ = TryKillProcessTree();
        }
        finally
        {
            if (completionError is null)
            {
                _messages.Writer.TryComplete();
            }
            else
            {
                _messages.Writer.TryComplete(
                    new ClientTransportClosedException(
                        new ClientCompletionDetails { Exception = completionError }));
            }
        }
    }

    private async Task PublishLineAsync(ReadOnlyMemory<byte> utf8Line)
    {
        if (Interlocked.Decrement(ref _remainingIncomingMessages) < 0)
        {
            throw new McpTransportFailureException(
                McpErrorCode.LimitExceeded,
                "The MCP server exceeded the incoming control-message limit.");
        }

        if (utf8Line.IsEmpty
            || !McpJsonBudget.TryValidateDocument(
                utf8Line,
                _options.MaxJsonDepth,
                _options.MaxJsonNodes,
                out var document))
        {
            throw new McpTransportFailureException(
                McpErrorCode.InvalidMessage,
                "The MCP server emitted an invalid JSON message.");
        }

        using (document)
        {
            JsonRpcMessage? message;
            try
            {
                message = JsonSerializer.Deserialize(
                    utf8Line.Span,
                    McpSdkJson.TypeInfo<JsonRpcMessage>());
            }
            catch (JsonException exception)
            {
                throw new McpTransportFailureException(
                    McpErrorCode.InvalidMessage,
                    "The MCP server emitted an invalid JSON-RPC message.",
                    exception);
            }

            if (message is null)
            {
                throw new McpTransportFailureException(
                    McpErrorCode.InvalidMessage,
                    "The MCP server emitted an empty JSON-RPC message.");
            }

            await _messages.Writer
                .WriteAsync(message, _shutdown.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await _process.StandardError.BaseStream
                    .ReadAsync(buffer, _shutdown.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _stderr.Observe(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch
        {
            _stderr.MarkReadFailed();
        }
    }

    private async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            if (_process.HasExited)
            {
                return true;
            }

            using var timeoutSource = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                return _process.HasExited;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or ObjectDisposedException)
            {
                Volatile.Write(ref _cleanupUncertain, 1);
                return false;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or ObjectDisposedException)
        {
            Volatile.Write(ref _cleanupUncertain, 1);
            return false;
        }
    }

    private bool TryKillProcessTree()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or ObjectDisposedException)
        {
            Volatile.Write(ref _cleanupUncertain, 1);
            return false;
        }
    }

    private static async Task AwaitReaderAsync(Task reader)
    {
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException
                or OperationCanceledException
                or IOException)
        {
        }
    }
}
