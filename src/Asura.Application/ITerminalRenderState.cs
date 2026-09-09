namespace Asura.Application;

/// <summary>
/// Publishes the immutable, viewport-scoped state consumed by a managed
/// terminal renderer.
/// </summary>
/// <remarks>
/// This is intentionally separate from <see cref="ITerminalAutomation"/>.
/// Automation reads are bounded, text-oriented snapshots; render reads retain
/// exact cell decorations, damage, cursor state, and Kitty image content.
/// </remarks>
public interface ITerminalRenderState
{
    ValueTask<TerminalRenderFrame> ReadRenderFrameAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TerminalRenderFrame>(
            new PlatformNotSupportedException(
                "This terminal engine does not expose a managed render frame."));
}
