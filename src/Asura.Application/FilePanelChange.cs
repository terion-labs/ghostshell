namespace Asura.Application;

public enum FilePanelChangeKind
{
    /// <summary>The observer captured its baseline; re-read once to close the startup race.</summary>
    Synchronized,

    Changed,
}

public sealed record FilePanelChange(FilePanelLocation Location, FilePanelChangeKind Kind);
