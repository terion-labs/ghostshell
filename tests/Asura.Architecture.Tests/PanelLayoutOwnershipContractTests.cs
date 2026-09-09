using System.Text.RegularExpressions;

namespace Asura.Architecture.Tests;

/// <summary>
/// A panel outlives its view.
///
/// Floating a panel, adding another beside it, or any other change to the
/// arrangement builds the view again from its markup. Anything the view was
/// holding at the time is gone, and anything the markup states comes back —
/// so a division a person chose, kept in the view, is replaced by the one
/// written in the file, while the panel goes on describing the other.
///
/// That is how a closed preview came back as a column of empty space: the
/// close zeroed the grid track in code-behind, the rebuilt view read 2* from
/// the markup, and the panel still said the preview was hidden. Layout the
/// panel has an opinion about is bound to the panel.
/// </summary>
public sealed class PanelLayoutOwnershipContractTests
{
    /// <summary>
    /// Reaching a track out of the collection at all, rather than assigning one
    /// directly — the sizes that did not survive were set through a local held
    /// for exactly two lines, and a rule that only reads the assignment misses
    /// that. Nothing a panel view legitimately needs is behind this index: how
    /// wide something came out is read from its bounds.
    /// </summary>
    private static readonly Regex TrackAccess = new(
        @"(ColumnDefinitions|RowDefinitions)\s*\[",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    [Fact]
    public void No_panel_view_reaches_into_a_grid_track_it_will_not_be_around_to_restore()
    {
        var offenders = PanelViewSources()
            .Where(source => TrackAccess.IsMatch(source.Source))
            .Select(source => Path.GetFileName(source.Path))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These panel views size grid tracks from code-behind, so the sizes go "
            + "back to the markup's on the next relayout: "
            + string.Join(", ", offenders));
    }

    private static IEnumerable<(string Path, string Source)> PanelViewSources()
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Asura.App",
            "Views",
            "RuntimePanels");

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path: path, Source: File.ReadAllText(path)));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the Asura repository root.");
    }
}
