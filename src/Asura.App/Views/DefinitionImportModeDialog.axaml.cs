using Asura.Application;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views;

public sealed partial class DefinitionImportModeDialog : Window
{
    public DefinitionImportModeDialog() => InitializeComponent();

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close((DefinitionImportMode?)null);
    }

    private void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var mode = this.FindControl<RadioButton>("ReplaceExistingOption")?.IsChecked == true
            ? DefinitionImportMode.ReplaceExisting
            : DefinitionImportMode.FailOnConflict;
        Close((DefinitionImportMode?)mode);
    }
}
