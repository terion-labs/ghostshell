namespace Asura.Application;

/// <summary>
/// Construction-time aggregate for terminal engines that provide every
/// application terminal port.
/// </summary>
/// <remarks>
/// Runtime consumers should depend on the narrow port used by an operation.
/// The aggregate remains as the terminal-factory return type so existing engine
/// implementations and callers retain source compatibility.
/// </remarks>
public interface ITerminalPanelSession :
    ITerminalProcess,
    ITerminalState,
    ITerminalRendererAttachment,
    ITerminalRenderState,
    ITerminalAutomation
{
    // These two operations occur in two capability views. Redeclaration keeps
    // calls through the compatibility aggregate unambiguous.
    new ValueTask WriteAsync(string text, CancellationToken cancellationToken);

    new ValueTask<TerminalScreenSnapshot> ReadScreenAsync(
        CancellationToken cancellationToken);
}
