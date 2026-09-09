namespace Asura.App;

/// <summary>
/// Keeps native save/open UI outside the portable definition workflow. A null path means the user
/// dismissed the picker without choosing a file.
/// </summary>
public interface IDefinitionBundlePathPicker
{
    ValueTask<string?> PickExportPathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken);

    ValueTask<string?> PickImportPathAsync(CancellationToken cancellationToken);
}
