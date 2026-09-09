using System.Security.Cryptography;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Stable, non-secret identity of an app-owned terminal multiplexer session.
/// The identity survives replacement of the local PTY and application recovery.
/// </summary>
public sealed record TerminalMultiplexerSession
{
    public const int MaximumSessionNameLength = 64;
    public const string NamePrefix = "asura-";

    public TerminalMultiplexerSession(
        TerminalMultiplexingMode mode,
        string sessionName,
        bool isEstablished = false)
    {
        if (mode != TerminalMultiplexingMode.Automatic)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Only automatic remote multiplexer sessions have a runtime identity.");
        }

        if (!IsValidSessionName(sessionName))
        {
            throw new ArgumentException(
                "A terminal multiplexer session name must be a bounded Asura identifier.",
                nameof(sessionName));
        }

        Mode = mode;
        SessionName = sessionName;
        IsEstablished = isEstablished;
    }

    public TerminalMultiplexingMode Mode { get; }

    public string SessionName { get; }

    /// <summary>
    /// Once established, reconnect must resume only. Silently creating a fresh
    /// shell would present lost remote state as a successful restoration.
    /// </summary>
    public bool IsEstablished { get; }

    public TerminalMultiplexerSession MarkEstablished() =>
        IsEstablished ? this : new(Mode, SessionName, isEstablished: true);

    public static TerminalMultiplexerSession CreateAutomatic() => new(
        TerminalMultiplexingMode.Automatic,
        NamePrefix + RandomNumberGenerator.GetHexString(16, lowercase: true));

    public static bool IsValidSessionName(string? value) =>
        value is not null
        && value.Length is > 0 and <= MaximumSessionNameLength
        && value.StartsWith(NamePrefix, StringComparison.Ordinal)
        && value.All(character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character == '-');
}
