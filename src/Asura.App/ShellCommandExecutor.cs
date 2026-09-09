using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App;

internal enum NativeMenuCommand
{
    NewWindow,
    NewTab,
    NewTerminal,
    CloseTab,
    CommandPalette,
    Launcher,
    QuickTerminal,
    ToggleAgent,
    AddPanel,
    LayoutDesigner,
    PreviousTab,
    NextTab,
    ClosePanel,
}

internal sealed record ApplicationCommandPresentation(
    Func<Task> ShowNewItemLauncherAsync,
    Action FocusActivePanel,
    Func<Task> CloseActivePanelAsync,
    Func<Task> RenameActiveTabAsync,
    Func<Task> CloseActiveTabAsync,
    Func<int, Task> SelectWorkspaceAsync,
    Func<Task> SendLiteralPrefixAsync);

internal sealed record NativeMenuCommandActions(
    Action OpenNewWindow,
    Func<Task> OpenNewTabAsync,
    Func<Task> OpenNewTerminalAsync,
    Func<Task> CloseTabAsync,
    Action ShowCommandPalette,
    Func<Task> ShowLauncherAsync,
    Action ToggleQuickTerminal,
    Action ToggleAgent,
    Func<Task> ShowAddPanelAsync,
    Func<Task> ShowLayoutDesignerAsync,
    Func<Task> SelectPreviousTabAsync,
    Func<Task> SelectNextTabAsync,
    Func<Task> ClosePanelAsync);

/// <summary>
/// Executes the shell command vocabulary after the registry has validated and
/// parsed durable command arguments. The borrowed lifetime belongs to the
/// window; this executor owns no subscription or cancellation source.
/// </summary>
internal sealed class ShellCommandExecutor(
    MainWindowViewModel viewModel,
    ApplicationCommandPresentation presentation,
    NativeMenuCommandActions nativeMenu,
    CancellationToken lifetime)
{
    public async Task ExecuteAsync(
        CommandId commandId,
        IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var routed = ApplicationCommandRouter.Route(
            commandId,
            arguments,
            viewModel.ActiveCommandContexts);
        if (routed.Action is not { } action)
        {
            viewModel.SetError(routed.Error ?? "That command is unavailable.");
            return;
        }

        try
        {
            await ExecuteAsync(action);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            viewModel.SetError(exception.Message);
        }
    }

    public async Task ExecuteNativeAsync(NativeMenuCommand command)
    {
        switch (command)
        {
            case NativeMenuCommand.NewWindow:
                nativeMenu.OpenNewWindow();
                return;
            case NativeMenuCommand.NewTab:
                await nativeMenu.OpenNewTabAsync();
                return;
            case NativeMenuCommand.NewTerminal:
                await nativeMenu.OpenNewTerminalAsync();
                return;
            case NativeMenuCommand.CloseTab:
                await nativeMenu.CloseTabAsync();
                return;
            case NativeMenuCommand.CommandPalette:
                nativeMenu.ShowCommandPalette();
                return;
            case NativeMenuCommand.Launcher:
                await nativeMenu.ShowLauncherAsync();
                return;
            case NativeMenuCommand.QuickTerminal:
                nativeMenu.ToggleQuickTerminal();
                return;
            case NativeMenuCommand.ToggleAgent:
                nativeMenu.ToggleAgent();
                return;
            case NativeMenuCommand.AddPanel:
                await nativeMenu.ShowAddPanelAsync();
                return;
            case NativeMenuCommand.LayoutDesigner:
                await nativeMenu.ShowLayoutDesignerAsync();
                return;
            case NativeMenuCommand.PreviousTab:
                await nativeMenu.SelectPreviousTabAsync();
                return;
            case NativeMenuCommand.NextTab:
                await nativeMenu.SelectNextTabAsync();
                return;
            case NativeMenuCommand.ClosePanel:
                await nativeMenu.ClosePanelAsync();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }

    private async Task ExecuteAsync(ApplicationCommandAction action)
    {
        switch (action.Kind)
        {
            case ApplicationCommandActionKind.NewTab:
                await presentation.ShowNewItemLauncherAsync();
                return;
            case ApplicationCommandActionKind.SplitPanel:
                if (await viewModel.AddLocalTerminalPanelAsync(
                    action.SplitOrientation!.Value,
                    lifetime))
                {
                    presentation.FocusActivePanel();
                }

                return;
            case ApplicationCommandActionKind.FocusPanel:
                _ = await viewModel.FocusPanelAsync(
                    action.FocusDirection!.Value,
                    lifetime);
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.TogglePanelZoom:
                _ = viewModel.ToggleActivePanelZoom();
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.ClosePanel:
                await presentation.CloseActivePanelAsync();
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.RenameTab:
                await presentation.RenameActiveTabAsync();
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.CloseTab:
                await presentation.CloseActiveTabAsync();
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.MoveTab:
                _ = await viewModel.MoveActiveTabAsync(action.TabOffset!.Value, lifetime);
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.MoveTabToWorkspace:
                _ = await viewModel.MoveActiveTabToWorkspaceAsync(
                    action.WorkspacePosition!.Value,
                    lifetime);
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.MovePanelToWorkspace:
                _ = await viewModel.MoveActivePanelToWorkspaceAsync(
                    action.WorkspacePosition!.Value,
                    lifetime);
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.SelectRelativeTab:
                _ = await viewModel.SelectRelativeTabAsync(
                    action.TabOffset!.Value,
                    lifetime);
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.SelectLastTab:
                _ = await viewModel.SelectLastActiveTabAsync(lifetime);
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.SelectTab:
                if (!await viewModel.SelectTabAtPositionAsync(
                    action.TabPosition!.Value,
                    lifetime))
                {
                    viewModel.SetError(
                        $"Tab position {action.TabPosition.Value} is not open.");
                }

                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.SelectWorkspace:
                await presentation.SelectWorkspaceAsync(
                    action.WorkspacePosition!.Value);
                return;
            case ApplicationCommandActionKind.EnterTerminalCopyMode:
                _ = viewModel.EnterTerminalCopyMode();
                presentation.FocusActivePanel();
                return;
            case ApplicationCommandActionKind.SendPrefix:
                await presentation.SendLiteralPrefixAsync();
                presentation.FocusActivePanel();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null);
        }
    }
}
