namespace Asura.Application.Previews;

/// <summary>A node of a listed hierarchy — an archive's contents, for now.</summary>
public sealed record PreviewTreeNode(
    string Name,
    string? Detail,
    bool IsContainer,
    IReadOnlyList<PreviewTreeNode> Children);

/// <summary>
/// Turns the flat paths an archive records into the folders they describe.
/// Archives store "docs/api/index.html", not a folder called "docs".
/// </summary>
public static class PreviewTreeBuilder
{
    public static IReadOnlyList<PreviewTreeNode> FromPaths(
        IEnumerable<ArchiveEntryDescriptor> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var root = new Folder();
        foreach (var entry in entries)
        {
            var segments = entry.Path
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var folder = root;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                folder = folder.Child(segments[index]);
            }

            var leaf = segments[^1];
            if (entry.IsDirectory)
            {
                folder.Child(leaf);
            }
            else
            {
                folder.Files[leaf] = entry;
            }
        }

        return root.Build();
    }

    private sealed class Folder
    {
        public SortedDictionary<string, Folder> Folders { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public SortedDictionary<string, ArchiveEntryDescriptor> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Folder Child(string name)
        {
            if (!Folders.TryGetValue(name, out var folder))
            {
                folder = new Folder();
                Folders[name] = folder;
            }

            return folder;
        }

        public IReadOnlyList<PreviewTreeNode> Build()
        {
            var nodes = new List<PreviewTreeNode>();
            foreach (var (name, folder) in Folders)
            {
                var children = folder.Build();
                nodes.Add(new PreviewTreeNode(
                    name,
                    Describe(children.Count),
                    IsContainer: true,
                    children));
            }

            foreach (var (name, entry) in Files)
            {
                nodes.Add(new PreviewTreeNode(
                    name,
                    entry.Size is { } size ? FormatSize(size) : null,
                    IsContainer: false,
                    []));
            }

            return nodes;
        }

        private static string Describe(int count) =>
            count == 1 ? "1 item" : $"{count} items";
    }

    /// <summary>
    /// Sizes as a person reads them, on the same terms as the file listing.
    /// </summary>
    public static string FormatSize(long bytes) => ByteSize.Format(bytes);
}
