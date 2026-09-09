using Asura.Core;

namespace Asura.Application;

public sealed record AttachmentPresence(
    AttachmentId Id,
    SessionId SessionId,
    ClientId ClientId,
    AttachmentKind Kind,
    ViewportDescriptor Viewport,
    DateTimeOffset AttachedAtUtc);
