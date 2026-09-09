using System.Globalization;
using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class DockThemeContractTests
{
    private const string AccentBrush = "{DynamicResource ShellAccentBrush}";
    private const string SelectorThemeKey = "AsuraDockTargetSelectorTheme";
    private const string LocalTargetThemeKey = "AsuraDockTargetTheme";
    private const string GlobalTargetThemeKey = "AsuraGlobalDockTargetTheme";

    private static readonly XNamespace DockSettingsNamespace = "using:Dock.Settings";

    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string> LucideGlyphs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Top"] = "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M3 9 H21 M9 16 L12 13 L15 16",
            ["Bottom"] = "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M3 15 H21 M15 8 L12 11 L9 8",
            ["Left"] = "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M9 3 V21 M16 15 L13 12 L16 9",
            ["Right"] = "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M15 3 V21 M8 9 L11 12 L8 15",
            ["Fill"] = "M3 7 V5 A2 2 0 0 1 5 3 H7 M17 3 H19 A2 2 0 0 1 21 5 V7 M21 17 V19 A2 2 0 0 1 19 21 H17 M7 21 H5 A2 2 0 0 1 3 19 V17 M8 8 H16 A1 1 0 0 1 17 9 V15 A1 1 0 0 1 16 16 H8 A1 1 0 0 1 7 15 V9 A1 1 0 0 1 8 8 Z",
        };

    private static readonly (string Part, string Operation)[] LocalOperationParts =
    [
        ("PART_TopIndicator", "Top"),
        ("PART_BottomIndicator", "Bottom"),
        ("PART_LeftIndicator", "Left"),
        ("PART_RightIndicator", "Right"),
        ("PART_CenterIndicator", "Fill"),
        ("PART_TopSelector", "Top"),
        ("PART_BottomSelector", "Bottom"),
        ("PART_LeftSelector", "Left"),
        ("PART_RightSelector", "Right"),
        ("PART_CenterSelector", "Fill"),
    ];

    private static readonly (string Part, string Operation)[] GlobalOperationParts =
    [
        ("PART_TopIndicator", "Top"),
        ("PART_BottomIndicator", "Bottom"),
        ("PART_LeftIndicator", "Left"),
        ("PART_RightIndicator", "Right"),
        ("PART_TopSelector", "Top"),
        ("PART_BottomSelector", "Bottom"),
        ("PART_LeftSelector", "Left"),
        ("PART_RightSelector", "Right"),
    ];

    [Fact]
    public void Application_loads_and_applies_the_Asura_dock_theme_after_Dock_defaults()
    {
        var application = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml"));
        var styles = Assert.Single(
            Assert.IsType<XElement>(application.Root).Elements(),
            element => string.Equals(element.Name.LocalName, "Application.Styles", StringComparison.Ordinal));
        var entries = styles.Elements().ToArray();
        var dockDefaults = Assert.Single(
            entries,
            element => string.Equals(element.Name.LocalName, "DockFluentTheme", StringComparison.Ordinal));
        var asuraTheme = Assert.Single(
            entries,
            element => string.Equals(element.Name.LocalName, "StyleInclude"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Source"),
                    "avares://Asura.App/Styles/AsuraDockTheme.axaml",
                    StringComparison.Ordinal));

        Assert.True(
            Array.IndexOf(entries, asuraTheme) > Array.IndexOf(entries, dockDefaults),
            "The Asura dock overrides must load after Dock's default theme.");

        var theme = LoadDockTheme();
        AssertThemeAssignment(
            theme,
            "dock|DockTarget",
            "{StaticResource AsuraDockTargetTheme}");
        AssertThemeAssignment(
            theme,
            "dock|GlobalDockTarget",
            "{StaticResource AsuraGlobalDockTargetTheme}");
    }

    [Fact]
    public void Dock_selectors_use_the_exact_five_Lucide_glyph_mappings()
    {
        var theme = LoadDockTheme();
        var localTarget = FindControlTheme(theme, LocalTargetThemeKey);
        var globalTarget = FindControlTheme(theme, GlobalTargetThemeKey);

        AssertGlyphMappings(localTarget, LucideGlyphs);
        AssertGlyphMappings(
            globalTarget,
            LucideGlyphs
                .Where(entry => entry.Key is not "Fill")
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal));
    }

    [Fact]
    public void Selector_surfaces_and_dock_indicators_use_the_shell_accent()
    {
        var theme = LoadDockTheme();
        var selectorTheme = FindControlTheme(theme, SelectorThemeKey);
        var accentSurface = FindNamedElement(selectorTheme, "PART_AccentSurface");

        Assert.Equal(AccentBrush, AttributeValue(accentSurface, "Background"));
        var opacity = double.Parse(
            Assert.IsType<string>(AttributeValue(accentSurface, "Opacity")),
            CultureInfo.InvariantCulture);
        Assert.True(
            opacity is > 0 and < 1,
            "The selector accent surface must remain semitransparent.");

        var glyphStyle = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Path.DockTargetGlyph",
                    StringComparison.Ordinal));
        AssertSetter(glyphStyle, "Stroke", AccentBrush);
        AssertSetter(glyphStyle, "Fill", "Transparent");

        AssertIndicatorBrushes(
            FindControlTheme(theme, LocalTargetThemeKey),
            LocalOperationParts);
        AssertIndicatorBrushes(
            FindControlTheme(theme, GlobalTargetThemeKey),
            GlobalOperationParts);
    }

    [Fact]
    public void Dock_templates_preserve_behavior_critical_parts_and_operations()
    {
        var theme = LoadDockTheme();
        var localTarget = FindControlTheme(theme, LocalTargetThemeKey);
        var globalTarget = FindControlTheme(theme, GlobalTargetThemeKey);

        AssertNamedParts(
            localTarget,
            "PART_IndicatorGrid",
            "PART_SelectorPanel",
            "PART_SelectorGrid");
        AssertOperationParts(localTarget, LocalOperationParts);

        AssertNamedParts(
            globalTarget,
            "PART_IndicatorGrid",
            "PART_SelectorPanel");
        AssertOperationParts(globalTarget, GlobalOperationParts);
    }

    [Fact]
    public void Dock_theme_contains_no_stock_raster_targets_or_literal_blue()
    {
        var path = DockThemePath();
        var source = File.ReadAllText(path);
        var theme = XDocument.Load(path);

        foreach (var stockRaster in new[]
                 {
                     "DockAnchorableTop.png",
                     "DockAnchorableBottom.png",
                     "DockAnchorableLeft.png",
                     "DockAnchorableRight.png",
                     "DockDocumentInside.png",
                 })
        {
            Assert.DoesNotContain(stockRaster, source, StringComparison.Ordinal);
        }

        var elements = Assert.IsType<XElement>(theme.Root).DescendantsAndSelf();
        Assert.DoesNotContain(elements, element => string.Equals(element.Name.LocalName, "Image", StringComparison.Ordinal));
        Assert.DoesNotContain(
            elements.SelectMany(element => element.Attributes()),
            attribute => attribute.Value.StartsWith('#')
                || attribute.Value.Contains("blue", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertGlyphMappings(
        XElement controlTheme,
        IReadOnlyDictionary<string, string> expectedGlyphs)
    {
        var selectors = controlTheme
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ContentControl", StringComparison.Ordinal))
            .Where(element => AttributeValue(element, "Name")?.EndsWith(
                "Selector",
                StringComparison.Ordinal) is true)
            .ToArray();

        Assert.Equal(expectedGlyphs.Count, selectors.Length);
        foreach (var (operation, data) in expectedGlyphs)
        {
            var selector = Assert.Single(
                selectors,
                element => string.Equals(
                    DockOperation(element),
                    operation,
                    StringComparison.Ordinal));
            var glyph = Assert.Single(
                selector.Elements(),
                element => string.Equals(element.Name.LocalName, "Path", StringComparison.Ordinal));

            Assert.Equal(
                "{StaticResource AsuraDockTargetSelectorTheme}",
                AttributeValue(selector, "Theme"));
            Assert.Equal("DockTargetGlyph", AttributeValue(glyph, "Classes"));
            Assert.Equal(data, AttributeValue(glyph, "Data"));
        }
    }

    private static void AssertIndicatorBrushes(
        XElement controlTheme,
        IEnumerable<(string Part, string Operation)> operationParts)
    {
        foreach (var (part, _) in operationParts.Where(entry =>
                     entry.Part.EndsWith("Indicator", StringComparison.Ordinal)))
        {
            Assert.Equal(
                AccentBrush,
                AttributeValue(FindNamedElement(controlTheme, part), "Background"));
        }
    }

    private static void AssertOperationParts(
        XElement controlTheme,
        IReadOnlyCollection<(string Part, string Operation)> expectedParts)
    {
        var operationParts = controlTheme
            .Descendants()
            .Where(element => DockOperation(element) is not null)
            .ToArray();

        Assert.Equal(expectedParts.Count, operationParts.Length);
        foreach (var (part, operation) in expectedParts)
        {
            Assert.Equal(
                operation,
                DockOperation(FindNamedElement(controlTheme, part)));
        }
    }

    private static void AssertNamedParts(XElement controlTheme, params string[] parts)
    {
        foreach (var part in parts)
        {
            _ = FindNamedElement(controlTheme, part);
        }
    }

    private static void AssertSetter(
        XElement style,
        string property,
        string value)
    {
        Assert.Single(
            style.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Property"),
                    property,
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Value"),
                    value,
                    StringComparison.Ordinal));
    }

    private static void AssertThemeAssignment(
        XDocument theme,
        string selector,
        string targetTheme)
    {
        var style = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    selector,
                    StringComparison.Ordinal));

        AssertSetter(style, "Theme", targetTheme);
    }

    private static XElement FindControlTheme(XDocument theme, string key) =>
        Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "ControlTheme"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Key"),
                    key,
                    StringComparison.Ordinal));

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static string? DockOperation(XElement element) =>
        element.Attribute(
            DockSettingsNamespace + "DockProperties.IndicatorDockOperation")?.Value;

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;

    private static XDocument LoadDockTheme() => XDocument.Load(DockThemePath());

    private static string DockThemePath() => Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        "Styles",
        "AsuraDockTheme.axaml");
}
