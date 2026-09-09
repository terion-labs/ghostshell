using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Binds a renderer only after the host validates that the attachment belongs
/// to the same session and calling client.
/// </summary>
public sealed record AttachBrowserRendererRequest(
    SessionId SessionId,
    AttachmentId AttachmentId,
    IBrowserRenderer Renderer);
