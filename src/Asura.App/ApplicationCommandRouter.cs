using System.Globalization;
using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App;

internal enum ApplicationCommandActionKind
{
    NewTab,
    SplitPanel,
    FocusPanel,
    TogglePanelZoom,
    ClosePanel,
    RenameTab,
    CloseTab,
    MoveTab,
    MoveTabToWorkspace,
    MovePanelToWorkspace,
    SelectRelativeTab,
    SelectLastTab,
    SelectTab,
    SelectWorkspace,
    EnterTerminalCopyMode,
    SendPrefix,
}

internal sealed record ApplicationCommandAction(
    ApplicationCommandActionKind Kind,
    PanelSplitOrientation? SplitOrientation = null,
    PanelFocusDirection? FocusDirection = null,
    int? TabOffset = null,
    int? TabPosition = null,
    int? WorkspacePosition = null);

internal sealed record ApplicationCommandRouteResult(
    ApplicationCommandAction? Action,
    string? Error)
{
    public bool IsSuccess => Action is not null;
}

/// <summary>
/// Validates commands against the shared registry and turns durable string
/// arguments into the typed actions understood by the desktop shell.
/// </summary>
internal static class ApplicationCommandRouter
{
    public static ApplicationCommandRouteResult Route(
        CommandId commandId,
        IReadOnlyDictionary<string, string> arguments,
        CommandContext activeContexts)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!BuiltInCommands.Registry.TryGet(commandId, out var definition)
            || definition is null)
        {
            return Failure($"Command '{commandId}' is not registered.");
        }

        var invocation = new CommandInvocation(activeContexts, arguments);
        if (!definition.IsAvailable(invocation))
        {
            return Failure($"Command '{definition.Title}' is unavailable in the current context or has invalid arguments.");
        }

        if (commandId == BuiltInCommands.NewTab)
        {
            return Success(ApplicationCommandActionKind.NewTab);
        }

        if (commandId == BuiltInCommands.SplitPanel)
        {
            return ParseSplitOrientation(arguments);
        }

        if (commandId == BuiltInCommands.FocusPanel)
        {
            return ParseFocusDirection(arguments);
        }

        if (commandId == BuiltInCommands.TogglePanelZoom)
        {
            return Success(ApplicationCommandActionKind.TogglePanelZoom);
        }

        if (commandId == BuiltInCommands.ClosePanel)
        {
            return Success(ApplicationCommandActionKind.ClosePanel);
        }

        if (commandId == BuiltInCommands.RenameTab)
        {
            return Success(ApplicationCommandActionKind.RenameTab);
        }

        if (commandId == BuiltInCommands.CloseTab)
        {
            return Success(ApplicationCommandActionKind.CloseTab);
        }

        if (commandId == BuiltInCommands.MoveTabLeft)
        {
            return Success(ApplicationCommandActionKind.MoveTab, tabOffset: -1);
        }

        if (commandId == BuiltInCommands.MoveTabRight)
        {
            return Success(ApplicationCommandActionKind.MoveTab, tabOffset: 1);
        }

        if (commandId == BuiltInCommands.MoveTabToWorkspace)
        {
            return ParseWorkspaceTransferPosition(
                arguments,
                ApplicationCommandActionKind.MoveTabToWorkspace);
        }

        if (commandId == BuiltInCommands.MovePanelToWorkspace)
        {
            return ParseWorkspaceTransferPosition(
                arguments,
                ApplicationCommandActionKind.MovePanelToWorkspace);
        }

        if (commandId == BuiltInCommands.NextTab)
        {
            return Success(ApplicationCommandActionKind.SelectRelativeTab, tabOffset: 1);
        }

        if (commandId == BuiltInCommands.PreviousTab)
        {
            return Success(ApplicationCommandActionKind.SelectRelativeTab, tabOffset: -1);
        }

        if (commandId == BuiltInCommands.LastTab)
        {
            return Success(ApplicationCommandActionKind.SelectLastTab);
        }

        if (commandId == BuiltInCommands.SelectTab)
        {
            return ParseTabPosition(arguments);
        }

        if (commandId == BuiltInCommands.SelectWorkspace)
        {
            return ParseWorkspacePosition(arguments);
        }

        if (commandId == BuiltInCommands.EnterTerminalCopyMode)
        {
            return Success(ApplicationCommandActionKind.EnterTerminalCopyMode);
        }

        if (commandId == BuiltInCommands.SendPrefix)
        {
            return Success(ApplicationCommandActionKind.SendPrefix);
        }

        return Failure($"Command '{definition.Title}' is not an application command.");
    }

    private static ApplicationCommandRouteResult ParseSplitOrientation(
        IReadOnlyDictionary<string, string> arguments)
    {
        var orientation = arguments["orientation"] switch
        {
            "left-right" => PanelSplitOrientation.LeftRight,
            "top-bottom" => PanelSplitOrientation.TopBottom,
            _ => (PanelSplitOrientation?)null,
        };
        return orientation is { } value
            ? new ApplicationCommandRouteResult(
                new ApplicationCommandAction(
                    ApplicationCommandActionKind.SplitPanel,
                    SplitOrientation: value),
                null)
            : Failure("The split orientation is not supported.");
    }

    private static ApplicationCommandRouteResult ParseFocusDirection(
        IReadOnlyDictionary<string, string> arguments)
    {
        var direction = arguments["direction"] switch
        {
            "left" => PanelFocusDirection.Left,
            "right" => PanelFocusDirection.Right,
            "up" => PanelFocusDirection.Up,
            "down" => PanelFocusDirection.Down,
            "next" => PanelFocusDirection.Next,
            _ => (PanelFocusDirection?)null,
        };
        return direction is { } value
            ? new ApplicationCommandRouteResult(
                new ApplicationCommandAction(
                    ApplicationCommandActionKind.FocusPanel,
                    FocusDirection: value),
                null)
            : Failure("The panel focus direction is not supported.");
    }

    private static ApplicationCommandRouteResult ParseTabPosition(
        IReadOnlyDictionary<string, string> arguments)
    {
        if (!int.TryParse(
                arguments["position"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var position)
            || position is < 0 or > 9)
        {
            return Failure("The tab position must be between 0 and 9.");
        }

        return new ApplicationCommandRouteResult(
            new ApplicationCommandAction(
                ApplicationCommandActionKind.SelectTab,
                TabPosition: position),
            null);
    }

    private static ApplicationCommandRouteResult ParseWorkspacePosition(
        IReadOnlyDictionary<string, string> arguments)
    {
        if (!int.TryParse(
                arguments["position"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var position)
            || position is < 0 or > 8)
        {
            return Failure("The workspace position must be between 0 and 8.");
        }

        return new ApplicationCommandRouteResult(
            new ApplicationCommandAction(
                ApplicationCommandActionKind.SelectWorkspace,
                WorkspacePosition: position),
            null);
    }

    private static ApplicationCommandRouteResult ParseWorkspaceTransferPosition(
        IReadOnlyDictionary<string, string> arguments,
        ApplicationCommandActionKind kind)
    {
        if (!int.TryParse(
                arguments["position"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var position)
            || position is < 0 or > 8)
        {
            return Failure("The open workspace position must be between 0 and 8.");
        }

        return new ApplicationCommandRouteResult(
            new ApplicationCommandAction(kind, WorkspacePosition: position),
            null);
    }

    private static ApplicationCommandRouteResult Success(
        ApplicationCommandActionKind kind,
        int? tabOffset = null) => new(
        new ApplicationCommandAction(kind, TabOffset: tabOffset),
        null);

    private static ApplicationCommandRouteResult Failure(string error) => new(null, error);
}
