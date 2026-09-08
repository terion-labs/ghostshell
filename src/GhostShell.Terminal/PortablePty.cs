using GhostShell.Application;
using Porta.Pty;

namespace GhostShell.Terminal;

internal sealed record PortablePtyExit(int ExitCode);

internal interface IPortablePtyConnection : IDisposable
{
    event EventHandler<PortablePtyExit>? ProcessExited;

    Stream Reader { get; }

    Stream Writer { get; }

    bool TryGetExitCode(out int exitCode);

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Resize(int columns, int rows);

    void Kill();
}

internal interface IPortablePtyFactory
{
    ValueTask<IPortablePtyConnection> SpawnAsync(
        TerminalLaunchRequest launch,
        int columns,
        int rows,
        CancellationToken cancellationToken);
}

internal sealed class PortaPtyFactory : IPortablePtyFactory
{
    public async ValueTask<IPortablePtyConnection> SpawnAsync(
        TerminalLaunchRequest launch,
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        PortablePtyRuntime.EnsureInitialized();
        var executable = launch.Executable ?? ResolveDefaultShell();
        var workingDirectory = launch.WorkingDirectory ?? Environment.CurrentDirectory;
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The terminal working directory does not exist: {workingDirectory}");
        }

        var environment = CreateProcessEnvironment(launch.Environment);
        var connection = await PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "GhostSHELL",
                    App = executable,
                    CommandLine = [.. launch.Arguments],
                    Cwd = workingDirectory,
                    Cols = columns,
                    Rows = rows,
                    Environment = environment,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return new PortaPtyConnection(connection);
    }

    internal static Dictionary<string, string> CreateProcessEnvironment(
        IReadOnlyDictionary<string, string> configured)
    {
        var environment = new Dictionary<string, string>(configured, StringComparer.Ordinal)
        {
            // These describe the emulator, not the app that happened to launch
            // GhostSHELL. Inheriting (for example) Warp's TERM_PROGRAM makes
            // terminal-aware tools choose escape sequences for the wrong host.
            // We intentionally advertise Ghostty compatibility because the parser,
            // shell integration, and notification protocols come from Ghostty.
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "ghostty"
        };
        // Do not pair the managed program name with a stale version inherited
        // from a different terminal (for example WarpTerminal).
        environment.Remove("TERM_PROGRAM_VERSION");
        // This advertises parser support only. Applications still own the state
        // they emit, and GhostSHELL continues to treat every payload as untrusted.
        environment[TerminalInteractiveStateProtocol.CapabilityEnvironmentVariable] =
            TerminalInteractiveStateProtocol.NotificationTitle;
        return environment;
    }

    private static string ResolveDefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("COMSPEC")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        var configured = Environment.GetEnvironmentVariable("SHELL");
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)
            ? configured
            : "/bin/sh";
    }
}

internal sealed class PortaPtyConnection : IPortablePtyConnection
{
    private readonly IPtyConnection _connection;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PortaPtyConnection(IPtyConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.ProcessExited += OnProcessExited;
        if (_connection.WaitForExit(0))
        {
            _exited.TrySetResult();
        }
    }

    public event EventHandler<PortablePtyExit>? ProcessExited;

    public Stream Reader => _connection.ReaderStream;

    public Stream Writer => _connection.WriterStream;

    public bool TryGetExitCode(out int exitCode)
    {
        if (_connection.WaitForExit(0))
        {
            exitCode = _connection.ExitCode;
            return true;
        }

        exitCode = default;
        return false;
    }

    public void Resize(int columns, int rows) => _connection.Resize(columns, rows);

    public void Kill() => _connection.Kill();

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void Dispose()
    {
        _connection.Dispose();
        if (_exited.Task.IsCompleted)
        {
            _connection.ProcessExited -= OnProcessExited;
        }
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs eventArgs)
    {
        _exited.TrySetResult();
        _connection.ProcessExited -= OnProcessExited;
        ProcessExited?.Invoke(this, new PortablePtyExit(eventArgs.ExitCode));
    }
}
