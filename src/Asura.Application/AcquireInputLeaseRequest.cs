using Asura.Core;

namespace Asura.Application;

public sealed record AcquireInputLeaseRequest(
    SessionId SessionId,
    AttachmentId? AttachmentId,
    TimeSpan Duration);
