namespace GhostShell.App;

public sealed record DefinitionBundleExportReceipt(
    string Path,
    int DefinitionCount,
    DateTimeOffset ExportedAt,
    int ReconnectRequiredDatabasePanelCount = 0)
{
    public string? Warning => ReconnectRequiredDatabasePanelCount == 0 ? null
        : $"{ReconnectRequiredDatabasePanelCount} legacy database panel(s) require reconnection in this export because their saved targets contain credentials. Local saved connections are unchanged.";
}
