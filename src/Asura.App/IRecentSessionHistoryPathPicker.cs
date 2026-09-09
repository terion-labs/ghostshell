namespace Asura.App;

public interface IRecentSessionHistoryPathPicker
{
    ValueTask<string?> PickExportPathAsync(
        string suggestedFileName,
        int recordCount,
        CancellationToken cancellationToken);
}
