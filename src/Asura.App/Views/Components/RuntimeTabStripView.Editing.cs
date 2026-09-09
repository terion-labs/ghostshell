using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Asura.App.Views.Components;

public sealed partial class RuntimeTabStripView
{
    private TextBox? _activeTitleEditor;

    public event EventHandler<RuntimeTabTitleEditRequestedEventArgs>? TitleEditRequested;

    public event EventHandler<RuntimeTabIconEditRequestedEventArgs>? IconEditRequested;

    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock { Parent: Panel host } title
            || host.Children.OfType<TextBox>().SingleOrDefault() is not { } editor)
        {
            return;
        }

        if (_activeTitleEditor is { } active && !ReferenceEquals(active, editor))
        {
            FinishTitleEdit(active, commit: true);
        }

        _activeTitleEditor = editor;
        editor.Text = title.Text;
        title.IsVisible = false;
        editor.IsVisible = true;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void OnTitleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            FinishTitleEdit(editor, commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FinishTitleEdit(editor, commit: false);
            e.Handled = true;
        }
    }

    private void OnTitleEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is TextBox editor && ReferenceEquals(editor, _activeTitleEditor))
        {
            FinishTitleEdit(editor, commit: true);
        }
    }

    private void FinishTitleEdit(TextBox editor, bool commit)
    {
        if (editor.Parent is not Panel host
            || host.Children.OfType<TextBlock>().SingleOrDefault() is not { } title)
        {
            _activeTitleEditor = null;
            return;
        }

        var editedTitle = editor.Text?.Trim() ?? string.Empty;
        var previousTitle = title.Text ?? string.Empty;
        _activeTitleEditor = null;
        editor.IsVisible = false;
        title.IsVisible = true;
        editor.Text = previousTitle;
        if (commit
            && editedTitle.Length > 0
            && !string.Equals(editedTitle, previousTitle, StringComparison.Ordinal)
            && editor.DataContext is { } tab)
        {
            TitleEditRequested?.Invoke(
                this,
                new RuntimeTabTitleEditRequestedEventArgs(tab, editedTitle));
        }
    }

    private void OnIconDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: { } tab } anchor
            || FlyoutBase.GetAttachedFlyout(anchor) is not Flyout
            {
                Content: IconPicker picker,
            } flyout)
        {
            return;
        }

        picker.DataContext = new RuntimeTabIconPickerViewModel(anchor.Tag as string ?? string.Empty);
        picker.Tag = new IconPickerSession(tab, flyout);
        flyout.Placement = IconPickerPlacement;
        flyout.ShowAt(anchor);
        e.Handled = true;
    }

    private void OnIconFlyoutOpened(object? sender, EventArgs e)
    {
        _ = e;
        if (sender is Flyout { Content: IconPicker picker })
        {
            Dispatcher.UIThread.Post(picker.FocusSearch);
        }
    }

    private void OnIconChosen(object? sender, string icon)
    {
        if (sender is not IconPicker
            {
                Tag: IconPickerSession session,
            })
        {
            return;
        }

        session.Flyout.Hide();
        IconEditRequested?.Invoke(
            this,
            new RuntimeTabIconEditRequestedEventArgs(session.Tab, icon));
    }

    private sealed record IconPickerSession(object Tab, Flyout Flyout);
}

public sealed class RuntimeTabTitleEditRequestedEventArgs(object tab, string title) : EventArgs
{
    public object Tab { get; } = tab ?? throw new ArgumentNullException(nameof(tab));

    public string Title { get; } = string.IsNullOrWhiteSpace(title)
        ? throw new ArgumentException("A tab title is required.", nameof(title))
        : title.Trim();
}

public sealed class RuntimeTabIconEditRequestedEventArgs(object tab, string icon) : EventArgs
{
    public object Tab { get; } = tab ?? throw new ArgumentNullException(nameof(tab));

    public string Icon { get; } = icon ?? throw new ArgumentNullException(nameof(icon));
}
