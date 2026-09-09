namespace Asura.Application;

/// <summary>
/// Binds the exact in-process platform surface to a logical browser session.
/// Attaching does not transfer ownership of the renderer object.
/// </summary>
public interface IBrowserRendererAttachment
{
    ValueTask AttachRendererAsync(
        IBrowserRenderer renderer,
        CancellationToken cancellationToken);

    ValueTask DetachRendererAsync(CancellationToken cancellationToken);
}
