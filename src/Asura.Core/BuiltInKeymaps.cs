using System.Collections.Immutable;

namespace Asura.Core;

public static class BuiltInKeymaps
{
    private static readonly KeyStroke TmuxPrefix = new("B", KeyModifiers.Control);

    public static KeymapProfileId TmuxApplicationId { get; } = new("builtin.application.tmux");
    public static KeymapProfileId MacOsTerminalId { get; } = new("builtin.terminal.macos-native");
    public static KeymapProfileId WindowsTerminalId { get; } = new("builtin.terminal.windows-native");
    public static KeymapProfileId LinuxTerminalId { get; } = new("builtin.terminal.linux-native");

    public static KeymapProfile TmuxApplication { get; } = CreateTmuxApplication();
    public static KeymapProfile MacOsTerminal { get; } = CreateMacOsTerminal();
    public static KeymapProfile WindowsTerminal { get; } = CreateWindowsTerminal();
    public static KeymapProfile LinuxTerminal { get; } = CreateLinuxTerminal();

    public static ImmutableArray<KeymapProfile> All { get; } =
    [
        TmuxApplication,
        MacOsTerminal,
        WindowsTerminal,
        LinuxTerminal,
    ];

    public static KeymapProfile TerminalFor(HostOperatingSystem operatingSystem) => operatingSystem switch
    {
        HostOperatingSystem.MacOS => MacOsTerminal,
        HostOperatingSystem.Windows => WindowsTerminal,
        HostOperatingSystem.Linux => LinuxTerminal,
        _ => throw new ArgumentOutOfRangeException(nameof(operatingSystem), operatingSystem, null),
    };

    private static KeymapProfile CreateTmuxApplication()
    {
        var prefix = new PrefixConfiguration(
            TmuxPrefix,
            TimeSpan.FromMilliseconds(750),
            repeatable: true,
            FailedSequenceBehavior.DiscardAndShowHint);

        var bindings = new List<CommandBinding>
        {
            Prefixed(BuiltInCommands.NewTab, "C", CommandContext.Workspace),
            Prefixed(BuiltInCommands.SplitPanel, "%", CommandContext.Panel, ("orientation", "left-right")),
            Prefixed(BuiltInCommands.SplitPanel, "\"", CommandContext.Panel, ("orientation", "top-bottom")),
            Prefixed(BuiltInCommands.FocusPanel, "ARROWLEFT", CommandContext.Panel, ("direction", "left")),
            Prefixed(BuiltInCommands.FocusPanel, "ARROWRIGHT", CommandContext.Panel, ("direction", "right")),
            Prefixed(BuiltInCommands.FocusPanel, "ARROWUP", CommandContext.Panel, ("direction", "up")),
            Prefixed(BuiltInCommands.FocusPanel, "ARROWDOWN", CommandContext.Panel, ("direction", "down")),
            Prefixed(BuiltInCommands.FocusPanel, "O", CommandContext.Panel, ("direction", "next")),
            Prefixed(BuiltInCommands.TogglePanelZoom, "Z", CommandContext.Panel),
            Prefixed(BuiltInCommands.ClosePanel, "X", CommandContext.Panel),
            Prefixed(BuiltInCommands.RenameTab, ",", CommandContext.Tab),
            Prefixed(BuiltInCommands.CloseTab, "&", CommandContext.Tab),
            new(
                BuiltInCommands.MoveTabLeft,
                KeySequence.Of(
                    TmuxPrefix,
                    new KeyStroke("ARROWLEFT", KeyModifiers.Shift)),
                CommandContext.Tab),
            new(
                BuiltInCommands.MoveTabRight,
                KeySequence.Of(
                    TmuxPrefix,
                    new KeyStroke("ARROWRIGHT", KeyModifiers.Shift)),
                CommandContext.Tab),
            Prefixed(BuiltInCommands.NextTab, "N", CommandContext.Tab),
            Prefixed(BuiltInCommands.PreviousTab, "P", CommandContext.Tab),
            Direct(
                BuiltInCommands.PreviousTab,
                "ARROWLEFT",
                KeyModifiers.Meta | KeyModifiers.Alt,
                CommandContext.Tab),
            Direct(
                BuiltInCommands.NextTab,
                "ARROWRIGHT",
                KeyModifiers.Meta | KeyModifiers.Alt,
                CommandContext.Tab),
            Prefixed(BuiltInCommands.LastTab, "L", CommandContext.Tab),
            Prefixed(BuiltInCommands.EnterTerminalCopyMode, "[", CommandContext.Terminal),
            new(
                BuiltInCommands.SendPrefix,
                KeySequence.Of(TmuxPrefix, TmuxPrefix),
                CommandContext.Terminal),
        };

        for (var position = 0; position <= 9; position++)
        {
            bindings.Add(Prefixed(
                BuiltInCommands.SelectTab,
                position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CommandContext.Tab,
                ("position", position.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        for (var position = 0; position < 9; position++)
        {
            var key = (position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var argument = position.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bindings.Add(Direct(
                BuiltInCommands.SelectWorkspace,
                key,
                KeyModifiers.Meta,
                CommandContext.Window,
                ("position", argument)));
            bindings.Add(Direct(
                BuiltInCommands.MoveTabToWorkspace,
                key,
                KeyModifiers.Meta | KeyModifiers.Shift,
                CommandContext.Tab,
                ("position", argument)));
            bindings.Add(Direct(
                BuiltInCommands.MovePanelToWorkspace,
                key,
                KeyModifiers.Meta | KeyModifiers.Alt,
                CommandContext.Panel,
                ("position", argument)));
        }

        return new KeymapProfile(
            TmuxApplicationId,
            "tmux-like application",
            KeymapLayer.Application,
            bindings,
            prefix);
    }

    private static KeymapProfile CreateMacOsTerminal() => new(
        MacOsTerminalId,
        "macOS Native",
        KeymapLayer.Terminal,
        [
            Single(BuiltInCommands.Copy, "C", KeyModifiers.Meta),
            Single(BuiltInCommands.Paste, "V", KeyModifiers.Meta),
            Single(BuiltInCommands.SelectAll, "A", KeyModifiers.Meta),
            Single(BuiltInCommands.MoveWordLeft, "ARROWLEFT", KeyModifiers.Alt),
            Single(BuiltInCommands.MoveWordRight, "ARROWRIGHT", KeyModifiers.Alt),
            Single(BuiltInCommands.DeleteWordBackward, "BACKSPACE", KeyModifiers.Alt),
            Single(BuiltInCommands.DeleteWordForward, "DELETE", KeyModifiers.Alt),
            Single(BuiltInCommands.MoveToLineStart, "ARROWLEFT", KeyModifiers.Meta),
            Single(BuiltInCommands.MoveToLineEnd, "ARROWRIGHT", KeyModifiers.Meta),
            Single(BuiltInCommands.Find, "F", KeyModifiers.Meta),
            Single(BuiltInCommands.IncreaseFontSize, "+", KeyModifiers.Meta),
            Single(BuiltInCommands.DecreaseFontSize, "-", KeyModifiers.Meta),
            Single(BuiltInCommands.ResetFontSize, "0", KeyModifiers.Meta),
            // Meta+K is reserved by the shell Command Palette. Keep terminal
            // Clear Scrollback reachable without relying on focus-layer precedence.
            Single(
                BuiltInCommands.ClearScrollback,
                "K",
                KeyModifiers.Meta | KeyModifiers.Shift),
            Single(BuiltInCommands.SendInterrupt, "C", KeyModifiers.Control),
            Single(BuiltInCommands.SendEndOfFile, "D", KeyModifiers.Control),
            Single(BuiltInCommands.ClearScreen, "L", KeyModifiers.Control),
        ]);

    private static KeymapProfile CreateWindowsTerminal() => CreateControlShiftTerminalPreset(
        WindowsTerminalId,
        "Windows Native");

    private static KeymapProfile CreateLinuxTerminal() => CreateControlShiftTerminalPreset(
        LinuxTerminalId,
        "Linux Native");

    private static KeymapProfile CreateControlShiftTerminalPreset(KeymapProfileId id, string name) => new(
        id,
        name,
        KeymapLayer.Terminal,
        [
            Single(BuiltInCommands.Copy, "C", KeyModifiers.Control | KeyModifiers.Shift),
            Single(BuiltInCommands.Paste, "V", KeyModifiers.Control | KeyModifiers.Shift),
            Single(BuiltInCommands.SelectAll, "A", KeyModifiers.Control | KeyModifiers.Shift),
            Single(BuiltInCommands.MoveWordLeft, "ARROWLEFT", KeyModifiers.Control),
            Single(BuiltInCommands.MoveWordRight, "ARROWRIGHT", KeyModifiers.Control),
            Single(BuiltInCommands.DeleteWordBackward, "BACKSPACE", KeyModifiers.Control),
            Single(BuiltInCommands.DeleteWordForward, "DELETE", KeyModifiers.Control),
            Single(BuiltInCommands.MoveToLineStart, "HOME"),
            Single(BuiltInCommands.MoveToLineEnd, "END"),
            Single(BuiltInCommands.Find, "F", KeyModifiers.Control | KeyModifiers.Shift),
            Single(BuiltInCommands.IncreaseFontSize, "+", KeyModifiers.Control),
            Single(BuiltInCommands.DecreaseFontSize, "-", KeyModifiers.Control),
            Single(BuiltInCommands.ResetFontSize, "0", KeyModifiers.Control),
            Single(BuiltInCommands.ClearScrollback, "K", KeyModifiers.Control | KeyModifiers.Shift),
            Single(BuiltInCommands.SendInterrupt, "C", KeyModifiers.Control),
            Single(BuiltInCommands.SendEndOfFile, "D", KeyModifiers.Control),
            Single(BuiltInCommands.ClearScreen, "L", KeyModifiers.Control),
        ]);

    private static CommandBinding Single(
        CommandId commandId,
        string key,
        KeyModifiers modifiers = KeyModifiers.None) => new(
            commandId,
            KeySequence.Of(new KeyStroke(key, modifiers)),
            CommandContext.Terminal);

    private static CommandBinding Direct(
        CommandId commandId,
        string key,
        KeyModifiers modifiers,
        CommandContext contexts,
        params (string Name, string Value)[] arguments) => new(
            commandId,
            KeySequence.Of(new KeyStroke(key, modifiers)),
            contexts,
            arguments.ToDictionary(argument => argument.Name, argument => argument.Value, StringComparer.Ordinal));

    private static CommandBinding Prefixed(
        CommandId commandId,
        string key,
        CommandContext contexts,
        params (string Name, string Value)[] arguments) => new(
            commandId,
            KeySequence.Of(TmuxPrefix, new KeyStroke(key)),
            contexts,
            arguments.ToDictionary(argument => argument.Name, argument => argument.Value, StringComparer.Ordinal));
}
