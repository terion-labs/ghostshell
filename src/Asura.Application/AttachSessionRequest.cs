using Asura.Core;

namespace Asura.Application;

public sealed record AttachSessionRequest(
    SessionId SessionId,
    ClientId ClientId,
    AttachmentKind Kind,
    ViewportDescriptor Viewport,
    CapabilitySet ClientCapabilities);
