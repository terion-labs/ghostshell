using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views;

public sealed partial class DiagnosticsExportView : UserControl
{
    public DiagnosticsExportView()
    {
        InitializeComponent();
    }

    public DiagnosticsExportView(DiagnosticsExportViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public DiagnosticsExportViewModel? ViewModel => DataContext as DiagnosticsExportViewModel;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ViewModel?.TryCancelExport();
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.ExportAsync();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel?.TryCancelExport();
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.OpenArtifactAsync();
        }
    }

    private async void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.RevealArtifactAsync();
        }
    }
}
