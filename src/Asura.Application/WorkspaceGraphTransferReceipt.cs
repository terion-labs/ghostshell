using Asura.Core;

namespace Asura.Application;

public sealed record SessionOwnershipTransferReceipt(
    SessionId SessionId,
    SessionOwner Source,
    SessionOwner Destination);

public sealed record WorkspaceGraphTransferReceipt(
    Guid TransferId,
    WorkspaceGraphSnapshot Source,
    WorkspaceGraphSnapshot Destination,
    TabInstanceId TabId,
    PanelInstanceId? PanelId,
    IReadOnlyList<SessionOwnershipTransferReceipt> Sessions);
