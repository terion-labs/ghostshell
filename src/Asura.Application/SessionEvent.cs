using Asura.Core;

namespace Asura.Application;

public sealed record SessionEvent(
    SessionId SessionId,
    long Sequence,
    long Revision,
    SessionEventKind Kind,
    int PayloadVersion,
    DateTimeOffset TimestampUtc,
    SessionDescriptor Descriptor,
    AttachmentPresence? Attachment = null,
    InputLease? InputLease = null,
    string? Detail = null);
