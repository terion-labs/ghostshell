using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Asura.Core;

public sealed class TabInstance
{
    public TabInstance(
        TabInstanceId id,
        string title,
        IEnumerable<PanelInstance> panels,
        PanelInstanceId activePanelId)
        : this(
            id,
            title,
            [.. (panels ?? throw new ArgumentNullException(nameof(panels)))],
            activePanelId)
    {
    }

    [JsonConstructor]
    public TabInstance(
        TabInstanceId id,
        string title,
        IReadOnlyList<PanelInstance> panels,
        PanelInstanceId activePanelId)
    {
        RuntimeInstanceValidation.RequireId(id.Value, nameof(id));
        ArgumentNullException.ThrowIfNull(panels);

        var panelCopies = panels
            .Select(panel => new PanelInstance(
                panel ?? throw new ArgumentException(
                    "A tab cannot contain a null panel.",
                    nameof(panels))))
            .ToArray();
        if (panelCopies.Length == 0)
        {
            throw new ArgumentException("A tab must contain at least one panel.", nameof(panels));
        }

        RuntimeInstanceValidation.RequireUniqueIds(
            panelCopies.Select(panel => panel.Id.Value),
            "A tab cannot contain duplicate panel IDs.",
            nameof(panels));
        if (panelCopies.All(panel => panel.Id != activePanelId))
        {
            throw new ArgumentException(
                "The active panel must belong to the tab.",
                nameof(activePanelId));
        }

        Id = id;
        Title = RuntimeInstanceValidation.RequireTitle(title, nameof(title));
        Panels = new ReadOnlyCollection<PanelInstance>(panelCopies);
        ActivePanelId = activePanelId;
    }

    public TabInstance(TabInstance source)
        : this(
            (source ?? throw new ArgumentNullException(nameof(source))).Id,
            source.Title,
            source.Panels,
            source.ActivePanelId)
    {
    }

    public TabInstanceId Id { get; }

    public string Title { get; }

    public IReadOnlyList<PanelInstance> Panels { get; }

    public PanelInstanceId ActivePanelId { get; }

    public TabInstance ActivatePanel(PanelInstanceId panelId)
    {
        if (Panels.All(panel => panel.Id != panelId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(panelId),
                panelId,
                "The panel does not belong to this tab.");
        }

        return panelId == ActivePanelId
            ? this
            : new TabInstance(Id, Title, Panels, panelId);
    }

    public TabInstance ReplacePanelSession(
        PanelInstanceId panelId,
        SessionId? sessionId)
    {
        var panelIndex = -1;
        for (var index = 0; index < Panels.Count; index++)
        {
            if (Panels[index].Id == panelId)
            {
                panelIndex = index;
                break;
            }
        }

        if (panelIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(panelId),
                panelId,
                "The panel does not belong to this tab.");
        }

        var currentPanel = Panels[panelIndex];
        var replacement = currentPanel.WithSession(sessionId);
        if (ReferenceEquals(currentPanel, replacement))
        {
            return this;
        }

        var panels = Panels.ToArray();
        panels[panelIndex] = replacement;
        return new TabInstance(Id, Title, panels, ActivePanelId);
    }
}
