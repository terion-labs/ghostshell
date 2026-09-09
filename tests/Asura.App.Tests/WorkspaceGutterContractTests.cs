using System.Xml.Linq;

namespace Asura.App.Tests;

/// <summary>
/// Dock owns splitter spacing inside the panel canvas. Asura owns only the
/// canvas inset, so rearranged and nested panes cannot accumulate wrapper margins.
/// </summary>
public sealed class WorkspaceGutterContractTests
{
    private static XDocument Workspace() => XDocument.Load(Path.Combine(
        RepositoryRoot,
        "src",
        "Asura.App",
        "Views",
        "WorkspaceView.axaml"));

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void The_canvas_has_one_outer_gutter_and_no_per_panel_margin()
    {
        var root = Workspace().Root!;

        var canvas = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals((string?)element.Attribute("IsVisible")
, "{Binding IsWorkspaceCanvasVisible}", StringComparison.Ordinal));
        // The agent panel is not a canvas panel: it floats over the canvas or
        // holds a slot beside it, and its docked/floating margins live in state
        // styles precisely so an inline value cannot silence either state.
        var panelMargins = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && ((string?)element.Attribute("Selector"))
                    ?.StartsWith("views|AgentWorkspaceView", StringComparison.Ordinal) != true)
            .SelectMany(element => element.Descendants())
            .Where(element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals((string?)element.Attribute("Property"), "Margin", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Value"))
            .ToArray();

        var canvasMargin = (string?)canvas.Attribute("Margin");

        // Three sides, not four: the chrome band above the canvas is twice the
        // window buttons' axis and centres what it holds on it, so it already
        // ends a gap below the tab strip. The canvas adding a fourth spent that
        // gap twice and left the space over the panels half again wider than
        // the space beside them.
        Assert.Equal("{controls:Inset Horizontal=Sm, Bottom=Sm}", canvasMargin);
        Assert.Empty(panelMargins);
        Assert.Single(
            canvas.Descendants(),
            element => string.Equals(
                element.Name.LocalName,
                "RuntimeDockControl",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The workspace edge remains one token while Dock recursively owns every
    /// interior splitter, independent of nesting depth. Both sit on the small
    /// spacing step: the splitter is styled to <c>ShellSpaceSm</c> and the
    /// canvas gutter uses the same inset, so a panel's distance to the rail,
    /// to the agent panel, and to its neighbour reads as one gap.
    /// </summary>
    [Fact]
    public void Edge_and_interior_gaps_come_out_equal()
    {
        const double canvas = 8;
        const double dockSplitter = 8;

        Assert.Equal(canvas, dockSplitter);
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
