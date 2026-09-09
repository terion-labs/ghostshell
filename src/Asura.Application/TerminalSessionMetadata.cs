using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Trusted, bounded context for one live terminal session. The connection identity and
/// initial directory are immutable; SessionHost may advance the current directory from
/// a canonical terminal-state read.
/// </summary>
public sealed record TerminalSessionMetadata
{
    public TerminalSessionMetadata(
        ConnectionId? connectionId,
        string connectionBoundary,
        string? initialWorkingDirectory,
        string? currentWorkingDirectory,
        TerminalMultiplexerSession? multiplexerSession = null)
    {
        TerminalConnectionMetadata.ValidateConnectionId(
            connectionId,
            nameof(connectionId));
        var launchMetadata = new TerminalConnectionMetadata(
            connectionBoundary,
            initialWorkingDirectory);
        ConnectionId = connectionId;
        ConnectionBoundary = launchMetadata.ConnectionBoundary;
        InitialWorkingDirectory = launchMetadata.InitialWorkingDirectory;
        CurrentWorkingDirectory = TerminalConnectionMetadata.CopyWorkingDirectory(
            currentWorkingDirectory,
            nameof(currentWorkingDirectory));
        MultiplexerSession = multiplexerSession;
    }

    public ConnectionId? ConnectionId { get; }

    public string ConnectionBoundary { get; }

    public string? InitialWorkingDirectory { get; }

    public string? CurrentWorkingDirectory { get; }

    public TerminalMultiplexerSession? MultiplexerSession { get; }

    public TerminalSessionMetadata WithCurrentWorkingDirectory(string workingDirectory) =>
        new(
            ConnectionId,
            ConnectionBoundary,
            InitialWorkingDirectory,
            workingDirectory,
            MultiplexerSession);

    public static TerminalSessionMetadata FromLaunch(TerminalLaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var connection = launch.ConnectionMetadata;
        var boundary = connection?.ConnectionBoundary
            ?? (launch.ConnectionId is { } connectionId
                ? $"Connection {connectionId.Value}"
                : "Local terminal");
        var initialWorkingDirectory =
            connection?.InitialWorkingDirectory ?? launch.WorkingDirectory;
        try
        {
            return new TerminalSessionMetadata(
                launch.ConnectionId,
                boundary,
                initialWorkingDirectory,
                initialWorkingDirectory,
                launch.MultiplexerSession);
        }
        catch (ArgumentException) when (connection is null)
        {
            // Legacy/ad-hoc launches did not carry bounded presentation metadata.
            // Keep the session usable and expose an explicit unknown directory.
            return new TerminalSessionMetadata(
                launch.ConnectionId,
                boundary,
                initialWorkingDirectory: null,
                currentWorkingDirectory: null,
                launch.MultiplexerSession);
        }
    }
}
