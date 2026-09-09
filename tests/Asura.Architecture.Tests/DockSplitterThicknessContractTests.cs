using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Dock.Controls.ProportionalStackPanel;

namespace Asura.Architecture.Tests;

/// <summary>
/// The gap between docked panels comes from the spacing scale, so the density
/// setting changes it — and a dock splitter is laid out by its Width or Height,
/// which the library copies from Thickness once, when the splitter joins its
/// panel, and never again.
///
/// The panel keeps dividing the row by the current Thickness. So after a
/// density change the row is divided for one gap and drawn with another: at
/// compact, six against eight. The last panel overhangs the dock by the
/// difference, the dock clips, and what the clip takes is that panel's border —
/// which, when the panel is the focused one, is its accent outline, missing
/// down the whole right edge of the window.
/// </summary>
public sealed class DockSplitterThicknessContractTests
{
    /// <summary>
    /// The sync itself. Width stands in for whichever axis the splitter was
    /// given: a vertical splitter in a row is sized by Width, and the library
    /// leaves the other axis unset.
    /// </summary>
    [Fact]
    public void A_splitter_resized_by_the_spacing_scale_is_laid_out_at_its_new_thickness()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Asura.App.App).TypeHandle);
        var splitter = new ProportionalStackPanelSplitter { Thickness = 8 };
        // What the library does when the splitter joins its panel, and the only
        // time it does it.
        splitter.Width = splitter.Thickness;

        splitter.Thickness = 6;

        Assert.Equal(6, splitter.Width);
        Assert.True(
            double.IsNaN(splitter.Height),
            "The axis the splitter was not given must stay unset, or it is sized "
            + "across the row instead of along it.");
    }

    /// <summary>
    /// And the reason the sync is needed at all: the gap is a spacing token, not
    /// a constant. Pinning it would hide this bug rather than fix it, and would
    /// leave the one gap in the workspace that a density setting cannot reach.
    /// </summary>
    [Fact]
    public void The_gap_between_docked_panels_comes_from_the_spacing_scale()
    {
        var theme = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Asura.App",
            "Styles",
            "AsuraDockTheme.axaml"));

        var setter = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(element.Attribute("Property")?.Value, "Thickness"
, StringComparison.Ordinal) && string.Equals(element.Parent?.Attribute("Selector")?.Value
, "proportional|ProportionalStackPanelSplitter", StringComparison.Ordinal));

        Assert.Equal("{DynamicResource ShellSpaceSm}", setter.Attribute("Value")?.Value);
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
