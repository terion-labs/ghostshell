namespace Asura.Protocol;

public sealed record ProtocolTargets(
    string? WindowId = null,
    string? WorkspaceId = null,
    string? TabId = null,
    string? PanelId = null,
    string? SessionId = null,
    string? AttachmentId = null);
