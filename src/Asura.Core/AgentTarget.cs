using System.Collections.ObjectModel;
using System.Text;

namespace Asura.Core;

/// <summary>
/// Identifies the exact live application scope an agent may inspect. Targets contain identifiers
/// only; resolving them into live panels remains a session-host responsibility.
/// </summary>
public abstract record AgentTarget
{
    private const int MaximumIdentifierBytes = 256;

    private AgentTarget()
    {
    }

    public sealed record Panel : AgentTarget
    {
        public Panel(
            WindowInstanceId windowId,
            WorkspaceInstanceId workspaceId,
            TabInstanceId tabId,
            PanelInstanceId panelId)
        {
            RequireId(windowId.Value, nameof(windowId));
            RequireId(workspaceId.Value, nameof(workspaceId));
            RequireId(tabId.Value, nameof(tabId));
            RequireId(panelId.Value, nameof(panelId));
            WindowId = windowId;
            WorkspaceId = workspaceId;
            TabId = tabId;
            PanelId = panelId;
        }

        public WindowInstanceId WindowId { get; }

        public WorkspaceInstanceId WorkspaceId { get; }

        public TabInstanceId TabId { get; }

        public PanelInstanceId PanelId { get; }
    }

    public sealed record ConnectionSession : AgentTarget
    {
        public ConnectionSession(SessionId sessionId)
        {
            RequireId(sessionId.Value, nameof(sessionId));
            SessionId = sessionId;
        }

        public SessionId SessionId { get; }
    }

    public sealed record OpenTab : AgentTarget
    {
        public OpenTab(
            WindowInstanceId windowId,
            WorkspaceInstanceId workspaceId,
            TabInstanceId tabId)
        {
            RequireId(windowId.Value, nameof(windowId));
            RequireId(workspaceId.Value, nameof(workspaceId));
            RequireId(tabId.Value, nameof(tabId));
            WindowId = windowId;
            WorkspaceId = workspaceId;
            TabId = tabId;
        }

        public WindowInstanceId WindowId { get; }

        public WorkspaceInstanceId WorkspaceId { get; }

        public TabInstanceId TabId { get; }
    }

    public sealed record Workspace : AgentTarget
    {
        public Workspace(
            WindowInstanceId windowId,
            WorkspaceInstanceId workspaceId)
        {
            RequireId(windowId.Value, nameof(windowId));
            RequireId(workspaceId.Value, nameof(workspaceId));
            WindowId = windowId;
            WorkspaceId = workspaceId;
        }

        public WindowInstanceId WindowId { get; }

        public WorkspaceInstanceId WorkspaceId { get; }
    }

    public sealed record SelectedPanels : AgentTarget
    {
        public const int MaximumPanelCount = WorkspaceInstance.MaximumPanelCount;

        public SelectedPanels(IEnumerable<Panel> panels)
        {
            ArgumentNullException.ThrowIfNull(panels);
            var copies = panels
                .Select(panel => panel ?? throw new ArgumentException(
                    "A selected-panel target cannot contain null values.",
                    nameof(panels)))
                .OrderBy(panel => panel.TabId.Value, StringComparer.Ordinal)
                .ThenBy(panel => panel.PanelId.Value, StringComparer.Ordinal)
                .ToArray();
            if (copies.Length is 0 or > MaximumPanelCount)
            {
                throw new ArgumentException(
                    $"A selected-panel target must contain between 1 and {MaximumPanelCount} panels.",
                    nameof(panels));
            }

            var first = copies[0];
            if (copies.Any(panel =>
                    panel.WindowId != first.WindowId
                    || panel.WorkspaceId != first.WorkspaceId))
            {
                throw new ArgumentException(
                    "A selected-panel target must stay within one window and workspace.",
                    nameof(panels));
            }

            if (copies
                .Select(panel => panel.PanelId)
                .Distinct()
                .Count() != copies.Length)
            {
                throw new ArgumentException(
                    "A selected-panel target cannot contain duplicate panels.",
                    nameof(panels));
            }

            Panels = new ReadOnlyCollection<Panel>(copies);
        }

        public IReadOnlyList<Panel> Panels { get; }

        // A selected scope is identified by its canonical panel values, not by the
        // collection instance created while parsing a particular UI selection.
        public bool Equals(SelectedPanels? other) =>
            ReferenceEquals(this, other)
            || other is not null
            && Panels.SequenceEqual(other.Panels);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(typeof(SelectedPanels));
            foreach (var panel in Panels)
            {
                hash.Add(panel);
            }

            return hash.ToHashCode();
        }
    }

    private static void RequireId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > MaximumIdentifierBytes)
        {
            throw new ArgumentException(
                $"A runtime identifier must be printable and at most {MaximumIdentifierBytes} UTF-8 bytes.",
                parameterName);
        }
    }
}
