namespace Asura.Core;

public enum TerminalMultiplexingMode
{
    Disabled = 0,
    Automatic = 1,

    // Keep the Screen-only preview name as the stable value-1 wire alias until
    // persisted workspace and recovery payloads have aged out.
    [Obsolete("Use Automatic. The enabled mode now prefers tmux and falls back to GNU Screen.")]
    GnuScreen = Automatic,
}
