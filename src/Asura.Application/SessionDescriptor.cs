using Asura.Core;

namespace Asura.Application;

public sealed record SessionDescriptor(
    SessionId Id,
    PanelKind Kind,
    SessionLifecycle Lifecycle,
    SessionHealth Health,
    SessionOwner Owner,
    CapabilitySet Capabilities,
    long Revision,
    bool HasActiveWork,
    string StatusDetail,
    SessionFailure? Failure = null,
    TerminalSessionMetadata? TerminalMetadata = null,
    FileSessionMetadata? FileMetadata = null,
    BrowserSessionMetadata? BrowserMetadata = null,
    GitSessionMetadata? GitMetadata = null);
