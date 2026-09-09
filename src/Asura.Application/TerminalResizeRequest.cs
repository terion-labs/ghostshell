using Asura.Core;

namespace Asura.Application;

public sealed record TerminalResizeRequest(
    SessionId SessionId,
    AttachmentId AttachmentId,
    ViewportDescriptor Viewport);
