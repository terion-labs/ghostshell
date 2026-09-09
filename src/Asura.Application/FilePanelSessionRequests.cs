using Asura.Core;

namespace Asura.Application;

public sealed record EnsureFilePanelSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    FilePanelLocation InitialLocation);

public sealed record FilePanelListHostRequest(
    SessionId SessionId,
    FilePanelListRequest Request);

public sealed record FilePanelStatHostRequest(
    SessionId SessionId,
    FilePanelLocation Location);

public sealed record FilePanelPreviewHostRequest(
    SessionId SessionId,
    FilePanelPreviewRequest Request);

public sealed record FilePanelCreateDirectoryHostRequest(
    SessionId SessionId,
    FilePanelCreateDirectoryRequest Request);

public sealed record FilePanelRenameHostRequest(
    SessionId SessionId,
    FilePanelRenameRequest Request);

public sealed record FilePanelDeleteHostRequest(
    SessionId SessionId,
    FilePanelDeleteRequest Request);

public sealed record FilePanelAccessControlHostRequest(
    SessionId SessionId,
    FilePanelAccessControlRequest Request);

public sealed record FilePanelSetAccessControlHostRequest(
    SessionId SessionId,
    FilePanelSetAccessControlRequest Request);

public sealed record FilePanelTransferEnqueueHostRequest(
    SessionId SessionId,
    FilePanelTransferRequest Request);

public sealed record FilePanelTransferCancelHostRequest(
    SessionId SessionId,
    FilePanelTransferId TransferId);

public sealed record FilePanelTransferRetryHostRequest(
    SessionId SessionId,
    FilePanelTransferId TransferId);
