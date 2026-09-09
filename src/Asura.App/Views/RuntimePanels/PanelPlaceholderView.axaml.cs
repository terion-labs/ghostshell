using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Asura.App.Views.RuntimePanels;

/// <summary>
/// The adapter choice for a panel that has already been placed. It owns no policy:
/// each button forwards to the shell, which creates the panel and puts it in this
/// placeholder's cell.
/// </summary>
public sealed partial class PanelPlaceholderView : UserControl
{
    public PanelPlaceholderView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public event EventHandler<RoutedEventArgs>? TerminalRequested;

    public event EventHandler<RoutedEventArgs>? BrowserRequested;

    public event EventHandler<RoutedEventArgs>? StatisticsRequested;

    public event EventHandler<RoutedEventArgs>? DatabaseRequested;

    public event EventHandler<RoutedEventArgs>? DockerRequested;

    public event EventHandler<RoutedEventArgs>? GitRequested;

    public event EventHandler<RoutedEventArgs>? FileViewerRequested;

    public event EventHandler<RoutedEventArgs>? ProcessMonitorRequested;

    public event EventHandler<RoutedEventArgs>? AddConnectionRequested;

    public event EventHandler<SavedConnectionLaunchViewModel>? ConnectionLaunchRequested;

    public event EventHandler<SavedConnectionShortcutViewModel>? EditConnectionRequested;

    public event EventHandler<SavedConnectionShortcutViewModel>? DeleteConnectionRequested;

    public event EventHandler<RoutedEventArgs>? FinishOnboardingRequested;

    public event EventHandler<RoutedEventArgs>? RetryOnboardingRequested;

    public event EventHandler<Avalonia.Input.KeyEventArgs>? HistorySearchKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? OpenScreenRequested;

    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;

    public event EventHandler<RoutedEventArgs>? ShowLayoutDesignerRequested;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);

    private void OnChooseTerminalClick(object? sender, RoutedEventArgs e) =>
        TerminalRequested?.Invoke(sender, e);

    private void OnChooseBrowserClick(object? sender, RoutedEventArgs e) =>
        BrowserRequested?.Invoke(sender, e);

    private void OnChooseStatisticsClick(object? sender, RoutedEventArgs e) =>
        StatisticsRequested?.Invoke(sender, e);

    private void OnChooseDatabaseClick(object? sender, RoutedEventArgs e) =>
        DatabaseRequested?.Invoke(sender, e);

    private void OnChooseDockerClick(object? sender, RoutedEventArgs e) =>
        DockerRequested?.Invoke(sender, e);

    private void OnChooseGitClick(object? sender, RoutedEventArgs e) =>
        GitRequested?.Invoke(sender, e);

    private void OnChooseFileViewerClick(object? sender, RoutedEventArgs e) =>
        FileViewerRequested?.Invoke(sender, e);

    private void OnChooseProcessMonitorClick(object? sender, RoutedEventArgs e) =>
        ProcessMonitorRequested?.Invoke(sender, e);

    private void OnAddConnectionClick(object? sender, RoutedEventArgs e) =>
        AddConnectionRequested?.Invoke(sender, e);

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

    private void OnHistorySearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e) =>
        HistorySearchKeyDownRequested?.Invoke(sender, e);

    private void OnOpenScreenClick(object? sender, RoutedEventArgs e) =>
        OpenScreenRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        ShowLayoutDesignerRequested?.Invoke(sender, e);
}
