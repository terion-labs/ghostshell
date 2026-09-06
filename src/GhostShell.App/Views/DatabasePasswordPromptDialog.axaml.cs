using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views;

/// <summary>
/// Asks for the password of a saved connection stored without one. Closes
/// with the typed value and the user's explicit persistence choice, or null
/// on cancel.
/// </summary>
public sealed partial class DatabasePasswordPromptDialog : Window
{
    public DatabasePasswordPromptDialog()
    {
        InitializeComponent();
    }

    public DatabasePasswordPromptDialog(
        string connectionName,
        bool canSavePassword = false,
        string passwordStoreLabel = "Save in system credential store",
        string? description = null)
        : this()
    {
        Title = $"{connectionName} password";
        PromptTitle.Text = $"{connectionName} password";
        AutomationProperties.SetName(PasswordInput, $"{connectionName} password");
        if (canSavePassword)
        {
            PromptDescription.Text =
                "Use this password for the current session, or save it securely for future connections.";
            SavePasswordCheckBox.Content = passwordStoreLabel;
            SavePasswordCheckBox.IsVisible = true;
        }

        if (description is not null)
        {
            PromptDescription.Text = description;
        }

        Opened += (_, _) => PasswordInput.Focus();
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void OnPasswordTextChanged(object? sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        SavePasswordCheckBox.IsEnabled = !string.IsNullOrEmpty(PasswordInput.Text);
        if (!SavePasswordCheckBox.IsEnabled)
        {
            SavePasswordCheckBox.IsChecked = false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Submit();
    }

    private void Submit() => Close(new DatabasePasswordPromptResult(
        PasswordInput.Text ?? string.Empty,
        SavePasswordCheckBox.IsVisible && SavePasswordCheckBox.IsChecked == true));
}
