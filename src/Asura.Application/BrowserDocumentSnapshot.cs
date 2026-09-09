using System.Collections.ObjectModel;

namespace Asura.Application;

/// <summary>
/// A bounded immutable projection of one exact committed browser document.
/// Nodes are ordered in pre-order and references never outlive the bound
/// document.
/// </summary>
public sealed record BrowserDocumentSnapshot
{
    public const int MaximumNodeCount = 512;

    public BrowserDocumentSnapshot(
        BrowserDocumentBinding document,
        IReadOnlyList<BrowserSnapshotNode> nodes,
        DateTimeOffset capturedAtUtc,
        bool isTruncated = false)
    {
        Document = document
            ?? throw new ArgumentNullException(nameof(document));
        Nodes = SnapshotNodes(nodes, document);
        CapturedAtUtc = capturedAtUtc;
        IsTruncated = isTruncated;
    }

    public BrowserDocumentBinding Document { get; }

    public IReadOnlyList<BrowserSnapshotNode> Nodes { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public bool IsTruncated { get; }

    private static IReadOnlyList<BrowserSnapshotNode> SnapshotNodes(
        IReadOnlyList<BrowserSnapshotNode> nodes,
        BrowserDocumentBinding document)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count > MaximumNodeCount)
        {
            throw new ArgumentException(
                $"A browser document snapshot cannot contain more than "
                + $"{MaximumNodeCount} nodes.",
                nameof(nodes));
        }

        var references = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new BrowserSnapshotNode[nodes.Count];
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index]
                ?? throw new ArgumentException(
                    "A browser document snapshot cannot contain null nodes.",
                    nameof(nodes));
            if (index == 0 && node.Depth != 0)
            {
                throw new ArgumentException(
                    "The first browser snapshot node must be at depth zero.",
                    nameof(nodes));
            }

            if (index > 0
                && node.Depth > snapshot[index - 1].Depth + 1)
            {
                throw new ArgumentException(
                    "Browser snapshot node depth cannot skip a parent level.",
                    nameof(nodes));
            }

            if (node.Reference is { } reference
                && (reference.Document != document
                    || !references.Add(reference.Value)))
            {
                throw new ArgumentException(
                    "Browser snapshot references must be unique and bound to "
                    + "the captured document.",
                    nameof(nodes));
            }

            snapshot[index] = node;
        }

        return new ReadOnlyCollection<BrowserSnapshotNode>(snapshot);
    }
}
