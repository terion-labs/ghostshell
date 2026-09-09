using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Asura.Architecture.Tests;

/// <summary>
/// A menu is not glass.
///
/// The shell's surfaces are translucent when the blur is on, and inside the
/// window that reads as glass because the platform's material stands behind
/// them. A flyout, a menu flyout and a combo box's list each open in a window
/// of their own, where there is no material behind anything — so the same
/// translucency is a hole onto the desktop, and a menu you can read the
/// wallpaper through is a menu you cannot read.
///
/// These surfaces take the solid token instead. Whichever way the setting goes,
/// the answer for a popup is opaque.
/// </summary>
public sealed class PopupSurfaceContractTests
{
    private static readonly string[] PopupSelectors =
    [
        "FlyoutPresenter",
        "MenuFlyoutPresenter",
        "ComboBox:dropdownopen /template/ Border#PopupBorder",
    ];

    [Fact]
    public void Every_surface_that_opens_in_its_own_window_is_solid()
    {
        var theme = XDocument.Load(ThemePath());

        foreach (var selector in PopupSelectors)
        {
            var background = theme.Descendants()
                .Where(element => string.Equals(element.Parent?.Attribute("Selector")?.Value, selector, StringComparison.Ordinal))
                .SingleOrDefault(element => string.Equals(element.Attribute("Property")?.Value, "Background", StringComparison.Ordinal));

            Assert.True(
                background is not null,
                $"'{selector}' states no background, so it falls back to the theme's own — "
                + "which is not the shell's surface and not answerable here.");
            Assert.Equal(
                "{DynamicResource ShellPopupSurfaceBrush}",
                background!.Attribute("Value")?.Value);
        }
    }

    /// <summary>
    /// And the token itself is published solid. It is derived from the same
    /// colour as the raised surface, so the only thing separating them is the
    /// alpha the translucent one carries.
    /// </summary>
    [Fact]
    public void The_popup_surface_token_is_published_without_the_shells_translucency()
    {
        var appSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Asura.App",
            "App.axaml.cs"));

        var publish = Regex.Match(
            appSource,
            @"Publish\(""ShellPopupSurfaceBrush"",\s*(?<value>[^)]*\)?)\s*\);",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(publish.Success, "The popup surface token is never published.");
        Assert.DoesNotContain(
            "Translucent",
            publish.Groups["value"].Value,
            StringComparison.Ordinal);
    }

    private static string ThemePath() => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "Asura.App",
        "Styles",
        "AsuraTheme.axaml");

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
