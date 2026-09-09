using System.Globalization;

namespace Asura.Git;

/// <summary>
/// Parses <c>git ls-tree -z -l</c>: NUL-terminated records of
/// "mode SP type SP object SP size TAB name". Directories first, then files,
/// each group alphabetical — the order every tree browser presents.
/// </summary>
public static class GitTreeParser
{
    public static IReadOnlyList<GitTreeEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var entries = new List<GitTreeEntry>();
        foreach (var record in output.Split('\0'))
        {
            if (record.Length == 0)
            {
                continue;
            }

            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || tab == record.Length - 1)
            {
                throw new FormatException("Malformed ls-tree record.");
            }

            var meta = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 4)
            {
                throw new FormatException("Malformed ls-tree metadata.");
            }

            var isTree = string.Equals(meta[1], "tree", StringComparison.Ordinal);
            long? size = long.TryParse(
                meta[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedSize)
                ? parsedSize
                : null;
            entries.Add(new GitTreeEntry(record[(tab + 1)..], isTree, size));
        }

        return [.. entries
            .OrderByDescending(entry => entry.IsTree)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
