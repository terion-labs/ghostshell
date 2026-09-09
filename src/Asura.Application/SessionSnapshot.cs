namespace Asura.Application;

public sealed record SessionSnapshot(
    SessionDescriptor Descriptor,
    long LastSequence,
    IReadOnlyList<AttachmentPresence> Attachments,
    InputLease? InputLease);
