namespace Asura.Core;

public enum PanelKind
{
    Terminal,
    Browser,
    FileViewer,
    Statistics,
    ProcessMonitor,

    /// <summary>
    /// A panel that has been placed but not yet told what to be. It exists so the
    /// choice of adapter happens where the panel will live rather than in a modal
    /// over the whole window; appended last so persisted history keeps its values.
    /// </summary>
    Placeholder,

    /// <summary>Appended after Placeholder so persisted history keeps its values.</summary>
    DatabaseViewer,

    /// <summary>Appended so persisted session history keeps every existing numeric value.</summary>
    Docker,

    /// <summary>Appended so persisted session history keeps every existing numeric value.</summary>
    Git,
}
