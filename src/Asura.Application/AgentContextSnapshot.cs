using System.Collections.ObjectModel;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// A point-in-time descriptive context projection. It conveys no reusable authority; callers
/// must re-resolve target ownership before every agent operation.
/// </summary>
public sealed record AgentContextSnapshot
{
    public AgentContextSnapshot(
        AgentTarget target,
        IEnumerable<AgentContextPanel> panels,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(panels);
        var copies = panels
            .Select(panel => panel ?? throw new ArgumentException(
                "An agent context cannot contain null panels.",
                nameof(panels)))
            .ToArray();
        if (copies.Length is 0 or > AgentContextRequest.MaximumAllowedPanelCount)
        {
            throw new ArgumentException(
                "An agent context must contain a bounded, non-empty panel collection.",
                nameof(panels));
        }

        if (copies
            .Select(panel => (
                panel.WindowId,
                panel.WorkspaceId,
                panel.TabId,
                panel.PanelId))
            .Distinct()
            .Count() != copies.Length)
        {
            throw new ArgumentException(
                "An agent context cannot contain duplicate panels.",
                nameof(panels));
        }

        Target = target;
        Panels = new ReadOnlyCollection<AgentContextPanel>(copies);
        CapturedAtUtc = capturedAtUtc;
        Revision = copies.Max(panel =>
            Math.Max(panel.WorkspaceRevision, panel.SessionRevision ?? 0));
        BindingFingerprint = AgentContextBindingFingerprint.Create(target, Panels);
    }

    public AgentTarget Target { get; }

    public IReadOnlyList<AgentContextPanel> Panels { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public long Revision { get; }

    /// <summary>
    /// Canonical comparison evidence for execution-time re-resolution. It does
    /// not authorize an operation.
    /// </summary>
    public AgentActionDigest BindingFingerprint { get; }
}
