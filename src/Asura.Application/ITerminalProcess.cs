namespace Asura.Application;

/// <summary>
/// Owns the terminal process lifecycle and its raw PTY-facing operations.
/// </summary>
/// <remarks>
/// <see cref="Launch"/> is immutable for the process lifetime. It carries the
/// non-secret environment and durable connection identity used to create the
/// process, while <see cref="IPanelSession.Id"/> is its runtime identity.
/// </remarks>
public interface ITerminalProcess : IPanelSession
{
    TerminalLaunchRequest Launch { get; }

    ValueTask ResizeAsync(
        ViewportDescriptor viewport,
        CancellationToken cancellationToken);

    ValueTask WriteAsync(string text, CancellationToken cancellationToken);
}
