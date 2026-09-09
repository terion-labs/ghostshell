namespace Asura.Application;

public sealed record AttachmentResult(
    AttachmentPresence Attachment,
    SessionSnapshot Snapshot,
    CapabilityNegotiation Capabilities,
    long EventCursor);
