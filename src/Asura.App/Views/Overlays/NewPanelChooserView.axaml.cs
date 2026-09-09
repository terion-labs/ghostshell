using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views.Overlays;

public sealed partial class NewPanelChooserView : UserControl
{
    public NewPanelChooserView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? AddBrowserPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddFilePanelRequested;

    public event EventHandler<RoutedEventArgs>? AddProcessMonitorPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddStatisticsPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddDatabasePanelRequested;

    public event EventHandler<RoutedEventArgs>? AddDockerPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddGitPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddTerminalPanelRequested;

    /// <summary>Raised with the chosen connection row as the sender.</summary>
    public event EventHandler<RoutedEventArgs>? AddConnectionPanelRequested;


    /// <summary>Raised with the chosen saved-screen row as the sender.</summary>
    public event EventHandler<RoutedEventArgs>? OpenScreenRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? ShowLayoutDesignerRequested;

    internal void FocusInitialAction() =>
        NewPanelTerminalButton.Focus(NavigationMethod.Tab);

    private void OnAddBrowserPanelClick(object? sender, RoutedEventArgs e) =>
        AddBrowserPanelRequested?.Invoke(sender, e);

    private void OnAddFilePanelClick(object? sender, RoutedEventArgs e) =>
        AddFilePanelRequested?.Invoke(sender, e);

    private void OnAddProcessMonitorPanelClick(object? sender, RoutedEventArgs e) =>
        AddProcessMonitorPanelRequested?.Invoke(sender, e);

    private void OnAddStatisticsPanelClick(object? sender, RoutedEventArgs e) =>
        AddStatisticsPanelRequested?.Invoke(sender, e);

    private void OnAddDatabasePanelClick(object? sender, RoutedEventArgs e) =>
        AddDatabasePanelRequested?.Invoke(sender, e);

    private void OnAddDockerPanelClick(object? sender, RoutedEventArgs e) =>
        AddDockerPanelRequested?.Invoke(sender, e);

    private void OnAddGitPanelClick(object? sender, RoutedEventArgs e) =>
        AddGitPanelRequested?.Invoke(sender, e);

    private void OnAddTerminalPanelClick(object? sender, RoutedEventArgs e) =>
        AddTerminalPanelRequested?.Invoke(sender, e);

    private void OnAddConnectionPanelClick(object? sender, RoutedEventArgs e) =>
        AddConnectionPanelRequested?.Invoke(sender, e);


    private void OnOpenScreenClick(object? sender, RoutedEventArgs e) =>
        OpenScreenRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        ShowLayoutDesignerRequested?.Invoke(sender, e);
}
