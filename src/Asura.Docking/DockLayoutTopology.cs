using Dock.Model.Controls;
using Dock.Model.Core;

namespace Asura.Docking;

/// <summary>
/// Repairs the structural invariants of Asura's one-panel-per-leaf Dock tree.
/// </summary>
public static class DockLayoutTopology
{
    /// <summary>
    /// Removes empty proportional branches and gives singleton branches the full
    /// share of their parent. Dock deliberately preserves non-collapsible saved
    /// layout containers, so panel removal must enforce these runtime invariants.
    /// </summary>
    public static void Normalize(IFactory factory, IRootDock root)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(root);

        while (FindEmptyBranch(root) is { } emptyBranch)
        {
            factory.RemoveDockable(emptyBranch, collapse: true);
        }

        foreach (var dock in Enumerate(root).OfType<IProportionalDock>())
        {
            var panels = (dock.VisibleDockables ?? [])
                .Where(dockable => dockable is not IProportionalDockSplitter)
                .ToArray();
            if (panels.Length != 1)
            {
                continue;
            }

            panels[0].Proportion = 1d;
            panels[0].CollapsedProportion = 1d;
        }

        NormalizeSelections(root);
    }

    private static IProportionalDock? FindEmptyBranch(IRootDock root) =>
        Enumerate(root)
            .OfType<IProportionalDock>()
            .LastOrDefault(dock =>
                dock.Owner is IDock
                && !(dock.VisibleDockables ?? [])
                    .Any(dockable => dockable is not IProportionalDockSplitter));

    private static void NormalizeSelections(IRootDock root)
    {
        foreach (var dock in Enumerate(root).OfType<IDock>())
        {
            var children = (dock.VisibleDockables ?? [])
                .Where(dockable => dockable is not IProportionalDockSplitter)
                .ToArray();
            if (!Contains(children, dock.ActiveDockable))
            {
                dock.ActiveDockable = children.FirstOrDefault();
            }

            if (dock.DefaultDockable is { } defaultDockable
                && !Contains(children, defaultDockable))
            {
                dock.DefaultDockable = dock.ActiveDockable;
            }
        }

        if (root.FocusedDockable is { } focused
            && !Enumerate(root).Any(dockable => ReferenceEquals(dockable, focused)))
        {
            root.FocusedDockable = null;
        }
    }

    private static bool Contains(
        IReadOnlyList<IDockable> children,
        IDockable? selection) =>
        children.Any(child => ReferenceEquals(child, selection));

    private static IEnumerable<IDockable> Enumerate(IRootDock root)
    {
        var pending = new Stack<IDockable>();
        pending.Push(root);
        foreach (var window in root.Windows ?? [])
        {
            if (window.Layout is not null)
            {
                pending.Push(window.Layout);
            }
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is not IDock { VisibleDockables: { } children })
            {
                continue;
            }

            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
    }
}
