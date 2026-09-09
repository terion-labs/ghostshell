namespace Asura.Application;

public enum SessionEventKind
{
    Created,
    StateChanged,
    AttachmentAdded,
    AttachmentRemoved,
    InputLeaseGranted,
    InputLeaseReleased,
    InputLeasePreempted,
    CloseRequested,
    Closed,
    Failed,
}
