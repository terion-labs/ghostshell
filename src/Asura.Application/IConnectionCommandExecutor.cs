using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Executes one bounded, non-interactive command on a connection target.
/// Callers provide argv as data; connection adapters own authentication,
/// host verification, and the platform-specific process invocation.
/// </summary>
public interface IConnectionCommandExecutor
{
    ValueTask<ConnectionCommandResult> ExecuteAsync(
        ConnectionCommand request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a command whose standard output is binary. Docker's archive
    /// endpoint is exposed by the CLI this way, so decoding it as terminal text
    /// would corrupt file names and contents before the Docker adapter sees it.
    /// </summary>
    ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
        ConnectionBinaryCommand request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lets a caller consume standard output as it arrives. The consumer must
    /// read through end-of-stream; this keeps large archives bounded by the
    /// consumer's own state instead of by an arbitrary capture buffer.
    /// </summary>
    ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
        ConnectionBinaryCommand request,
        Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
        CancellationToken cancellationToken);
}

/// <summary>
/// Plans a structured, non-interactive command through a connection runtime.
/// Isolation runtimes implement this so command-backed panels cannot bypass
/// their execution boundary merely because the durable connection is local.
/// </summary>
public interface IConnectionCommandRuntime
{
    ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Plans a long-lived command whose standard input and output carry a duplex byte stream.
    /// Workspace providers must preserve stdin for the lifetime of the process without
    /// allocating a terminal.
    /// </summary>
    ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed record ConnectionCommand
{
    public ConnectionCommand(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumOutputCharacters)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maximumOutputCharacters is <= 0 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        }

        if (executable.Contains('\0', StringComparison.Ordinal)
            || arguments.Any(argument => argument is null
                || argument.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Command argv cannot contain NUL characters.");
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Timeout = timeout;
        MaximumOutputCharacters = maximumOutputCharacters;
    }

    public ConnectionProfile Connection { get; }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }

    public int MaximumOutputCharacters { get; }
}

public enum ConnectionCommandOutcome
{
    Exited,
    StartFailed,
    TimedOut,
    Cancelled,
    ConnectionFailed,
}

public sealed record ConnectionCommandResult(
    ConnectionCommandOutcome Outcome,
    int? ExitCode,
    string StandardOutput,
    string StandardError = "",
    bool OutputTruncated = false);

public sealed record ConnectionBinaryCommand
{
    public ConnectionBinaryCommand(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumOutputBytes)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maximumOutputBytes is <= 0 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        }

        if (executable.Contains('\0', StringComparison.Ordinal)
            || arguments.Any(argument => argument is null
                || argument.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Command argv cannot contain NUL characters.");
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Timeout = timeout;
        MaximumOutputBytes = maximumOutputBytes;
    }

    public ConnectionProfile Connection { get; }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }

    public int MaximumOutputBytes { get; }
}

public sealed record ConnectionBinaryCommandResult(
    ConnectionCommandOutcome Outcome,
    int? ExitCode,
    ReadOnlyMemory<byte> StandardOutput,
    string StandardError = "",
    bool OutputTruncated = false);

public sealed record ConnectionStreamingCommandResult<T>(
    ConnectionCommandOutcome Outcome,
    int? ExitCode,
    T? Value,
    string StandardError = "");
