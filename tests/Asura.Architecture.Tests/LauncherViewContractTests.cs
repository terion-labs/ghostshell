using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class LauncherViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Workspace_tiles_receive_the_isolation_state()
    {
        var root = Assert.IsType<XElement>(LoadComponent("LauncherView").Root);
        var tile = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                element.Name.LocalName,
                "WorkspaceRailTile",
                StringComparison.Ordinal));

        Assert.Equal("{Binding IsIsolated}", AttributeValue(tile, "IsIsolated"));
    }

    /// <summary>
    /// A saved target is one row, whichever way it can be opened. Listing it
    /// once per supported adapter said the same host four times and buried the
    /// ordinary case; the chevron carries the rest, and appears only where
    /// there are any.
    /// </summary>
    [Fact]
    public void A_saved_connection_is_one_row_with_its_other_adapters_behind_a_chevron()
    {
        var shortcut = LoadComponent("SavedConnectionShortcutView");
        var root = Assert.IsType<XElement>(shortcut.Root);
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "SplitButton", StringComparison.Ordinal));

        var buttons = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal))
            .ToArray();
        var row = Assert.Single(
            buttons,
            element => string.Equals(AttributeValue(element, "Classes"), "ListRow LauncherListRow", StringComparison.Ordinal));
        Assert.Equal("OnPrimaryClick", AttributeValue(row, "Click"));

        var chevron = Assert.Single(
            buttons,
            element => string.Equals(AttributeValue(element, "Name"), "ShortcutMenuButton", StringComparison.Ordinal));
        Assert.Equal("{Binding HasAlternatives}", AttributeValue(chevron, "IsVisible"));

        var flyout = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Flyout", StringComparison.Ordinal));
        Assert.Equal(
            "SavedConnectionMenu",
            AttributeValue(flyout, "FlyoutPresenterClasses"));
        Assert.DoesNotContain(
            flyout.Elements(),
            element => string.Equals(element.Name.LocalName, "Border", StringComparison.Ordinal));
        Assert.All(
            flyout.Descendants().Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal)),
            element => Assert.Equal(
                "FlyoutMenuItem",
                AttributeValue(element, "Classes")));
    }

    /// <summary>
    /// The launcher is the one place a saved connection is opened from. A second
    /// strip above the workspace offered the same targets in a second shape,
    /// and every capability had to be taught to both.
    /// </summary>
    [Fact]
    public void The_launcher_is_the_only_place_saved_connections_are_launched_from()
    {
        var launcher = LoadComponent("LauncherView");
        Assert.Single(
            Assert.IsType<XElement>(launcher.Root).Descendants(),
            element => string.Equals(element.Name.LocalName, "SavedConnectionShortcutView", StringComparison.Ordinal));

        Assert.DoesNotContain(
            Assert.IsType<XElement>(LoadView("WorkspaceView").Root).Descendants(),
            element => string.Equals(element.Name.LocalName, "SavedConnectionShortcutView", StringComparison.Ordinal));
    }

    [Fact]
    public void Dropdown_surfaces_share_the_saved_connection_menu_treatment()
    {
        var theme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "AsuraTheme.axaml"));

        foreach (var selector in new[]
                 {
                     "FlyoutPresenter",
                     "MenuFlyoutPresenter",
                     "ComboBox:dropdownopen /template/ Border#PopupBorder",
                     "MenuItem",
                     "ComboBoxItem",
                 })
        {
            Assert.Single(
                theme.Descendants(),
                element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                        AttributeValue(element, "Selector"),
                        selector,
                        StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Floating_sidebars_use_their_dedicated_surface_token()
    {
        var theme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "AsuraTheme.axaml"));
        var sidebarStyle = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Border.FloatingSidebar",
                    StringComparison.Ordinal));

        AssertStyleSetter(
            sidebarStyle,
            "Background",
            "{DynamicResource ShellSidebarBrush}");
        AssertStyleSetter(
            sidebarStyle,
            "BorderBrush",
            "{DynamicResource ShellSidebarBorderBrush}");
        AssertStyleSetter(sidebarStyle, "BorderThickness", "1");
        AssertStyleSetter(
            sidebarStyle,
            "CornerRadius",
            "{DynamicResource ShellSidebarCornerRadius}");

        var app = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml"));
        foreach (var resource in new[]
                 {
                     "ShellSidebarBorderBrush",
                     "ShellSidebarSelectionBrush",
                     "ShellSidebarCornerRadius",
                 })
        {
            Assert.Contains(
                app.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Key"),
                    resource,
                    StringComparison.Ordinal));
        }
    }

    private static void AssertTitleBarDragRegion(XElement root)
    {
        var titleBar = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Classes"),
                "TopChrome",
                StringComparison.Ordinal));
        Assert.Equal(
            "TitleBar",
            AttributeValue(titleBar, "WindowDecorationProperties.ElementRole"));
        Assert.Null(AttributeValue(titleBar, "PointerPressed"));
        Assert.Equal(
            "{Binding $parent[Window].TitleBarChromeHeight}",
            AttributeValue(titleBar, "MinHeight"));
    }

    [Fact]
    public void Native_titlebar_role_owns_window_drag_and_double_click()
    {
        var mainWindow = Assert.IsType<XElement>(LoadView("MainWindow").Root);
        // The window keeps its system decorations — its frame, its rounded
        // corners, its resize edges and its standard buttons. Dropping them to
        // be rid of the title bar took all of that with it, which is a far
        // bigger loss than the band it removed; the band is dealt with by
        // letting the content run under it instead.
        Assert.Equal("Full", AttributeValue(mainWindow, "WindowDecorations"));
        Assert.Equal(
            "True",
            AttributeValue(mainWindow, "ExtendClientAreaToDecorationsHint"));
        Assert.Equal(
            "-1",
            AttributeValue(mainWindow, "ExtendClientAreaTitleBarHeightHint"));

        // Extending under the decorations is only half of it. Avalonia draws
        // its own title bar and still fills it with an opaque brush; 12.0.5
        // only moved that fill behind a named resource. Without an answer to
        // it the pale strip comes straight back, which it did once already.
        var app = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml"));
        var titleBarFill = Assert.Single(
            app.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Key"),
                "TitleBarBackgroundBrush",
                StringComparison.Ordinal));
        Assert.Equal("Transparent", AttributeValue(titleBarFill, "Color"));

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class MainWindow : Window");

        // Extending under the decorations is only half of it on macOS: the
        // same switch that extends the client area also tells Avalonia's
        // content view to show a title-bar material and a separator, and
        // there is no managed way to ask for one without the other. Left
        // alone they paint behind the translucent base and read as a title
        // bar. The window has to put them back to sleep, and again whenever
        // the decorations are rebuilt.
        Assert.Contains(
            "MacOsWindowTitleBar.TryLetTheBaseSurfaceRunToTheTop(",
            codeBehind,
            StringComparison.Ordinal);
        // The corners the platform draws are decided by which kind of window
        // it is asked to be, so the ask travels with the corner setting.
        Assert.Contains("MacOsWindowKind.Toolbar", codeBehind, StringComparison.Ordinal);

        Assert.Contains("TitleBarChromeHeightProperty", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "WindowTitleBarContentMarginProperty",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("RefreshWindowChromeMetrics();", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OnTitleBarPointerPressed",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("BeginMoveDrag(e);", codeBehind, StringComparison.Ordinal);

        var nativeTitleBar = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "MacOsWindowTitleBar.cs"));
        Assert.DoesNotContain(
            "TryDoWhatADoubleClickDoes",
            nativeTitleBar,
            StringComparison.Ordinal);
        Assert.DoesNotContain("performZoom:", nativeTitleBar, StringComparison.Ordinal);
        // Avalonia makes the native title bar opaque again after entering
        // macOS full screen. The client area still extends beneath it, so that
        // band would cover the top chrome and its tabs unless the state-change
        // repair restores transparency.
        Assert.Contains(
            "setTitlebarAppearsTransparent:",
            nativeTitleBar,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_collection_counts_use_a_shared_noninteractive_component()
    {
        var launcher = LoadComponent("LauncherView");
        var root = Assert.IsType<XElement>(launcher.Root);
        var countPills = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "CountPill", StringComparison.Ordinal))
            .ToArray();

        // The assertion is on which counts exist rather than how many pills
        // render them — a second place reusing a count is fine, a new kind of
        // count is the thing worth catching.
        Assert.Equal(
            new[]
            {
                "{Binding HistoryResultCount}",
                "{Binding SavedConnectionShortcutCount}",
                "{Binding Screens.Count}",
                "{Binding Workspaces.Count}",
            },
            countPills
                .Select(element => AttributeValue(element, "Value") ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());

        var countPill = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "Components",
            "CountPill.axaml"));
        var countRoot = Assert.IsType<XElement>(countPill.Root);
        Assert.Equal("False", AttributeValue(countRoot, "Focusable"));
        Assert.Equal("False", AttributeValue(countRoot, "IsHitTestVisible"));
    }

    [Fact]
    public void Pill_components_share_a_smaller_scalable_font_token()
    {
        var designSystem = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "DesignSystem.axaml"));
        var statusChipTheme = Assert.Single(
            designSystem.Descendants(),
            element => string.Equals(element.Name.LocalName, "ControlTheme"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "TargetType"),
                    "controls:StatusChip",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            statusChipTheme,
            "FontSize",
            "{DynamicResource ShellPillFontSize}");

        var shellTheme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "AsuraTheme.axaml"));
        var interactiveChipStyle = Assert.Single(
            shellTheme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.Chip, ComboBox.Chip",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            interactiveChipStyle,
            "FontSize",
            "{DynamicResource ShellPillFontSize}");

        var app = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml"));
        Assert.Contains(
            app.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Key"),
                "ShellPillFontSize",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Navigation_style_stretches_items_and_exposes_selection_state()
    {
        var theme = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "AsuraTheme.axaml"));
        var navigationStyle = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.NavButton",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            navigationStyle,
            "HorizontalAlignment",
            "Stretch");
        AssertStyleSetter(
            navigationStyle,
            "Foreground",
            "{DynamicResource ShellTextBrush}");
        AssertStyleSetter(navigationStyle, "MinHeight", "32");
        AssertStyleSetter(navigationStyle, "Padding", "{controls:Inset Horizontal=Sm}");
        AssertStyleSetter(
            navigationStyle,
            "AutomationProperties.ItemStatus",
            string.Empty);

        var activeNavigationStyle = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.NavButton.active",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            activeNavigationStyle,
            "Background",
            "{DynamicResource ShellSidebarSelectionBrush}");
        AssertStyleSetter(
            activeNavigationStyle,
            "Foreground",
            "{DynamicResource ShellAccentBrush}");
        AssertStyleSetter(activeNavigationStyle, "FontWeight", "Medium");
        AssertStyleSetter(
            activeNavigationStyle,
            "AutomationProperties.ItemStatus",
            "Current page");

        var activeLabelStyle = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.NavButton.active TextBlock",
                    StringComparison.Ordinal));
        AssertStyleSetter(
            activeLabelStyle,
            "Foreground",
            "{DynamicResource ShellAccentBrush}");
    }

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static XElement FindUniqueAccessibleElement(
        XElement root,
        string accessibleName) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                accessibleName,
                StringComparison.Ordinal));

    private static void AssertStyleSetter(
        XElement style,
        string property,
        string value) =>
        Assert.Contains(
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

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadComponent(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "Components",
            $"{view}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
