namespace Asura.Core;

public sealed record ScreenPanelDefinition(
    ScreenPanelId Id,
    LayoutSlotId SlotId,
    ScreenPanelKind Kind,
    string? Title,
    ConnectionId? ConnectionId,
    PanelStartupBehavior Startup,
    FileProviderProfileId? FileProviderProfileId = null);
