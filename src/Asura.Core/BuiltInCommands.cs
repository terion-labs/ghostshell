namespace Asura.Core;

public static class BuiltInCommands
{
    private static readonly Lazy<CommandRegistry> RegistryFactory = new(CreateRegistry);

    public static CommandId NewTab { get; } = new("tab.new");
    public static CommandId SplitPanel { get; } = new("panel.split");
    public static CommandId FocusPanel { get; } = new("panel.focus");
    public static CommandId TogglePanelZoom { get; } = new("panel.zoom.toggle");
    public static CommandId ClosePanel { get; } = new("panel.close");
    public static CommandId RenameTab { get; } = new("tab.rename");
    public static CommandId CloseTab { get; } = new("tab.close");
    public static CommandId MoveTabLeft { get; } = new("tab.move-left");
    public static CommandId MoveTabRight { get; } = new("tab.move-right");
    public static CommandId MoveTabToWorkspace { get; } = new("tab.move-to-workspace");
    public static CommandId MovePanelToWorkspace { get; } = new("panel.move-to-workspace");
    public static CommandId NextTab { get; } = new("tab.next");
    public static CommandId PreviousTab { get; } = new("tab.previous");
    public static CommandId LastTab { get; } = new("tab.last");
    public static CommandId SelectTab { get; } = new("tab.select-position");
    public static CommandId SelectWorkspace { get; } = new("workspace.select-position");
    public static CommandId EnterTerminalCopyMode { get; } = new("terminal.copy-mode");
    public static CommandId SendPrefix { get; } = new("terminal.send-prefix");

    public static CommandId Copy { get; } = new("terminal.copy");
    public static CommandId Paste { get; } = new("terminal.paste");
    public static CommandId SelectAll { get; } = new("terminal.select-all");
    public static CommandId MoveWordLeft { get; } = new("terminal.word-left");
    public static CommandId MoveWordRight { get; } = new("terminal.word-right");
    public static CommandId DeleteWordBackward { get; } = new("terminal.delete-word-backward");
    public static CommandId DeleteWordForward { get; } = new("terminal.delete-word-forward");
    public static CommandId MoveToLineStart { get; } = new("terminal.line-start");
    public static CommandId MoveToLineEnd { get; } = new("terminal.line-end");
    public static CommandId Find { get; } = new("terminal.find");
    public static CommandId IncreaseFontSize { get; } = new("terminal.font-increase");
    public static CommandId DecreaseFontSize { get; } = new("terminal.font-decrease");
    public static CommandId ResetFontSize { get; } = new("terminal.font-reset");
    public static CommandId ClearScrollback { get; } = new("terminal.clear-scrollback");
    public static CommandId SendInterrupt { get; } = new("terminal.send-interrupt");
    public static CommandId SendEndOfFile { get; } = new("terminal.send-eof");
    public static CommandId ClearScreen { get; } = new("terminal.clear-screen");

    public static CommandRegistry Registry => RegistryFactory.Value;

    private static CommandRegistry CreateRegistry()
    {
        var orientationSchema = new CommandParameterSchema(
            [new CommandParameter("orientation", CommandParameterType.Choice, true, ["left-right", "top-bottom"])]);
        var directionSchema = new CommandParameterSchema(
            [new CommandParameter("direction", CommandParameterType.Choice, true, ["left", "right", "up", "down", "next"])]);
        var positionSchema = new CommandParameterSchema(
            [new CommandParameter("position", CommandParameterType.Integer, true)]);

        return new CommandRegistry(
        [
            Define(NewTab, "New tab", "Tabs", CommandContext.Workspace),
            Define(SplitPanel, "Split panel", "Panels", CommandContext.Panel, orientationSchema),
            Define(FocusPanel, "Focus panel", "Panels", CommandContext.Panel, directionSchema),
            Define(TogglePanelZoom, "Toggle panel zoom", "Panels", CommandContext.Panel),
            Define(ClosePanel, "Close panel", "Panels", CommandContext.Panel),
            Define(RenameTab, "Rename tab", "Tabs", CommandContext.Tab),
            Define(CloseTab, "Close tab", "Tabs", CommandContext.Tab),
            Define(MoveTabLeft, "Move tab left", "Tabs", CommandContext.Tab),
            Define(MoveTabRight, "Move tab right", "Tabs", CommandContext.Tab),
            Define(
                MoveTabToWorkspace,
                "Move tab to open workspace",
                "Tabs",
                CommandContext.Tab,
                positionSchema),
            Define(
                MovePanelToWorkspace,
                "Move panel to open workspace",
                "Panels",
                CommandContext.Panel,
                positionSchema),
            Define(NextTab, "Next tab", "Tabs", CommandContext.Tab),
            Define(PreviousTab, "Previous tab", "Tabs", CommandContext.Tab),
            Define(LastTab, "Last active tab", "Tabs", CommandContext.Tab),
            Define(SelectTab, "Select tab by position", "Tabs", CommandContext.Tab, positionSchema),
            Define(
                SelectWorkspace,
                "Select workspace by position",
                "Workspaces",
                CommandContext.Window,
                positionSchema),
            Define(EnterTerminalCopyMode, "Enter terminal copy mode", "Terminal", CommandContext.Terminal),
            Define(SendPrefix, "Send literal prefix", "Terminal", CommandContext.Terminal),
            Define(Copy, "Copy", "Terminal", CommandContext.Terminal),
            Define(Paste, "Paste", "Terminal", CommandContext.Terminal),
            Define(SelectAll, "Select visible terminal content", "Terminal", CommandContext.Terminal),
            Define(MoveWordLeft, "Move one word left", "Terminal", CommandContext.Terminal),
            Define(MoveWordRight, "Move one word right", "Terminal", CommandContext.Terminal),
            Define(DeleteWordBackward, "Delete previous word", "Terminal", CommandContext.Terminal),
            Define(DeleteWordForward, "Delete next word", "Terminal", CommandContext.Terminal),
            Define(MoveToLineStart, "Move to line start", "Terminal", CommandContext.Terminal),
            Define(MoveToLineEnd, "Move to line end", "Terminal", CommandContext.Terminal),
            Define(Find, "Find", "Terminal", CommandContext.Terminal),
            Define(IncreaseFontSize, "Increase font size", "Terminal", CommandContext.Terminal),
            Define(DecreaseFontSize, "Decrease font size", "Terminal", CommandContext.Terminal),
            Define(ResetFontSize, "Reset font size", "Terminal", CommandContext.Terminal),
            Define(ClearScrollback, "Clear scrollback", "Terminal", CommandContext.Terminal),
            Define(SendInterrupt, "Send interrupt", "Terminal", CommandContext.Terminal),
            Define(SendEndOfFile, "Send end of file", "Terminal", CommandContext.Terminal),
            Define(ClearScreen, "Clear screen", "Terminal", CommandContext.Terminal),
        ]);
    }

    private static CommandDefinition Define(
        CommandId id,
        string title,
        string category,
        CommandContext contexts,
        CommandParameterSchema? parameters = null)
    {
        var defaults = BuiltInKeymaps.All
            .SelectMany(profile => profile.Bindings)
            .Where(binding => binding.CommandId == id)
            .ToArray();

        return new CommandDefinition(id, title, category, contexts, parameters, defaults);
    }
}
