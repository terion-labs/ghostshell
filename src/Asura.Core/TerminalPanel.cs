namespace Asura.Core;

public sealed record PanelBounds(int Column, int Row, int ColumnSpan, int RowSpan);

public sealed record TerminalPanel(
    PanelId Id,
    string Title,
    ConnectionId ConnectionId,
    PanelBounds Bounds,
    IReadOnlyList<CommandBlock> CommandBlocks,
    bool IsFocused);
