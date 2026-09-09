using System.ComponentModel;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.RuntimePanels;

public sealed partial class RedisRuntimePanelView : UserControl
{
    private RedisRuntimePanelViewModel? _observedPanel;

    public RedisRuntimePanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObservePanel();
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;
    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;
    public event EventHandler<RoutedEventArgs>? EditConnectionRequested;
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    private RedisRuntimePanelViewModel? Panel => DataContext as RedisRuntimePanelViewModel;

    private void ObservePanel()
    {
        if (_observedPanel is not null)
        {
            _observedPanel.PasswordRequested -= OnPasswordRequested;
            _observedPanel.PropertyChanged -= OnPanelPropertyChanged;
        }

        _observedPanel = Panel;
        if (_observedPanel is not null)
        {
            _observedPanel.PasswordRequested += OnPasswordRequested;
            _observedPanel.PropertyChanged += OnPanelPropertyChanged;
        }

        SyncPerspectiveButtons();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is null or nameof(RedisRuntimePanelViewModel.Perspective))
        {
            SyncPerspectiveButtons();
        }
    }

    private async void OnPasswordRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is not { } panel || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var result = await new DatabasePasswordPromptDialog(
                panel.SavedConnectionName ?? "Redis",
                panel.CanStorePassword,
                panel.PasswordStoreLabel)
            .ShowDialog<DatabasePasswordPromptResult?>(owner);
        if (result is not null)
        {
            panel.SetSessionPassword(result.Password);
            if (result.SaveToCredentialStore)
            {
                _ = await panel.StoreSessionPasswordAsync(result.Password);
            }

            await panel.ConnectAsync();
        }
    }

    private void OnBrowserClick(object? sender, RoutedEventArgs e) => SetPerspective(RedisWorkspacePerspective.Browser);
    private void OnSearchClick(object? sender, RoutedEventArgs e) => SetPerspective(RedisWorkspacePerspective.Search);
    private void OnPubSubClick(object? sender, RoutedEventArgs e) => SetPerspective(RedisWorkspacePerspective.PubSub);

    private void SetPerspective(RedisWorkspacePerspective perspective)
    {
        if (Panel is { } panel)
        {
            panel.Perspective = perspective;
        }

        SyncPerspectiveButtons();
    }

    private void SyncPerspectiveButtons()
    {
        BrowserButton.IsChecked = Panel?.Perspective == RedisWorkspacePerspective.Browser;
        SearchButton.IsChecked = Panel?.Perspective == RedisWorkspacePerspective.Search;
        PubSubButton.IsChecked = Panel?.Perspective == RedisWorkspacePerspective.PubSub;
    }

    private void OnNewKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Panel?.BeginCreateKey();
    }

    // The sheet closes itself once the key exists; this is the way out without
    // creating one.
    private void OnCancelCreateKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        Panel?.CancelCreateKey();
    }

    // Rows of a collection form. The row a button belongs to is the row it
    // carries as its data.
    private void OnAddNewKeyEntryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Panel?.AddNewKeyEntry();
    }

    private void OnRemoveNewKeyEntryClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Panel is { } panel && sender is Control { DataContext: RedisEntryDraft entry })
        {
            panel.RemoveNewKeyEntry(entry);
        }
    }

    private void OnAddMutationEntryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Panel?.AddMutationEntry();
    }

    private void OnRemoveMutationEntryClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Panel is { } panel && sender is Control { DataContext: RedisEntryDraft entry })
        {
            panel.RemoveMutationEntry(entry);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(sender, e);
    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e) => ConnectionSelected?.Invoke(this, e);
    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e) => NewConnectionRequested?.Invoke(sender, e);
    private void OnEditConnectionClick(object? sender, RoutedEventArgs e) => EditConnectionRequested?.Invoke(this, e);
    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) => SplitRequested?.Invoke(sender, orientation);
}
