using Asura.App.ViewModels;
using Asura.Application;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Asura.App.Views.Components;

public sealed partial class DatabaseWorkspaceView
{
    private static readonly FilePickerFileType MermaidMarkdownFileType = new("Mermaid Markdown")
    {
        Patterns = ["*.md"],
        MimeTypes = ["text/markdown"],
        AppleUniformTypeIdentifiers = ["net.daringfireball.markdown"],
    };
    private static readonly FilePickerFileType MermaidSvgFileType = new("SVG diagram")
    {
        Patterns = ["*.svg"],
        MimeTypes = ["image/svg+xml"],
        AppleUniformTypeIdentifiers = ["public.svg-image"],
    };

    private async void OnCopyMermaidDiagramClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await CopyDiagramAsync(DatabaseDiagramExport.MermaidMarkdown);
    }

    private async void OnCopyMermaidSvgClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await CopyDiagramAsync(DatabaseDiagramExport.Svg);
    }

    internal async Task CopyDiagramAsync(DatabaseDiagramExport format)
    {
        if (Panel is not { } panel
            || panel.DiagramSession is not { } session
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            var snapshot = await panel.BuildDiagramClipboardAsync(session, format);
            if (ReferenceEquals(Panel, panel) && ReferenceEquals(panel.DiagramSession, session)
                && panel.IsClipboardRequestCurrent(snapshot.Revision)
                && ReferenceEquals(TopLevel.GetTopLevel(this)?.Clipboard, clipboard))
            {
                await clipboard.SetTextAsync(snapshot.Text);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer copy, hidden/replaced diagram, or closed panel owns the UI.
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(Panel, panel) && ReferenceEquals(panel.DiagramSession, session))
            {
                panel.ReportInteractionError($"Could not copy the database diagram: {exception.Message}");
            }
        }
    }

    private async void OnSaveMermaidDiagramClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var panel = Panel;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (panel is not { IsConnected: true } || storage?.CanSave != true)
        {
            return;
        }

        var session = panel.DiagramSession;
        try
        {
            var selected = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the Mermaid ER diagram",
                SuggestedFileName = "dbdiagram.md",
                DefaultExtension = "md",
                FileTypeChoices = [MermaidMarkdownFileType, MermaidSvgFileType],
                ShowOverwritePrompt = true,
            });
            if (selected is null)
            {
                return;
            }

            if (!ReferenceEquals(session, panel.DiagramSession))
            {
                panel.ReportInteractionError(
                    "The database diagram changed while the destination was open. Save it again.");
                return;
            }

            var format = selected.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? DatabaseDiagramExport.Svg
                : DatabaseDiagramExport.MermaidMarkdown;
            await WriteStorageFileAsync(
                selected,
                destination => panel.ExportDatabaseDiagramAsync(destination, format, CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
            // Native save pickers do not agree on whether cancellation returns
            // null or throws. Both mean the user intentionally did nothing.
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not save the Mermaid diagram: {exception.Message}");
        }
    }

}
