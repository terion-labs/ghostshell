using Asura.Core;

namespace Asura.Application;

public enum GlobalHotkeyRegistrationErrorCode
{
    Unsupported,
    InvalidGesture,
    Conflict,
    NativeFailure,
}

public sealed record GlobalHotkeyRegistrationError(
    GlobalHotkeyRegistrationErrorCode Code,
    string StableCode,
    string Message);

public abstract record GlobalHotkeyRegistrationResult
{
    private GlobalHotkeyRegistrationResult()
    {
    }

    public sealed record Success(KeyStroke Gesture) : GlobalHotkeyRegistrationResult;

    public sealed record Failure(GlobalHotkeyRegistrationError Error) : GlobalHotkeyRegistrationResult;
}

/// <summary>
/// Owns the process-wide Quick Terminal shortcut and its transient Escape dismissal capture.
/// Implementations must report conflicts instead of silently sharing a system gesture with another
/// registration. Escape capture is enabled only while Quick Terminal is visible.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? Pressed;

    event EventHandler? EscapePressed;

    KeyStroke? RegisteredGesture { get; }

    GlobalHotkeyRegistrationResult Register(KeyStroke gesture);

    void Unregister();

    GlobalHotkeyRegistrationResult BeginEscapeCapture();

    void EndEscapeCapture();
}
