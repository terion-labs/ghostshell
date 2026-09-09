namespace Asura.App.Views;

public sealed partial class MainWindow
{
    private ShellCommandExecutor? _shellCommands;

    private static App AsuraApplication => Avalonia.Application.Current as App
        ?? throw new InvalidOperationException("The Asura application is unavailable.");

    private ShellCommandExecutor ShellCommands => _shellCommands ??= new(
        ViewModel,
        new ApplicationCommandPresentation(
            ShowNewItemLauncherAsync,
            FocusNavigator.FocusActivePanel,
            CloseActivePanelAsync,
            RenameActiveTabAsync,
            CloseActiveTabAsync,
            position => OpenRuntimeWorkspaceAsync(token =>
                ViewModel.SelectWorkspaceAtPositionAsync(position, token)),
            SendLiteralPrefixAsync),
        new NativeMenuCommandActions(
            AsuraApplication.OpenNewWindow,
            () => AsuraApplication.OpenNewTabAsync(this),
            RequestNewTerminalAsync,
            () => AsuraApplication.CloseTabAsync(this),
            ShowCommandPalette,
            NavigateToLauncherAsync,
            AsuraApplication.ToggleQuickTerminal,
            ToggleAgentPanel,
            ShowNewPanelChooserAsync,
            ShowLayoutDesignerAsync,
            () => SelectRelativeTabAsync(-1),
            () => SelectRelativeTabAsync(1),
            RequestClosePanelAsync),
        _lifetime.Token);

    private async void OnNewWindowMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.NewWindow);
    }

    private async void OnNewTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.NewTab);
    }

    private async void OnNewTerminalMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.NewTerminal);
    }

    private async void OnCloseTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.CloseTab);
    }

    private async void OnCommandPaletteMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.CommandPalette);
    }

    private async void OnLauncherMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.Launcher);
    }

    private async void OnQuickTerminalMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.QuickTerminal);
    }

    private async void OnToggleAgentMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.ToggleAgent);
    }

    private async void OnAddPanelMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.AddPanel);
    }

    private async void OnLayoutDesignerMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.LayoutDesigner);
    }

    private async void OnPreviousTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.PreviousTab);
    }

    private async void OnNextTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.NextTab);
    }

    private async void OnClosePanelMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await ShellCommands.ExecuteNativeAsync(NativeMenuCommand.ClosePanel);
    }
}
