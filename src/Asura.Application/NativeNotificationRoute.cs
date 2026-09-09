using Asura.Core;

namespace Asura.Application;

/// <summary>The runtime destination opened when a native notification is activated.</summary>
public sealed record NativeNotificationRoute(
    WorkspaceInstanceId WorkspaceId,
    TabInstanceId? TabId = null,
    PanelInstanceId? PanelId = null);
