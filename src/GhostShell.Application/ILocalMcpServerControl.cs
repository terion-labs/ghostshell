namespace GhostShell.Application;

public sealed record LocalMcpServerState(
    bool Enabled = false,
    int Port = 18765,
    bool IsRunning = false,
    string? Error = null);

/// <summary>The application-wide MCP listener and its saved user preferences.</summary>
public interface ILocalMcpServerControl
{
    LocalMcpServerState State { get; }

    event EventHandler? Changed;

    Task ConfigureAsync(bool enabled, int port);

    Task<string> ReadTokenAsync();

    Task RotateTokenAsync();
}
