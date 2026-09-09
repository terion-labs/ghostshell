using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc.Controls;
using Dock.Model.Inpc.Core;

namespace Asura.Docking;

/// <summary>
/// Serializes the stable geometry Asura owns instead of reflecting over
/// Dock's runtime graph. Owners, factories, commands, contexts, selections and
/// native windows are runtime state and are deliberately reconstructed.
/// </summary>
public sealed class DockLayoutSerializer : IDockSerializer
{
    private const string RootKind = "root";
    private const string ProportionalKind = "proportional";
    private const string DocumentDockKind = "documentDock";
    private const string DocumentKind = "document";

    public static IDockSerializer Create() => new DockLayoutSerializer();

    public string Serialize<T>(T value)
    {
        if (value is not IDockable dockable)
        {
            throw new NotSupportedException(
                "Only Dock layout nodes belong to the durable layout contract.");
        }

        return JsonSerializer.Serialize(
            Capture(dockable),
            DockLayoutJsonContext.Default.DockLayoutNode);
    }

    public T? Deserialize<T>(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var payload = JsonSerializer.Deserialize(
            text,
            DockLayoutJsonContext.Default.DockLayoutNode);
        if (payload is null)
        {
            return default;
        }

        var restored = Restore(payload);
        return restored is T typed
            ? typed
            : throw new NotSupportedException(
                $"The saved dock root cannot be restored as {typeof(T).Name}.");
    }

    public T? Load<T>(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.Deserialize(
            stream,
            DockLayoutJsonContext.Default.DockLayoutNode);
        if (payload is null)
        {
            return default;
        }

        var restored = Restore(payload);
        return restored is T typed
            ? typed
            : throw new NotSupportedException(
                $"The saved dock root cannot be restored as {typeof(T).Name}.");
    }

    public void Save<T>(Stream stream, T value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (value is not IDockable dockable)
        {
            throw new NotSupportedException(
                "Only Dock layout nodes belong to the durable layout contract.");
        }

        JsonSerializer.Serialize(
            stream,
            Capture(dockable),
            DockLayoutJsonContext.Default.DockLayoutNode);
    }

    private static DockLayoutNode Capture(IDockable dockable)
    {
        var kind = dockable switch
        {
            IRootDock => RootKind,
            IProportionalDock => ProportionalKind,
            IDocumentDock => DocumentDockKind,
            IDocument => DocumentKind,
            IProportionalDockSplitter => throw new InvalidOperationException(
                "Splitters are derived from adjacent layout nodes."),
            _ => throw new NotSupportedException(
                $"Dock node '{dockable.GetType().Name}' is not durable."),
        };
        var children = dockable is IDock { VisibleDockables: { } visible }
            ? visible
                .Where(static child => child is not IProportionalDockSplitter)
                .Select(Capture)
                .ToArray()
            : [];
        return new DockLayoutNode(
            kind,
            dockable.Id,
            dockable.Title,
            Finite(dockable.Proportion),
            Finite(dockable.MinWidth),
            Finite(dockable.MinHeight),
            dockable is IProportionalDock proportional
                ? proportional.Orientation
                : null,
            children,
            dockable is IRootDock { Windows: { } windows }
                ? [.. windows
                    .Where(static window => window.Layout is not null)
                    .Select(static window => new DockWindowNode(
                        window.Id,
                        Finite(window.X),
                        Finite(window.Y),
                        Finite(window.Width),
                        Finite(window.Height),
                        Capture(window.Layout!)))]
                : []);
    }

    private static IDockable Restore(DockLayoutNode payload)
    {
        IDockable restored = payload.Kind switch
        {
            RootKind => RestoreRoot(payload),
            ProportionalKind => RestoreProportional(payload),
            DocumentDockKind => RestoreDocumentDock(payload),
            DocumentKind => new Document
            {
                CanClose = false,
                CanFloat = false,
                CanDrag = true,
                CanDrop = true,
            },
            _ => throw new JsonException(
                $"Unknown durable dock node kind '{payload.Kind}'."),
        };
        restored.Id = payload.Id;
        restored.Title = payload.Title;
        if (payload.Proportion is { } proportion)
        {
            restored.Proportion = proportion;
        }

        if (payload.MinWidth is { } minWidth)
        {
            restored.MinWidth = minWidth;
        }

        if (payload.MinHeight is { } minHeight)
        {
            restored.MinHeight = minHeight;
        }

        return restored;
    }

    private static RootDock RestoreRoot(DockLayoutNode payload)
    {
        var children = RestoreChildren(payload.Children, includeSplitters: false);
        var root = new RootDock
        {
            IsCollapsable = false,
            VisibleDockables = children,
            ActiveDockable = FirstContent(children),
            Windows = new ObservableCollection<IDockWindow>(),
        };
        foreach (var window in payload.Windows)
        {
            root.Windows.Add(new DockWindow
            {
                Id = window.Id,
                X = window.X ?? 0,
                Y = window.Y ?? 0,
                Width = window.Width ?? 800,
                Height = window.Height ?? 600,
                Layout = Restore(window.Layout) as IRootDock
                    ?? throw new JsonException("A floating dock window requires a root layout."),
            });
        }

        return root;
    }

    private static ProportionalDock RestoreProportional(DockLayoutNode payload)
    {
        var children = RestoreChildren(payload.Children, includeSplitters: true);
        return new ProportionalDock
        {
            Orientation = payload.Orientation ?? Orientation.Horizontal,
            IsCollapsable = false,
            VisibleDockables = children,
            ActiveDockable = FirstContent(children),
        };
    }

    private static DocumentDock RestoreDocumentDock(DockLayoutNode payload)
    {
        var children = RestoreChildren(payload.Children, includeSplitters: false);
        return new DocumentDock
        {
            IsCollapsable = true,
            CanCloseLastDockable = true,
            CanCreateDocument = false,
            EnableWindowDrag = true,
            VisibleDockables = children,
            ActiveDockable = FirstContent(children),
        };
    }

    private static ObservableCollection<IDockable> RestoreChildren(
        IReadOnlyList<DockLayoutNode> payloads,
        bool includeSplitters)
    {
        var children = new ObservableCollection<IDockable>();
        for (var index = 0; index < payloads.Count; index++)
        {
            if (includeSplitters && index > 0)
            {
                children.Add(new ProportionalDockSplitter
                {
                    CanResize = true,
                    ResizePreview = false,
                });
            }

            children.Add(Restore(payloads[index]));
        }

        return children;
    }

    private static IDockable? FirstContent(IEnumerable<IDockable> children) =>
        children.FirstOrDefault(static child => child is not IProportionalDockSplitter);

    private static double? Finite(double value) =>
        double.IsFinite(value) ? value : null;
}

internal sealed record DockLayoutNode(
    string Kind,
    string Id,
    string Title,
    double? Proportion,
    double? MinWidth,
    double? MinHeight,
    Orientation? Orientation,
    DockLayoutNode[] Children,
    DockWindowNode[] Windows);

internal sealed record DockWindowNode(
    string Id,
    double? X,
    double? Y,
    double? Width,
    double? Height,
    DockLayoutNode Layout);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DockLayoutNode))]
internal sealed partial class DockLayoutJsonContext : JsonSerializerContext;
