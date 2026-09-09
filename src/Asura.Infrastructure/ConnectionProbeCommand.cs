namespace Asura.Infrastructure;

/// <summary>
/// A structured, non-secret process invocation. Arguments are never interpreted by a shell.
/// </summary>
public sealed record ConnectionProbeCommand
{
    public ConnectionProbeCommand(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Probe timeout must be positive.");
        }

        if (executable.Contains('\0') || arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new ArgumentException("Process paths and arguments cannot contain NUL characters.");
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Timeout = timeout;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }
}
