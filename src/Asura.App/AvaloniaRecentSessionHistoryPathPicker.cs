using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Asura.App;

public sealed class AvaloniaRecentSessionHistoryPathPicker(Window owner)
    : IRecentSessionHistoryPathPicker
{
    private static readonly FilePickerFileType HistoryFileType = new(
        "Asura session history")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"],
    };

    private readonly Window _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async ValueTask<string?> PickExportPathAsync(
        string suggestedFileName,
        int recordCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        if (recordCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recordCount));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var selected = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {recordCount:N0} metadata-only history records",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [HistoryFileType],
            ShowOverwritePrompt = true,
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (selected is null)
        {
            return null;
        }

        return selected.TryGetLocalPath()
            ?? throw new NotSupportedException(
                "Session history must be exported to a local filesystem path.");
    }
}
