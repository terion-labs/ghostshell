namespace GhostShell.Core;

/// <summary>
/// Legacy serialized profile values. Retained for existing definitions only;
/// terminal paste no longer uses a configurable confirmation policy.
/// </summary>
public enum TerminalPasteSafetyPolicy
{
    ProtectUnsafe,
    ProtectUnsafeIncludingBracketed,
    AllowUnsafe,
}
