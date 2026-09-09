namespace Asura.Monitoring;

/// <summary>
/// Executes a structured POSIX utility invocation on the panel's target host.
/// Local desktop sessions use a child process; connection adapters can provide
/// SSH, container, or WSL transports without changing monitor parsing.
/// </summary>
public interface IPosixCommandTransport
{
    ValueTask<PosixCommandResult> ExecuteAsync(
        PosixCommand command,
        CancellationToken cancellationToken);
}

public sealed record PosixCommand
{
    public PosixCommand(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumOutputCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Command timeout must be positive.");
        }

        if (maximumOutputCharacters is <= 0 or > 2 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutputCharacters),
                maximumOutputCharacters,
                "Command output must be bounded to at most 2 MiB of text.");
        }

        if (executable.Contains('\0')
            || arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new ArgumentException(
                "Process paths and arguments cannot contain NUL characters.");
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Timeout = timeout;
        MaximumOutputCharacters = maximumOutputCharacters;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }

    public int MaximumOutputCharacters { get; }
}

public enum PosixCommandOutcome
{
    Exited,
    StartFailed,
    TimedOut,
    Cancelled,
}

public sealed record PosixCommandResult(
    PosixCommandOutcome Outcome,
    int? ExitCode,
    string StandardOutput);
