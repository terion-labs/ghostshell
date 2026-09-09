namespace Asura.Application;

/// <summary>
/// Selects the conservative fallback used when native shell integration cannot observe whether
/// the foreground process is waiting at an interactive shell prompt.
/// </summary>
public enum TerminalShellActivityFallback
{
    None,
    PromptShape,
}
