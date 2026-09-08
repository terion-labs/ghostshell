using System.IO.Pipes;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Terminal.Tests;

internal sealed class FakePortablePtyFactory : IPortablePtyFactory
{
    public FakePortablePtyConnection Connection { get; } = new();

    public TerminalLaunchRequest? Launch { get; private set; }

    public int InitialColumns { get; private set; }

    public int InitialRows { get; private set; }

    public ValueTask<IPortablePtyConnection> SpawnAsync(
        TerminalLaunchRequest launch,
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Launch = launch;
        InitialColumns = columns;
        InitialRows = rows;
        return ValueTask.FromResult<IPortablePtyConnection>(Connection);
    }
}

internal sealed class FakePortablePtyConnection : IPortablePtyConnection
{
    private readonly AnonymousPipeServerStream _outputWriter = new(
        PipeDirection.Out,
        HandleInheritability.None);
    private readonly AnonymousPipeClientStream _outputReader;
    private readonly CapturingWriteStream _input = new();
    private bool _disposed;
    private bool _exited;
    private int _exitCode;
    private readonly TaskCompletionSource _exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool DelayExit { get; set; }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exitCompletion.Task.WaitAsync(cancellationToken);

    public FakePortablePtyConnection()
    {
        _outputReader = new AnonymousPipeClientStream(
            PipeDirection.In,
            _outputWriter.GetClientHandleAsString());
    }

    public event EventHandler<PortablePtyExit>? ProcessExited;

    public Stream Reader => _outputReader;

    public Stream Writer => _input;

    public (int Columns, int Rows)? LastResize { get; private set; }

    public int KillCount { get; private set; }

    public Exception? DisposeFailure { get; set; }

    public string WrittenText => _input.Text;

    public int InputWriteCount => _input.WriteCount;

    public void PauseInputWrites() => _input.PauseWrites();

    public Task WaitForInputWriteAttemptAsync() => _input.WaitForWriteAttemptAsync();

    public void ResumeInputWrites() => _input.ResumeWrites();

    public void FailNextInputWrite(IOException exception) => _input.FailNextWrite(exception);

    public void PauseInputFlushes() => _input.PauseFlushes();

    public Task WaitForInputFlushAttemptAsync() => _input.WaitForFlushAttemptAsync();

    public void ResumeInputFlushes() => _input.ResumeFlushes();

    public void FailNextInputFlush(IOException exception) => _input.FailNextFlush(exception);

    public async ValueTask WriteOutputAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _outputWriter.WriteAsync(bytes);
        await _outputWriter.FlushAsync();
    }

    public async ValueTask WriteOutputBytesAsync(ReadOnlyMemory<byte> bytes)
    {
        await _outputWriter.WriteAsync(bytes);
        await _outputWriter.FlushAsync();
    }

    public bool TryGetExitCode(out int exitCode)
    {
        exitCode = _exitCode;
        return _exited;
    }

    public void Exit(int exitCode)
    {
        _exited = true;
        _exitCode = exitCode;
        _exitCompletion.TrySetResult();
        ProcessExited?.Invoke(this, new PortablePtyExit(exitCode));
    }

    public void Resize(int columns, int rows) => LastResize = (columns, rows);

    public void Kill()
    {
        KillCount++;
        if (!DelayExit)
        {
            Exit(137);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputReader.Dispose();
        _outputWriter.Dispose();
        _input.Dispose();
        if (!DelayExit && !_exited)
        {
            Exit(0);
        }
        var failure = DisposeFailure;
        DisposeFailure = null;
        if (failure is not null)
        {
            throw failure;
        }
    }
}

internal sealed class CapturingWriteStream : Stream
{
    private readonly object _gate = new();
    private readonly MemoryStream _stream = new();
    private TaskCompletionSource? _writeAttempted;
    private TaskCompletionSource? _writeRelease;
    private TaskCompletionSource? _flushAttempted;
    private TaskCompletionSource? _flushRelease;
    private IOException? _nextWriteFailure;
    private IOException? _nextFlushFailure;
    private int _writeCount;

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return Encoding.UTF8.GetString(_stream.ToArray());
            }
        }
    }

    public int WriteCount
    {
        get
        {
            lock (_gate)
            {
                return _writeCount;
            }
        }
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length
    {
        get
        {
            lock (_gate)
            {
                return _stream.Length;
            }
        }
    }

    public override long Position
    {
        get => Length;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task? release;
        lock (_gate)
        {
            _flushAttempted?.TrySetResult();
            release = _flushRelease?.Task;
        }

        if (release is not null)
        {
            await release.WaitAsync(cancellationToken);
        }

        IOException? failure;
        lock (_gate)
        {
            failure = _nextFlushFailure;
            _nextFlushFailure = null;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            _stream.Write(buffer, offset, count);
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? release;
        lock (_gate)
        {
            _writeAttempted?.TrySetResult();
            release = _writeRelease?.Task;
        }

        if (release is not null)
        {
            await release.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IOException? failure;
        lock (_gate)
        {
            failure = _nextWriteFailure;
            _nextWriteFailure = null;
            if (failure is null)
            {
                _stream.Write(buffer.Span);
                _writeCount++;
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    public void PauseWrites()
    {
        lock (_gate)
        {
            if (_writeRelease is { Task.IsCompleted: false })
            {
                throw new InvalidOperationException("Input writes are already paused.");
            }

            _writeAttempted = NewSignal();
            _writeRelease = NewSignal();
        }
    }

    public Task WaitForWriteAttemptAsync()
    {
        lock (_gate)
        {
            return (_writeAttempted
                ?? throw new InvalidOperationException("Input writes are not paused."))
                .Task
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    public void ResumeWrites()
    {
        lock (_gate)
        {
            _writeRelease?.TrySetResult();
        }
    }

    public void FailNextWrite(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _nextWriteFailure = exception;
        }
    }

    public void PauseFlushes()
    {
        lock (_gate)
        {
            if (_flushRelease is { Task.IsCompleted: false })
            {
                throw new InvalidOperationException("Input flushes are already paused.");
            }

            _flushAttempted = NewSignal();
            _flushRelease = NewSignal();
        }
    }

    public Task WaitForFlushAttemptAsync()
    {
        lock (_gate)
        {
            return (_flushAttempted
                ?? throw new InvalidOperationException("Input flushes are not paused."))
                .Task
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    public void ResumeFlushes()
    {
        lock (_gate)
        {
            _flushRelease?.TrySetResult();
        }
    }

    public void FailNextFlush(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _nextFlushFailure = exception;
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _stream.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
