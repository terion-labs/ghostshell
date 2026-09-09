namespace Asura.Application;

/// <summary>
/// Attaches a platform rendering and input surface to a terminal session.
/// </summary>
public interface ITerminalRendererAttachment
{
    ValueTask AttachRendererAsync(
        NativeRendererHost rendererHost,
        CancellationToken cancellationToken);

    ValueTask DetachRendererAsync(CancellationToken cancellationToken);

    ValueTask FocusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reports that the renderer no longer owns keyboard focus so terminals
    /// using DECSET 1004 receive the corresponding focus-out sequence.
    /// </summary>
    ValueTask BlurAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Applies typography and palette to the running terminal.
    ///
    /// Returns false where the engine cannot reconfigure a live surface, so the
    /// caller can leave the terminal as it is rather than restarting the session
    /// and losing its scrollback to a font change.
    /// </summary>
    ValueTask<bool> UpdateRenderProfileAsync(
        TerminalRenderProfileSnapshot renderProfile,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}
