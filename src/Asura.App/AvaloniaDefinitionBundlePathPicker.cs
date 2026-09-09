using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Asura.App;

/// <summary>
/// Adapts Avalonia's native file dialogs to the portable definition bundle workflow.
/// Only local filesystem paths are accepted because bundle reads and atomic writes happen locally.
/// </summary>
public sealed class AvaloniaDefinitionBundlePathPicker(Window owner)
    : IDefinitionBundlePathPicker
{
    private static readonly FilePickerFileType BundleFileType = new("Asura definition bundle")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"],
    };

    private readonly Window _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async ValueTask<string?> PickExportPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Asura definitions",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [BundleFileType],
            ShowOverwritePrompt = true,
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (selected is null)
        {
            return null;
        }

        return selected.TryGetLocalPath()
            ?? throw new NotSupportedException(
                "Definition bundles must be exported to a local filesystem path.");
    }

    public async ValueTask<string?> PickImportPathAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Asura definitions",
            AllowMultiple = false,
            FileTypeFilter = [BundleFileType],
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (selected.Count == 0)
        {
            return null;
        }

        return selected[0].TryGetLocalPath()
            ?? throw new NotSupportedException(
                "Definition bundles must be imported from a local filesystem path.");
    }
}
