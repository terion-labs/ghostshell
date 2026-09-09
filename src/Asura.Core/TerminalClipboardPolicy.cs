namespace Asura.Core;

public sealed record TerminalClipboardPolicy
{
    public TerminalClipboardPolicy(
        TerminalClipboardAccess readAccess,
        TerminalClipboardAccess writeAccess,
        TerminalPasteSafetyPolicy pasteSafety)
    {
        if (!Enum.IsDefined(readAccess))
        {
            throw new ArgumentOutOfRangeException(nameof(readAccess), readAccess, "Unknown clipboard-read policy.");
        }

        if (!Enum.IsDefined(writeAccess))
        {
            throw new ArgumentOutOfRangeException(nameof(writeAccess), writeAccess, "Unknown clipboard-write policy.");
        }

        if (!Enum.IsDefined(pasteSafety))
        {
            throw new ArgumentOutOfRangeException(nameof(pasteSafety), pasteSafety, "Unknown paste-safety policy.");
        }

        ReadAccess = readAccess;
        WriteAccess = writeAccess;
        PasteSafety = pasteSafety;
    }

    public TerminalClipboardAccess ReadAccess { get; }

    public TerminalClipboardAccess WriteAccess { get; }

    // Retained only for deserializing and round-tripping existing profiles.
    // Human paste is explicit input; this retired setting has no runtime effect.
    public TerminalPasteSafetyPolicy PasteSafety { get; }

    public static TerminalClipboardPolicy Default { get; } = new(
        TerminalClipboardAccess.Ask,
        TerminalClipboardAccess.Allow,
        TerminalPasteSafetyPolicy.ProtectUnsafe);
}
