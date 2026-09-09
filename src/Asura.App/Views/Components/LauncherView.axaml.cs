using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

public sealed partial class LauncherView : UserControl
{
    public static readonly StyledProperty<bool> ShowCloseActionProperty =
        AvaloniaProperty.Register<LauncherView, bool>(
            nameof(ShowCloseAction),
            defaultValue: true);

    public LauncherView() => InitializeComponent();

    public bool ShowCloseAction
    {
        get => GetValue(ShowCloseActionProperty);
        set => SetValue(ShowCloseActionProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? AddConnectionRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? NewBrowserRequested;
    public event EventHandler<RoutedEventArgs>? NewDatabaseRequested;
    public event EventHandler<RoutedEventArgs>? NewDockerRequested;
    public event EventHandler<RoutedEventArgs>? NewFileViewerRequested;
    public event EventHandler<RoutedEventArgs>? NewGitRequested;
    public event EventHandler<RoutedEventArgs>? NewLocalTerminalRequested;
    public event EventHandler<RoutedEventArgs>? NewProcessMonitorRequested;
    public event EventHandler<RoutedEventArgs>? NewStatisticsRequested;

    /// <summary>
    /// A saved connection, together with which adapter to open it with. The
    /// target alone is not enough: the same host opens as a terminal, a file
    /// browser or a monitor, and the row says which was asked for.
    /// </summary>
    public event EventHandler<SavedConnectionLaunchViewModel>? ConnectionLaunchRequested;

    public event EventHandler<SavedConnectionShortcutViewModel>? EditConnectionRequested;

    public event EventHandler<SavedConnectionShortcutViewModel>? DeleteConnectionRequested;

    public event EventHandler<RoutedEventArgs>? FinishOnboardingRequested;

    public event EventHandler<RoutedEventArgs>? RetryOnboardingRequested;

    public event EventHandler<KeyEventArgs>? HistorySearchKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? OpenRecentSessionRequested;
    public event EventHandler<RoutedEventArgs>? OpenScreenRequested;
    public event EventHandler<RoutedEventArgs>? OpenWorkspaceRequested;
    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;
    public event EventHandler<RoutedEventArgs>? ShowLayoutDesignerRequested;

    internal void FocusInitialAction() =>
        NewTerminalButton.Focus(NavigationMethod.Tab);

    private void OnAddConnectionClick(object? sender, RoutedEventArgs e) =>
        AddConnectionRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnNewBrowserClick(object? sender, RoutedEventArgs e) =>
        NewBrowserRequested?.Invoke(sender, e);

    private void OnNewDatabaseClick(object? sender, RoutedEventArgs e) =>
        NewDatabaseRequested?.Invoke(sender, e);

    private void OnNewDockerClick(object? sender, RoutedEventArgs e) =>
        NewDockerRequested?.Invoke(sender, e);

    private void OnNewFileViewerClick(object? sender, RoutedEventArgs e) =>
        NewFileViewerRequested?.Invoke(sender, e);

    private void OnNewGitClick(object? sender, RoutedEventArgs e) =>
        NewGitRequested?.Invoke(sender, e);

    private void OnNewLocalTerminalClick(object? sender, RoutedEventArgs e) =>
        NewLocalTerminalRequested?.Invoke(sender, e);

    private void OnNewProcessMonitorClick(object? sender, RoutedEventArgs e) =>
        NewProcessMonitorRequested?.Invoke(sender, e);

    private void OnNewStatisticsClick(object? sender, RoutedEventArgs e) =>
        NewStatisticsRequested?.Invoke(sender, e);

    private void OnConnectionLaunchRequested(
        object? sender,
        SavedConnectionLaunchViewModel launch) =>
        ConnectionLaunchRequested?.Invoke(sender, launch);

    private void OnEditConnectionRequested(
        object? sender,
        SavedConnectionShortcutViewModel shortcut) =>
        EditConnectionRequested?.Invoke(sender, shortcut);

    private void OnDeleteConnectionRequested(
        object? sender,
        SavedConnectionShortcutViewModel shortcut) =>
        DeleteConnectionRequested?.Invoke(sender, shortcut);

    private void OnFinishOnboardingClick(object? sender, RoutedEventArgs e) =>
        FinishOnboardingRequested?.Invoke(sender, e);

    private void OnRetryOnboardingClick(object? sender, RoutedEventArgs e) =>
        RetryOnboardingRequested?.Invoke(sender, e);

    private void OnHistorySearchKeyDown(object? sender, KeyEventArgs e) =>
        HistorySearchKeyDownRequested?.Invoke(sender, e);

    private void OnOpenRecentSessionClick(object? sender, RoutedEventArgs e) =>
        OpenRecentSessionRequested?.Invoke(sender, e);

    private void OnOpenScreenClick(object? sender, RoutedEventArgs e) =>
        OpenScreenRequested?.Invoke(sender, e);

    private void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e) =>
        OpenWorkspaceRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        ShowLayoutDesignerRequested?.Invoke(sender, e);
}
