using Asura.Core;

namespace Asura.Application;

public sealed record AttachTerminalRendererRequest(
    SessionId SessionId,
    AttachmentId AttachmentId,
    NativeRendererHost RendererHost);
