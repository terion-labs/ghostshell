using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.Application;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views;

public sealed partial class FileTransferDialog : Window
{
    public FileTransferDialog()
    {
        InitializeComponent();
    }

    public FileTransferDialog(FileTransferEditorViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private FileTransferEditorViewModel ViewModel => DataContext as FileTransferEditorViewModel
        ?? throw new InvalidOperationException("The file transfer editor is unavailable.");

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnQueueClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            Close(ViewModel.CreateRequest());
        }
        catch (ArgumentException exception)
        {
            this.FindControl<TextBlock>("ValidationMessage")!.Text = exception.Message;
            this.FindControl<Callout>("ValidationCard")!.IsVisible = true;
        }
    }
}
