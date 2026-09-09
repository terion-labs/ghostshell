using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

/// <summary>
/// The lock screen: an opaque veil over the whole window with one PIN box.
/// Everything it shows and decides lives in
/// <see cref="ApplicationSecurityEditorViewModel"/>; this file only routes
/// the click and the Enter key.
/// </summary>
public partial class ShellLockView : UserControl
{
    public ShellLockView()
    {
        InitializeComponent();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible)
            {
                PinInput.Focus();
            }
        };
    }

    private void OnUnlockClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        TryUnlock();
    }

    private void OnPinKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryUnlock();
        }
    }

    private void OnVeilPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        // Only presses nothing else claimed reach here — the PIN box and the
        // buttons handle their own — so a drag can never eat a click.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && VisualRoot is Avalonia.Controls.Window window)
        {
            window.BeginMoveDrag(e);
        }
    }

    private void OnBiometricUnlockClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is ApplicationSecurityEditorViewModel editor)
        {
            _ = editor.TryUnlockWithBiometricsAsync();
        }
    }

    private void TryUnlock()
    {
        if (DataContext is ApplicationSecurityEditorViewModel editor)
        {
            _ = editor.TryUnlockAsync();
        }
    }
}
