using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class RuntimePanelViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Docker_files_embed_the_file_viewer_instead_of_declaring_another_browser()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var sharedViewer = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "FileRuntimePanelView", StringComparison.Ordinal));

        Assert.Equal("True", AttributeValue(sharedViewer, "IsEmbedded"));
        Assert.DoesNotContain(
            root.DescendantsAndSelf().Attributes(),
            attribute => attribute.Value.Contains("Docker files and folders", StringComparison.Ordinal)
                || attribute.Value.Contains("DockerFile", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "DockerFileBrowserViewModel.cs")));
    }

    [Fact]
    public void Docker_json_uses_the_shared_highlighted_code_preview()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var preview = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "CodePreviewView"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Text"), "{Binding Inspection.Json}", StringComparison.Ordinal));

        Assert.Equal("inspection.json", AttributeValue(preview, "FileName"));
        Assert.Equal("False", AttributeValue(preview, "WordWrap"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBox"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Text"), "{Binding Inspection.Json}", StringComparison.Ordinal));
    }

    [Fact]
    public void Docker_image_navigation_uses_archive_icons()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var imageButtons = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal))
            .Where(element =>
                element.Descendants().Any(descendant => string.Equals(descendant.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(AttributeValue(descendant, "Text"), "Images", StringComparison.Ordinal))
                || string.Equals(AttributeValue(element, "ToolTip.Tip"), "Images", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(imageButtons);
        Assert.All(
            imageButtons,
            button => Assert.Contains(
                button.Descendants(),
                element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), "Archive", StringComparison.Ordinal)));
    }

    [Fact]
    public void Docker_logs_use_a_virtualized_paged_surface_with_remote_controls()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var logList = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ListBox"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Name"), "LogList", StringComparison.Ordinal));

        Assert.Contains(
            logList.Descendants(),
            element => string.Equals(element.Name.LocalName, "VirtualizingStackPanel", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBox"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "PlaceholderText"), "Search all container logs", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ToggleSwitch"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name"), "Follow container logs", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "Download complete container logs", StringComparison.Ordinal));
    }

    [Fact]
    public void Docker_container_actions_use_accessible_icons_and_name_the_terminal_action_shell()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var actions = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "StackPanel"
, StringComparison.Ordinal) && HasClass(element, "DockerContainerActions"));
        var expectedActions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{Binding StartCommand}"] = "Play",
            ["{Binding StopCommand}"] = "Stop",
            ["{Binding RestartCommand}"] = "ArrowClockwise",
            ["{Binding PauseCommand}"] = "Pause",
            ["{Binding ResumeCommand}"] = "PauseOff",
        };
        var expectedEnabledBindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{Binding StartCommand}"] = "{Binding CanStartSelectedContainer}",
            ["{Binding StopCommand}"] = "{Binding CanStopSelectedContainer}",
            ["{Binding RestartCommand}"] = "{Binding CanRestartSelectedContainer}",
            ["{Binding PauseCommand}"] = "{Binding CanPauseSelectedContainer}",
            ["{Binding ResumeCommand}"] = "{Binding CanResumeSelectedContainer}",
        };

        foreach (var (command, symbol) in expectedActions)
        {
            var button = Assert.Single(
                actions.Descendants(),
                element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Command"), command, StringComparison.Ordinal));
            Assert.True(HasClass(button, "IconButton"));
            Assert.NotNull(AttributeValue(button, "ToolTip.Tip"));
            Assert.NotNull(AttributeValue(button, "AutomationProperties.Name"));
            Assert.Equal(
                expectedEnabledBindings[command],
                AttributeValue(button, "IsEnabled"));
            Assert.Contains(
                button.Descendants(),
                element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), symbol, StringComparison.Ordinal));
        }

        var actionToggles = actions.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Width"), "32"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Height"), "32", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, actionToggles.Length);
        var startStopToggle = Assert.Single(
            actionToggles,
            element => element.Descendants().Any(button => string.Equals(AttributeValue(button, "Command"), "{Binding StartCommand}", StringComparison.Ordinal)));
        Assert.Equal(2, startStopToggle.Elements().Count(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal)));
        Assert.Contains(
            startStopToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Command"), "{Binding StartCommand}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{Binding SelectedContainerIsStopped}", StringComparison.Ordinal));
        Assert.Contains(
            startStopToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Command"), "{Binding StopCommand}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{Binding SelectedContainerIsActive}", StringComparison.Ordinal));

        var pauseToggle = Assert.Single(
            actionToggles,
            element => element.Descendants().Any(button => string.Equals(AttributeValue(button, "Command"), "{Binding PauseCommand}", StringComparison.Ordinal)));
        Assert.Equal(2, pauseToggle.Elements().Count(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal)));
        Assert.Contains(
            pauseToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Command"), "{Binding PauseCommand}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{Binding !SelectedResource.IsPaused}", StringComparison.Ordinal));
        Assert.Contains(
            pauseToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Command"), "{Binding ResumeCommand}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{Binding SelectedResource.IsPaused}", StringComparison.Ordinal));

        var lifecycleSurface = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "Button.IconButton.DockerToolbarAction /template/ ContentPresenter", StringComparison.Ordinal));
        Assert.Contains(
            lifecycleSurface.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "Background"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value")
, "{DynamicResource ShellControlSurfaceBrush}", StringComparison.Ordinal));

        var disabledLifecycleSurface = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "Button.IconButton.DockerToolbarAction:disabled /template/ ContentPresenter", StringComparison.Ordinal));
        Assert.Contains(
            disabledLifecycleSurface.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "Background"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value")
, "{DynamicResource ShellControlSurfaceBrush}", StringComparison.Ordinal));

        var shell = Assert.Single(
            actions.Elements(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Click"), "OnOpenShellClick", StringComparison.Ordinal));
        Assert.Contains(
            shell.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), "Open", StringComparison.Ordinal));
        Assert.Contains(
            shell.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Text"), "Shell", StringComparison.Ordinal));
        Assert.DoesNotContain(
            shell.Descendants(),
            element => string.Equals(AttributeValue(element, "Text"), "New tab", StringComparison.Ordinal));
    }

    [Fact]
    public void Docker_volume_size_loading_is_visible_and_accessible()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var indicator = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ProgressBar"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{Binding ShowResourceProgress}", StringComparison.Ordinal));

        Assert.True(HasClass(indicator, "WorkingStripe"));
        Assert.Equal("Bottom", AttributeValue(indicator, "VerticalAlignment"));
        Assert.Equal(
            "Loading Docker resources",
            AttributeValue(indicator, "AutomationProperties.Name"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ProgressBar"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible"), "{Binding ShowLoading}", StringComparison.Ordinal));
    }

    [Fact]
    public void Docker_detail_tabs_collapse_to_an_accessible_menu_in_narrow_panels()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var tabs = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "DockerTab"))
            .ToArray();

        Assert.Equal(6, tabs.Length);
        Assert.All(
            tabs,
            tab =>
            {
                Assert.NotNull(AttributeValue(tab, "AutomationProperties.Name"));
                Assert.Contains(
                    tab.Descendants(),
                    element => string.Equals(element.Name.LocalName, "SymbolIcon", StringComparison.Ordinal));
                Assert.Contains(
                    tab.Descendants(),
                    element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && HasClass(element, "DockerTabLabel"));
            });

        var tabHost = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "StackPanel"
, StringComparison.Ordinal) && HasClass(element, "DockerDetailTabs"));
        Assert.Equal(6, tabHost.Elements().Count(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal) && HasClass(element, "DockerTab")));

        var menuButton = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "DockerDetailMenuButton"));
        Assert.Equal(
            "Open container view menu",
            AttributeValue(menuButton, "AutomationProperties.Name"));
        Assert.Contains(
            menuButton.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), "Navigation", StringComparison.Ordinal));
        var menu = Assert.Single(
            menuButton.Descendants(),
            element => string.Equals(element.Name.LocalName, "MenuFlyout", StringComparison.Ordinal));
        var menuItems = menu.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "MenuItem", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(6, menuItems.Length);
        Assert.All(menuItems, item =>
        {
            Assert.NotNull(AttributeValue(item, "Click"));
            Assert.NotNull(AttributeValue(item, "IsVisible"));
            Assert.NotNull(AttributeValue(item, "AutomationProperties.Name"));
        });

        var hideTabsStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "Grid.compactDetails StackPanel.DockerDetailTabs", StringComparison.Ordinal));
        Assert.Contains(
            hideTabsStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "IsVisible"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value"), "False", StringComparison.Ordinal));
        var showMenuStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "Grid.compactDetails Button.DockerDetailMenuButton.hasSelection", StringComparison.Ordinal));
        Assert.Contains(
            showMenuStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "IsVisible"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value"), "True", StringComparison.Ordinal));
    }

    [Fact]
    public void Docker_family_rail_is_wide_and_aligns_count_chips_to_its_right_edge()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var railStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector"), "Border.DockerFamilyRail", StringComparison.Ordinal));
        var width = Assert.Single(
            railStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "Width", StringComparison.Ordinal));
        Assert.Equal("180", AttributeValue(width, "Value"));

        var navigationStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector"), "Button.DockerNav", StringComparison.Ordinal));
        var horizontalAlignment = Assert.Single(
            navigationStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "HorizontalAlignment", StringComparison.Ordinal));
        Assert.Equal("Stretch", AttributeValue(horizontalAlignment, "Value"));

        var navigationButtons = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "DockerNav"))
            .ToArray();
        Assert.Equal(4, navigationButtons.Length);
        Assert.All(
            navigationButtons,
            button =>
            {
                var count = Assert.Single(
                    button.Descendants(),
                    element => string.Equals(element.Name.LocalName, "StatusChip", StringComparison.Ordinal));
                Assert.Null(AttributeValue(count, "Width"));
                Assert.Equal("28", AttributeValue(count, "MinWidth"));
                Assert.Equal("Right", AttributeValue(count, "HorizontalAlignment"));
                Assert.All(
                    button.Descendants().Where(element => element.Name.LocalName is
                        "SymbolIcon" or "TextBlock" or "StatusChip"),
                    element => Assert.Equal(
                        "Center",
                        AttributeValue(element, "VerticalAlignment")));
            });
    }

    [Fact]
    public void Docker_typography_uses_scaled_line_heights_and_inspection_values_wrap()
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);

        var explicitlySizedText = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "TextBlock", StringComparison.Ordinal))
            .Select(element => new
            {
                Element = element,
                FontSize = AttributeValue(element, "FontSize") ?? string.Empty,
            })
            .Where(item => item.FontSize.StartsWith(
                "{DynamicResource ShellFontSize",
                StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(explicitlySizedText);
        Assert.All(
            explicitlySizedText,
            item => Assert.Equal(
                item.FontSize.Replace("ShellFontSize", "ShellLineHeight", StringComparison.Ordinal),
                AttributeValue(item.Element, "LineHeight")));

        var propertyValue = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && HasClass(element, "DockerPropertyValue"));
        Assert.Equal("Wrap", AttributeValue(propertyValue, "TextWrapping"));
        Assert.Equal(
            "{DynamicResource ShellLineHeight12}",
            AttributeValue(propertyValue, "LineHeight"));
        Assert.Null(AttributeValue(propertyValue, "TextTrimming"));
    }

    [Fact]
    public void Docker_vertical_collections_use_adaptive_full_width_item_containers()
    {
        var docker = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("DockerRuntimePanelView", ".axaml")).Root);
        var verticalCollections = docker.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ItemsControl", StringComparison.Ordinal))
            .Where(element => AttributeValue(element, "ItemsSource") is
                "{Binding ContainerStacks}"
                or "{Binding Containers}"
                or "{Binding Inspection.Properties}")
            .ToArray();

        Assert.Equal(3, verticalCollections.Length);
        Assert.All(verticalCollections, collection =>
            Assert.True(HasClass(collection, "StretchItems")));

        var stackHeaderBackground = Assert.Single(
            docker.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector"), "Border.DockerStackHeaderSurface", StringComparison.Ordinal));
        Assert.Contains(
            stackHeaderBackground.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "Background"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value")
, "{DynamicResource ShellSurfaceHoverBrush}", StringComparison.Ordinal));
        Assert.Contains(
            stackHeaderBackground.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "BorderBrush"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value")
, "{DynamicResource ShellAccentBrush}", StringComparison.Ordinal));

        var stackHeader = Assert.Single(
            docker.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && HasClass(element, "DockerStackHeader"));
        Assert.Equal(
            "{controls:Inset Horizontal=Sm, Vertical=Xs}",
            AttributeValue(stackHeader, "Margin"));
        Assert.Equal("Auto,*,Auto", AttributeValue(stackHeader, "ColumnDefinitions"));
        var stackToggle = Assert.Single(
            stackHeader.Elements(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "DockerStackToggle"));
        Assert.Equal("0", AttributeValue(stackToggle, "Grid.Column"));
        var stackActionsHost = Assert.Single(
            stackHeader.Elements(),
            element => string.Equals(element.Name.LocalName, "StackPanel"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Grid.Column"), "2", StringComparison.Ordinal));
        Assert.Equal("Right", AttributeValue(stackActionsHost, "HorizontalAlignment"));
        Assert.DoesNotContain(
            stackHeader.Elements(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Width"), "2", StringComparison.Ordinal));
        var resourceRowTemplate = Assert.Single(
            docker.Descendants(),
            element => string.Equals(element.Name.LocalName, "DataTemplate"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Key"), "DockerResourceItemTemplate", StringComparison.Ordinal));
        var resourceRow = Assert.Single(
            resourceRowTemplate.Descendants(),
            element => string.Equals(element.Name.LocalName, "ListItem"
, StringComparison.Ordinal) && HasClass(element, "DockerResourceListItem"));
        Assert.Equal("{DynamicResource ShellListRowPadding}", AttributeValue(resourceRow, "ContentPadding"));
        Assert.Equal("{Binding Title}", AttributeValue(resourceRow, "Title"));
        Assert.Equal("{Binding Subtitle}", AttributeValue(resourceRow, "Detail"));
        Assert.Equal("{Binding Tertiary}", AttributeValue(resourceRow, "Metadata"));

        var containerResourceRow = Assert.Single(
            docker.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "DockerResourceRow"));
        Assert.Equal(
            "{StaticResource DockerResourceItemTemplate}",
            AttributeValue(containerResourceRow, "ContentTemplate"));
        var flatResourceList = Assert.Single(
            docker.Descendants(),
            element => string.Equals(element.Name.LocalName, "ListBox"
, StringComparison.Ordinal) && HasClass(element, "DockerResources"));
        Assert.Equal(
            "{StaticResource DockerResourceItemTemplate}",
            AttributeValue(flatResourceList, "ItemTemplate"));
        Assert.DoesNotContain(
            docker.Descendants(),
            element => HasClass(element, "DockerStackRowContent"));

        var stackActions = stackActionsHost.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal))
            .Where(element => AttributeValue(element, "AutomationProperties.Name")
                ?.EndsWith(" stack", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(5, stackActions.Length);
        Assert.Equal(
            ["Start stack", "Stop stack", "Restart stack", "Pause stack", "Resume stack"],
            stackActions.Select(button => AttributeValue(button, "AutomationProperties.Name")), StringComparer.Ordinal);
        var stackActionToggles = stackActionsHost.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Width"), "24"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Height"), "24", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, stackActionToggles.Length);
        var stackStartStopToggle = Assert.Single(
            stackActionToggles,
            element => element.Descendants().Any(button => string.Equals(AttributeValue(button, "Click"), "OnStartStackClick", StringComparison.Ordinal)));
        Assert.Contains(
            stackStartStopToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Click"), "OnStartStackClick"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible"), "{Binding !CanStop}", StringComparison.Ordinal));
        Assert.Contains(
            stackStartStopToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Click"), "OnStopStackClick"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible"), "{Binding CanStop}", StringComparison.Ordinal));
        var stackPauseResumeToggle = Assert.Single(
            stackActionToggles,
            element => element.Descendants().Any(button => string.Equals(AttributeValue(button, "Click"), "OnPauseStackClick", StringComparison.Ordinal)));
        Assert.Contains(
            stackPauseResumeToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Click"), "OnPauseStackClick"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible"), "{Binding CanPause}", StringComparison.Ordinal));
        Assert.Contains(
            stackPauseResumeToggle.Elements(),
            element => string.Equals(AttributeValue(element, "Click"), "OnResumeStackClick"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible"), "{Binding !CanPause}", StringComparison.Ordinal));

        var statusTiles = docker.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "IdentityTile"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Tint"), "{Binding StatusColor}", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(statusTiles);
        Assert.DoesNotContain(
            docker.Descendants(),
            element => string.Equals(element.Name.LocalName, "Ellipse"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Fill"), "{Binding StatusColor}", StringComparison.Ordinal));

        var resourceRowStyle = Assert.Single(
            docker.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector"), "Button.DockerResourceRow", StringComparison.Ordinal));
        Assert.Contains(
            resourceRowStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "Margin"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value")
, "{controls:Inset Horizontal=Sm}", StringComparison.Ordinal));
        Assert.Contains(
            resourceRowStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "HorizontalAlignment"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value"), "Stretch", StringComparison.Ordinal));

        var designSystem = Assert.IsType<XElement>(
            XDocument.Load(Path.Combine(
                ApplicationViews.RepositoryRoot,
                "src",
                "Asura.App",
                "Styles",
                "DesignSystem.axaml")).Root);
        var generatedItemStyle = Assert.Single(
            designSystem.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "ItemsControl.StretchItems > ContentPresenter", StringComparison.Ordinal));
        Assert.Contains(
            generatedItemStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "HorizontalAlignment"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value"), "Stretch", StringComparison.Ordinal));
    }

    [Fact]
    public void List_selection_surfaces_keep_one_consistent_side_gutter()
    {
        var theme = Assert.IsType<XElement>(
            XDocument.Load(Path.Combine(
                ApplicationViews.RepositoryRoot,
                "src",
                "Asura.App",
                "Styles",
                "AsuraTheme.axaml")).Root);

        foreach (var selector in new[] { "ListBoxItem", "Button.ListRow" })
        {
            var style = Assert.Single(
                theme.Descendants(),
                element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector"), selector, StringComparison.Ordinal));
            Assert.Contains(
                style.Elements(),
                element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "Margin"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value")
, "{controls:Inset Horizontal=Xs}", StringComparison.Ordinal));
        }

        var processMonitor = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath("ProcessMonitorRuntimePanelView", ".axaml")).Root);
        var processList = Assert.Single(
            processMonitor.Descendants(),
            element => string.Equals(element.Name.LocalName, "ListBox"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "Process list", StringComparison.Ordinal));
        Assert.Equal("{controls:Inset Xs}", AttributeValue(processList, "Margin"));
    }

    /// <summary>
    /// Every panel wears the same chrome, and it comes from the component rather
    /// than from the view.
    ///
    /// It used to be written out in each of these eight views, and had drifted the
    /// way duplicated markup does. The browser declared its split buttons with no
    /// <c>Grid.Column</c>, so they rendered stacked on top of its status dot where
    /// nobody could press them — the panel looked as though it simply had no split
    /// controls. Its two split icons were also swapped relative to the other seven,
    /// and two views set a header height the other six did not.
    ///
    /// None of that is reachable from a view any more, which is what this asserts.
    /// </summary>
    [Theory]
    [InlineData("TerminalRuntimePanelView")]
    [InlineData("BrowserRuntimePanelView")]
    [InlineData("FileRuntimePanelView")]
    [InlineData("StatisticsRuntimePanelView")]
    [InlineData("ProcessMonitorRuntimePanelView")]
    [InlineData("DatabaseRuntimePanelView")]
    [InlineData("RedisRuntimePanelView")]
    [InlineData("UnavailableRuntimePanelView")]
    [InlineData("PanelPlaceholderView")]
    public void Panels_take_their_chrome_from_the_component_rather_than_drawing_it(
        string viewName)
    {
        var root = Assert.IsType<XElement>(
            XDocument.Load(RuntimePanelPath(viewName, ".axaml")).Root);
        var chrome = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "PanelChrome", StringComparison.Ordinal));

        Assert.Equal("OnCloseClick", AttributeValue(chrome, "CloseRequested"));
        Assert.Equal("OnSplitRequested", AttributeValue(chrome, "SplitRequested"));
        Assert.Equal("{Binding IsActive}", AttributeValue(chrome, "IsActive"));
        Assert.Equal(
            "{Binding IsAgentActive}",
            AttributeValue(chrome, "IsAgentActive"));
        Assert.Equal("{Binding IsZoomed}", AttributeValue(chrome, "IsZoomed"));
        Assert.NotNull(AttributeValue(chrome, "Title"));

        foreach (var owned in new[] { "PanelDockHandle", "SurfaceCard" })
        {
            Assert.DoesNotContain(
                root.Elements(),
                element => string.Equals(element.Name.LocalName, owned, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "PanelHeader"));

        // Nor the other end of it. Two panels drew the same footer by hand —
        // surface fill, a hairline along the top, muted nine-point text — which
        // is how the header started before it was one component. A footer is a
        // slot now, and the browser needs it to be exactly the strip the
        // component draws: its page covers everything else the panel has.
        //
        // Along the bottom of the panel itself, which a strip inside some pane of
        // it is not — the file panel's preview toggles sit on the same hairline
        // and are nobody's footer.
        Assert.DoesNotContain(
            chrome.Elements().SelectMany(content => content.Elements()),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "BorderThickness"), "0,1,0,0"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Background")
, "{DynamicResource ShellSurfaceBrush}", StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "AutomationProperties.Name")
                is "Split this panel left and right"
                or "Split this panel top and bottom");
    }

    /// <summary>
    /// And the component draws it: the card, the title as Dock's drag surface so
    /// panels need no second tab strip, and split/split/close in that order.
    /// </summary>
    [Fact]
    public void The_panel_component_owns_the_card_the_drag_handle_and_the_shared_actions()
    {
        var theme = Assert.Single(
            DesignSystem().Descendants(),
            element => string.Equals(element.Name.LocalName, "ControlTheme"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "TargetType"),
                    "controls:PanelChrome",
                    StringComparison.Ordinal));

        var card = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "SurfaceCard", StringComparison.Ordinal));
        Assert.Equal("Panel", AttributeValue(card, "Tone"));
        Assert.Equal("True", AttributeValue(card, "ClipsContent"));
        Assert.Equal(
            "{TemplateBinding IsActive}",
            AttributeValue(card, "Classes.active"));

        // The title is the drag surface. It no longer needs a hand-set data
        // context: a handle declared in a template cannot know where it will be
        // used, so PanelDockHandle finds its dockable by walking to the ancestor
        // that holds one.
        var handle = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "PanelDockHandle", StringComparison.Ordinal));
        Assert.Null(AttributeValue(handle, "DataContext"));

        var handleSource = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Controls",
            "PanelDockHandle.cs"));
        // It points itself at that dockable. Dock reads the drag source's own
        // data context to decide what is being dragged, so a handle whose context
        // is something else is not a drag surface at all. Moving the handle into
        // the component dropped the binding each view used to set by hand, and
        // nothing failed — panels just stopped being draggable.
        Assert.Contains(
            "Bind(DataContextProperty, host.GetObservable(DataContextProperty))",
            handleSource,
            StringComparison.Ordinal);

        var actions = theme
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal))
            .Select(element => AttributeValue(element, "Name") ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "PART_Float",
                "PART_Dock",
                "PART_SplitLeftRight",
                "PART_SplitTopBottom",
                "PART_Close",
            ],
            actions);

        // Float and dock are one control in two states, so exactly one of them is
        // ever offered. Floating used to be a double-click on the title — nothing
        // announced it, and nothing undid it.
        var floatOut = theme.Descendants().Single(element => string.Equals(AttributeValue(element, "Name"), "PART_Float", StringComparison.Ordinal));
        var dockBack = theme.Descendants().Single(element => string.Equals(AttributeValue(element, "Name"), "PART_Dock", StringComparison.Ordinal));
        Assert.Equal("{TemplateBinding CanFloat}", AttributeValue(floatOut, "IsVisible"));
        Assert.Equal("{TemplateBinding IsFloating}", AttributeValue(dockBack, "IsVisible"));

        // A panel says what it is doing along its bottom, and for one holding an
        // operating-system view that strip is the only part of the panel the
        // shell still draws — such a view is composited above every Avalonia
        // pixel. Anything the browser panel needs seen or pressed lives there,
        // including the corner a floating panel is resized from, so the footer
        // is load-bearing rather than decorative.
        var footer = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "PanelFooter"));
        Assert.Equal("{TemplateBinding IsFooterVisible}", AttributeValue(footer, "IsVisible"));

        var browser = XDocument.Load(RuntimePanelPath("BrowserRuntimePanelView", ".axaml"));
        Assert.Contains(
            browser.Descendants(),
            element => string.Equals(element.Name.LocalName, "PanelChrome.Footer", StringComparison.Ordinal));

        // The browser drew the two splits the other way round for as long as it
        // drew them itself.
        var splits = theme
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "SymbolIcon", StringComparison.Ordinal))
            .Select(element => AttributeValue(element, "Symbol") ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "WindowMultiple",
                "WindowMultipleOff",
                "SplitVertical",
                "SplitHorizontal",
                "Dismiss",
            ],
            splits);

        Assert.DoesNotContain(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), "Bot", StringComparison.Ordinal));

        var agentGlow = Assert.Single(
            theme.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "PanelAgentGlow"));
        Assert.Equal(
            "{TemplateBinding IsAgentActive}",
            AttributeValue(agentGlow, "IsVisible"));
        Assert.Equal(
            "{DynamicResource ShellAgentPanelGlowShadow}",
            AttributeValue(agentGlow, "BoxShadow"));
        Assert.Equal(
            "{DynamicResource ShellAgentPanelGlowBrush}",
            AttributeValue(agentGlow, "Background"));
        Assert.Null(AttributeValue(agentGlow, "Margin"));
        Assert.Equal(
            "{Binding CornerRadius, ElementName=PART_PanelSurface}",
            AttributeValue(agentGlow, "CornerRadius"));

        var chrome = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Controls",
            "PanelChrome.cs"));
        // Raised as the chrome, not as the button inside its template: the shell
        // resolves which panel to act on from the sender's data context, and the
        // chrome's is the panel.
        Assert.Contains(
            "CloseRequested?.Invoke(this, e);",
            chrome,
            StringComparison.Ordinal);
        Assert.Contains(
            "SplitRequested?.Invoke(this, PanelSplitOrientation.LeftRight);",
            chrome,
            StringComparison.Ordinal);
        // Float is asked for, not done. A panel floats inside the shell's window
        // — a panel holding an operating-system view cannot change window without
        // that view being destroyed — so which panels are floating is the
        // workspace's to know, and the chrome only says which one asked.
        Assert.Contains(
            "RaiseEvent(new RoutedEventArgs(FloatToggleRequestedEvent, this));",
            chrome,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsFloating = FloatingPanelLayer.For(this) is not null;",
            chrome,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FloatDockable", chrome, StringComparison.Ordinal);

        // Nothing floats on a gesture nobody can see.
        Assert.DoesNotContain("DoubleTapped", handleSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "TerminalRuntimePanelViewModel",
        "TerminalRuntimePanelView")]
    [InlineData(
        "BrowserRuntimePanelViewModel",
        "BrowserRuntimePanelView")]
    [InlineData(
        "FileRuntimePanelViewModel",
        "FileRuntimePanelView")]
    [InlineData(
        "StatisticsRuntimePanelViewModel",
        "StatisticsRuntimePanelView")]
    [InlineData(
        "ProcessMonitorRuntimePanelViewModel",
        "ProcessMonitorRuntimePanelView")]
    [InlineData(
        "DatabaseRuntimePanelViewModel",
        "DatabaseRuntimePanelView")]
    [InlineData(
        "RedisRuntimePanelViewModel",
        "RedisRuntimePanelView")]
    [InlineData(
        "UnavailableRuntimePanelViewModel",
        "UnavailableRuntimePanelView")]
    public void Main_window_templates_delegate_runtime_interaction_to_named_panel_views(
        string viewModel,
        string panelView)
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "DataTemplate"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "DataType"),
                    $"vm:{viewModel}",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());

        Assert.Equal(panelView, component.Name.LocalName);
        Assert.Equal(
            "OnRuntimePanelGotFocus",
            AttributeValue(component, "GotFocus"));
        Assert.Equal(
            "OnRuntimePanelPointerPressed",
            AttributeValue(component, "PointerPressed"));
        Assert.Equal(
            "OnCloseRuntimePanelClick",
            AttributeValue(component, "CloseRequested"));
        Assert.DoesNotContain(
            template.Descendants(),
            element => string.Equals(element.Name.LocalName, "SurfaceCard", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(
        "TerminalRuntimePanelView",
        null,
        "Close panel")]
    [InlineData(
        "BrowserRuntimePanelView",
        null,
        "Close browser panel")]
    [InlineData(
        "FileRuntimePanelView",
        null,
        "Close File Viewer panel")]
    [InlineData(
        "StatisticsRuntimePanelView",
        "Statistics panel",
        "Close Statistics panel")]
    [InlineData(
        "ProcessMonitorRuntimePanelView",
        "Process monitor panel",
        "Close Process Monitor panel")]
    [InlineData(
        "DatabaseRuntimePanelView",
        "Database panel",
        "Close database panel")]
    [InlineData(
        "RedisRuntimePanelView",
        "Redis database panel",
        "Close Redis panel")]
    [InlineData(
        "UnavailableRuntimePanelView",
        null,
        "Close panel")]
    public void Runtime_panel_views_preserve_focus_layout_and_close_contracts(
        string panelView,
        string? accessibleName,
        string closeName)
    {
        var document = LoadRuntimePanelView(panelView);
        var root = Assert.IsType<XElement>(document.Root);

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.True(HasClass(root, "RuntimePanelFocusTarget"));
        Assert.Equal("True", AttributeValue(root, "Focusable"));
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));
        Assert.Equal(
            "{Binding IsVisibleInLayout}",
            AttributeValue(root, "IsVisible"));
        Assert.Equal(
            accessibleName,
            AttributeValue(root, "AutomationProperties.Name"));

        // A panel's chrome is the shared component, which draws the card on the
        // panel surface — the same one the workspaces sidebar uses. What closing
        // this particular panel is called is the one part of it the panel says.
        var chrome = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "PanelChrome", StringComparison.Ordinal));
        Assert.Equal(closeName, AttributeValue(chrome, "CloseLabel") ?? "Close panel");
        Assert.Equal("OnCloseClick", AttributeValue(chrome, "CloseRequested"));

        var codeBehind = File.ReadAllText(RuntimePanelPath(panelView, ".axaml.cs"));
        Assert.Contains(
            "public event EventHandler<RoutedEventArgs>? CloseRequested;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "CloseRequested?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        if (string.Equals(
                panelView,
                "FileRuntimePanelView",
                StringComparison.Ordinal))
        {
            // The file view creates and owns its transient HTML renderer visual,
            // so detaching the visual must release that renderer lifetime.
            Assert.Contains("ReleaseHtmlPreview();", codeBehind, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("Dispose(", codeBehind, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Redis_panel_uses_the_database_toolbar_and_flat_workspace_geometry()
    {
        var root = Assert.IsType<XElement>(LoadRuntimePanelView("RedisRuntimePanelView").Root);
        var connect = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "AutomationProperties.Name"), "Connect to Redis", StringComparison.Ordinal));
        var disconnect = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "AutomationProperties.Name"), "Disconnect from Redis", StringComparison.Ordinal));

        foreach (var button in new[] { connect, disconnect })
        {
            Assert.Equal("26", AttributeValue(button, "Height"));
            Assert.Equal("{controls:Inset Horizontal=Md}", AttributeValue(button, "Padding"));
            Assert.Equal("Center", AttributeValue(button, "VerticalAlignment"));
            Assert.Equal("Center", AttributeValue(button, "VerticalContentAlignment"));
            Assert.True(HasClass(button, "SecondaryButton"));
        }

        var error = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Callout"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Text"), "{Binding ErrorMessage}", StringComparison.Ordinal));
        Assert.Equal("Danger", AttributeValue(error, "Tone"));

        var browser = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible"), "{Binding ShowBrowser}", StringComparison.Ordinal));
        Assert.Equal("320,5,*", AttributeValue(browser, "ColumnDefinitions"));
        Assert.Contains(
            browser.Elements(),
            element => string.Equals(element.Name.LocalName, "GridSplitter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "Resize the Redis keys list", StringComparison.Ordinal));
        Assert.DoesNotContain(
            browser.Elements(),
            element => string.Equals(element.Name.LocalName, "SurfaceCard"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Tone"), "Panel", StringComparison.Ordinal));
    }

    /// <summary>
    /// Redis is a database panel, so it states what it is showing and who it is
    /// talking to in the same footer band the database workspace carries, and
    /// its value tables are the shell's one table rather than stock grids.
    /// </summary>
    [Fact]
    public void Redis_panel_shares_the_database_footer_band_and_table()
    {
        var root = Assert.IsType<XElement>(LoadRuntimePanelView("RedisRuntimePanelView").Root);

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "PanelChrome.Footer", StringComparison.Ordinal));

        var grids = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "DataGrid", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(grids);
        foreach (var grid in grids)
        {
            Assert.True(
                HasClass(grid, "DatabaseGrid"),
                "Every Redis value table must wear the shared DatabaseGrid chrome.");
        }

        // A row of a list is the kit's row. Hand-rolled grids are how two
        // lists in the same product stopped agreeing on their own geometry.
        foreach (var list in root.Descendants().Where(element => string.Equals(element.Name.LocalName, "ListBox", StringComparison.Ordinal)))
        {
            Assert.True(
                HasClass(list, "Rows"),
                "A Redis list is a list of rows and takes its chrome from the theme.");
            var template = Assert.Single(
                list.Descendants(),
                element => string.Equals(element.Name.LocalName, "DataTemplate", StringComparison.Ordinal));
            Assert.Equal("ListItem", Assert.Single(template.Elements()).Name.LocalName);
        }

        // The create sheet is a form, so it names every input with the kit's
        // field: a placeholder leaves the moment it is used. The two other
        // shapes are deliberately not held to this — a repeated row is named
        // once by the group above it, and the panel's dense bars (the value
        // editor's footer, the Pub/Sub column) state their options inline the
        // way the log toolbar does.
        // The sheet is a region of the panel, not a popup: a flyout is a window
        // of its own and can be drawn outside the frame it belongs to.
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Flyout", StringComparison.Ordinal));
        var sheet = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "SurfaceCard"
, StringComparison.Ordinal) && element.Descendants().Any(child => string.Equals(AttributeValue(child, "AutomationProperties.Name"), "Create Redis key", StringComparison.Ordinal)));

        foreach (var input in sheet
                     .Descendants()
                     .Where(element => element.Name.LocalName is "TextBox" or "ComboBox")
                     .Where(element => AttributeValue(element, "PlaceholderText") is null))
        {
            Assert.True(
                input.Ancestors().Any(parent => string.Equals(parent.Name.LocalName, "LabeledField", StringComparison.Ordinal)),
                $"A {input.Name.LocalName} in the create-key form carries no field label.");
        }

        // Both forms describe themselves from the selected type rather than
        // hard-coding one type's words.
        foreach (var binding in new[]
                 {
                     "{Binding NewKeyForm.ValueLabel}",
                     "{Binding MutationForm.ValueLabel}",
                     "{Binding MutationForm.ActionLabel}",
                 })
        {
            Assert.Contains(
                root.Descendants(),
                element => element.Attributes().Any(attribute => string.Equals(attribute.Value, binding, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Shell_focuses_the_single_runtime_panel_focus_target()
    {
        var focusNavigator = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "ShellFocusNavigator.cs"));

        Assert.Contains(
            "control.Classes.Contains(\"RuntimePanelFocusTarget\")",
            focusNavigator,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "control.Classes.Contains(\"PanelCard\")",
            focusNavigator,
            StringComparison.Ordinal);

        var mainWindow = LoadView("MainWindow");
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => HasClass(element, "RuntimePanelFocusTarget"));
    }

    [Fact]
    public void Empty_panel_reuses_the_new_item_catalog()
    {
        var document = LoadRuntimePanelView("PanelPlaceholderView");
        var root = Assert.IsType<XElement>(document.Root);

        var chooser = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "LauncherView", StringComparison.Ordinal));
        // The cell has its own close button in its header, so the catalog does
        // not offer a second one.
        Assert.Equal("False", AttributeValue(chooser, "ShowCloseAction"));
        Assert.Equal(
            "{Binding $parent[Window].((vm:MainWindowViewModel)DataContext)}",
            AttributeValue(chooser, "DataContext"));
        Assert.Equal(
            "OnConnectionLaunchRequested",
            AttributeValue(chooser, "ConnectionLaunchRequested"));
        Assert.Equal(
            "OnOpenScreenClick",
            AttributeValue(chooser, "OpenScreenRequested"));
        Assert.Equal(
            "OnChooseTerminalClick",
            AttributeValue(chooser, "NewLocalTerminalRequested"));
        Assert.Equal(
            "OnChooseBrowserClick",
            AttributeValue(chooser, "NewBrowserRequested"));
        Assert.Equal(
            "OnChooseFileViewerClick",
            AttributeValue(chooser, "NewFileViewerRequested"));
        Assert.Equal(
            "OnChooseStatisticsClick",
            AttributeValue(chooser, "NewStatisticsRequested"));
        Assert.Equal(
            "OnChooseProcessMonitorClick",
            AttributeValue(chooser, "NewProcessMonitorRequested"));

        Assert.DoesNotContain(
            root.Descendants(),
            element => HasClass(element, "InlinePanelChooser"));
    }

    [Fact]
    public void Terminal_panel_view_preserves_managed_host_and_typed_shell_interactions()
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "DataTemplate"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "DataType"),
                    "vm:TerminalRuntimePanelViewModel",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());

        Assert.Equal(
            "OnCancelConnectionReconnectClick",
            AttributeValue(component, "CancelReconnectRequested"));
        Assert.Equal(
            "OnTerminalConnectionSelected",
            AttributeValue(component, "ConnectionSelected"));
        Assert.Equal(
            "OnTerminalNewConnectionRequested",
            AttributeValue(component, "NewConnectionRequested"));
        Assert.Equal(
            "OnRetryConnectionPanelClick",
            AttributeValue(component, "RetryConnectionRequested"));
        Assert.Equal(
            "OnTerminalSessionInitializationFailed",
            AttributeValue(component, "SessionInitializationFailed"));
        Assert.Equal(
            "OnTerminalSessionSnapshotChanged",
            AttributeValue(component, "SessionSnapshotChanged"));
        Assert.Equal(
            "OnTrustConnectionHostKeyClick",
            AttributeValue(component, "TrustHostKeyRequested"));

        var document = LoadRuntimePanelView("TerminalRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        var connectionSelector = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "PanelConnectionSelectorView", StringComparison.Ordinal));
        Assert.Equal(
            "{CompiledBinding $parent[Window].((vm:IPanelConnectionOptionsHost)DataContext).PanelConnectionOptions}",
            AttributeValue(connectionSelector, "Options"));
        Assert.Equal(
            "{Binding ConnectionDisplayName}",
            AttributeValue(connectionSelector, "SelectedLabel"));
        Assert.Equal(
            "OnConnectionSelected",
            AttributeValue(connectionSelector, "ConnectionSelected"));
        Assert.Equal(
            "OnNewConnectionRequested",
            AttributeValue(connectionSelector, "NewConnectionRequested"));
        Assert.Equal(
            "Terminal",
            AttributeValue(
                Assert.Single(
                    root.Elements(),
                    element => string.Equals(element.Name.LocalName, "PanelChrome", StringComparison.Ordinal)),
                "Title"));
        var terminal = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TerminalPresentationHost", StringComparison.Ordinal));

        Assert.Equal("RuntimeTerminal", AttributeValue(terminal, "Name"));
        Assert.Equal("{DynamicResource ShellHairline}", AttributeValue(terminal, "Margin"));
        Assert.Equal("{CompiledBinding ClientId}", AttributeValue(terminal, "ClientId"));
        Assert.Equal(
            "{CompiledBinding SessionClient}",
            AttributeValue(terminal, "SessionClient"));
        Assert.Equal(
            "{CompiledBinding SessionRequest}",
            AttributeValue(terminal, "SessionRequest"));
        Assert.Equal(
            "OnSessionSnapshotChanged",
            AttributeValue(terminal, "SessionSnapshotChanged"));
        Assert.Equal(
            "OnSessionInitializationFailed",
            AttributeValue(terminal, "SessionInitializationFailed"));
        Assert.DoesNotContain(
            root.DescendantsAndSelf(),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName.Contains("Transform", StringComparison.Ordinal)));

        var status = FindUniqueAccessibleElement(root, "Terminal session status");
        Assert.Equal(
            "Polite",
            AttributeValue(status, "AutomationProperties.LiveSetting"));

        var codeBehind = File.ReadAllText(
            RuntimePanelPath("TerminalRuntimePanelView", ".axaml.cs"));
        Assert.Contains(
            "SessionInitializationFailed?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "SessionSnapshotChanged?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_connection_selector_filters_saved_connections_and_reports_intent()
    {
        var path = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "Components",
            "PanelConnectionSelectorView.axaml");
        var root = Assert.IsType<XElement>(XDocument.Load(path).Root);
        var filter = FindUniqueAccessibleElement(root, "Filter saved connections");
        Assert.Equal("OnFilterTextChanged", AttributeValue(filter, "TextChanged"));
        Assert.Equal("Filter connections", AttributeValue(filter, "PlaceholderText"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ItemsControl"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding FilteredOptions, ElementName=Root}",
                    StringComparison.Ordinal));
        var create = FindUniqueAccessibleElement(
            root,
            "Create and connect a new connection");
        Assert.Equal("OnNewConnectionClick", AttributeValue(create, "Click"));

        var codeBehind = File.ReadAllText($"{path}.cs");
        Assert.Contains("connection.Name.Contains", codeBehind, StringComparison.Ordinal);
        Assert.Contains("connection.Kind.Contains", codeBehind, StringComparison.Ordinal);
        Assert.Contains("connection.Detail.Contains", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ISessionHostClient", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_panel_view_preserves_native_host_and_typed_shell_interactions()
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "DataTemplate"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "DataType"),
                    "vm:BrowserRuntimePanelViewModel",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());

        Assert.Equal(
            "OnBrowserAddressKeyDown",
            AttributeValue(component, "AddressKeyDown"));
        Assert.Equal(
            "OnBrowserBackClick",
            AttributeValue(component, "BackRequested"));
        Assert.Equal(
            "OnBrowserStateChanged",
            AttributeValue(component, "BrowserStateChanged"));
        Assert.Equal(
            "OnBrowserDeveloperToolsClick",
            AttributeValue(component, "DeveloperToolsRequested"));
        Assert.Equal(
            "OnBrowserForwardClick",
            AttributeValue(component, "ForwardRequested"));
        Assert.Equal(
            "OnBrowserReloadClick",
            AttributeValue(component, "ReloadRequested"));
        Assert.Equal(
            "OnBrowserStopClick",
            AttributeValue(component, "StopRequested"));

        var document = LoadRuntimePanelView("BrowserRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        var browser = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "BrowserPresentationHost", StringComparison.Ordinal));

        Assert.Equal("RuntimeBrowser", AttributeValue(browser, "Name"));
        Assert.Equal("{DynamicResource ShellHairline}", AttributeValue(browser, "Margin"));
        Assert.Equal("{Binding ClientId}", AttributeValue(browser, "ClientId"));
        Assert.Equal(
            "{Binding RendererView}",
            AttributeValue(browser, "RendererView"));
        Assert.Equal(
            "{Binding SessionClient}",
            AttributeValue(browser, "SessionClient"));
        Assert.Equal(
            "{Binding SessionRequest}",
            AttributeValue(browser, "SessionRequest"));
        Assert.Equal(
            "OnBrowserStateChanged",
            AttributeValue(browser, "BrowserStateChanged"));
        var developerTools = FindUniqueAccessibleElement(
            root,
            "Open browser developer tools");
        Assert.Equal(
            "OnDeveloperToolsClick",
            AttributeValue(developerTools, "Click"));
        Assert.Equal(
            "{Binding IsLive, ElementName=RuntimeBrowser}",
            AttributeValue(developerTools, "IsEnabled"));
        var findInPage = FindUniqueAccessibleElement(root, "Find in page");
        Assert.Equal("OnFindClick", AttributeValue(findInPage, "Click"));
        Assert.Equal(
            "{Binding IsLive, ElementName=RuntimeBrowser}",
            AttributeValue(findInPage, "IsEnabled"));
        var findText = FindUniqueAccessibleElement(root, "Find text in page");
        Assert.Equal("OnFindBoxKeyDown", AttributeValue(findText, "KeyDown"));
        Assert.Equal("OnFindTextChanged", AttributeValue(findText, "TextChanged"));
        var eventNotice = FindUniqueAccessibleElement(root, "Browser event notice");
        Assert.Equal(
            "Polite",
            AttributeValue(eventNotice, "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "{Binding IsProductNoticeVisible, ElementName=RuntimeBrowser}",
            AttributeValue(eventNotice, "IsVisible"));
        var dismissNotice = FindUniqueAccessibleElement(
            root,
            "Dismiss browser event notice");
        Assert.Equal(
            "OnDismissProductNoticeClick",
            AttributeValue(dismissNotice, "Click"));
        Assert.DoesNotContain(
            root.DescendantsAndSelf(),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName.Contains("Transform", StringComparison.Ordinal)));

        var address = FindUniqueAccessibleElement(root, "Browser address");
        Assert.Equal("OnAddressKeyDown", AttributeValue(address, "KeyDown"));
        // Bound by element name, not by data context. The row's data context is the
        // panel, because the shell resolves which panel to act on from the sender's
        // context — re-pointing the row at the browser host broke Close silently.
        Assert.Equal(
            "{Binding AddressText, ElementName=RuntimeBrowser, Mode=TwoWay}",
            AttributeValue(address, "Text"));
        Assert.Equal("about:blank", AttributeValue(address, "PlaceholderText"));
        Assert.Equal(
            "OnAddressGotFocus",
            AttributeValue(address, "GotFocus"));
        Assert.Equal(
            "OnAddressLostFocus",
            AttributeValue(address, "LostFocus"));

        var status = FindUniqueAccessibleElement(root, "Browser session status");
        Assert.Equal(
            "Polite",
            AttributeValue(status, "AutomationProperties.LiveSetting"));

        var codeBehind = File.ReadAllText(
            RuntimePanelPath("BrowserRuntimePanelView", ".axaml.cs"));
        // Raised with the presentation host, not the sender. The shell resolves
        // browser actions from it, and resolves Close from the panel's data
        // context — one context cannot answer both, so the view names the host.
        Assert.Contains(
            "AddressKeyDown?.Invoke(RuntimeBrowser, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "addressBox.PlaceholderText = null;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "addressBox.PlaceholderText = BlankAddressPlaceholder;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserStateChanged?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Tunnel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsMacOS()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BrowserFindDirection.Previous", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void File_panel_view_preserves_content_states_and_typed_shell_interactions()
    {
        var mainWindow = LoadView("MainWindow");
        var template = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "DataTemplate"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "DataType"),
                    "vm:FileRuntimePanelViewModel",
                    StringComparison.Ordinal));
        var component = Assert.Single(template.Elements());
        var shellInteractions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The file actions arrive as one event that names which was asked
            // for, rather than one event each. They are a list the panel builds
            // from what the connection can do, and a list cannot be turned into
            // a fixed set of events without going back to hand-written markup
            // per action — which is what left the toolbar and its overflow menu
            // disagreeing about what was possible.
            ["ActionRequested"] = "OnFileActionRequested",
            ["DismissOperationIssueRequested"] = "OnDismissFileOperationIssueClick",
            ["EntryDoubleTapped"] = "OnFileEntryDoubleTapped",
            ["EntrySelectionChanged"] = "OnFileEntrySelectionChanged",
            ["EntryTransferDropRequested"] = "OnFileEntryTransferDropRequested",
            ["EntryTransferKeyRequested"] = "OnFileEntryTransferKeyRequested",
            ["LocationKeyDown"] = "OnFileLocationKeyDown",
            ["NavigateUpRequested"] = "OnFileNavigateUpClick",
            ["RefreshRequested"] = "OnFileRefreshClick",
        };

        foreach (var (interaction, handler) in shellInteractions)
        {
            Assert.Equal(handler, AttributeValue(component, interaction));
        }

        var document = LoadRuntimePanelView("FileRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "RowDefinitions"),
                    "Auto,*",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Grid.Row"),
                    "0",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Padding"),
                    "{controls:Inset Left=Sm, Top=Xs, Right=Sm, Bottom=Sm}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Background"),
                    "{DynamicResource ShellSurfaceBrush}",
                    StringComparison.Ordinal));
        var connectionSelector = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "PanelConnectionSelectorView", StringComparison.Ordinal));
        Assert.Equal(
            "{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).FileConnectionOptions}",
            AttributeValue(connectionSelector, "Options"));
        Assert.Equal(
            "{Binding ConnectionDisplayName}",
            AttributeValue(connectionSelector, "SelectedLabel"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ComboBox"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "File provider profile",
                    StringComparison.Ordinal));
        var sortHeaders = new Dictionary<string, (string Handler, string StateBinding)>(
            StringComparer.Ordinal)
        {
            ["Sort files by name"] = ("OnSortNameClick", "{Binding IsSortingByName}"),
            ["Sort files by size"] = ("OnSortSizeClick", "{Binding IsSortingBySize}"),
            ["Sort files by modified date"] = (
                "OnSortModifiedClick",
                "{Binding IsSortingByModified}"),
        };
        foreach (var (accessibleName, sortHeader) in sortHeaders)
        {
            var button = Assert.Single(
                root.Descendants(),
                element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                        AttributeValue(element, "AutomationProperties.Name"),
                        accessibleName,
                        StringComparison.Ordinal));
            Assert.Equal(sortHeader.Handler, AttributeValue(button, "Click"));
            Assert.True(HasClass(button, "FileColumnHeader"));
            Assert.Equal(
                sortHeader.StateBinding,
                AttributeValue(button, "Classes.sorted"));
            Assert.Equal(
                "{Binding IsSortDescending}",
                AttributeValue(button, "Classes.descending"));
            Assert.Contains(
                button.Descendants(),
                element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), "ArrowUp", StringComparison.Ordinal));
            Assert.Contains(
                button.Descendants(),
                element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), "ArrowDown", StringComparison.Ordinal));
        }
        var headerStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector"), "Button.FileColumnHeader", StringComparison.Ordinal));
        Assert.Contains(
            headerStyle.Elements(),
            setter => string.Equals(AttributeValue(setter, "Property"), "HorizontalAlignment"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "Stretch", StringComparison.Ordinal));
        Assert.Contains(
            headerStyle.Elements(),
            setter => string.Equals(AttributeValue(setter, "Property"), "CornerRadius"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "0", StringComparison.Ordinal));
        var headerHoverStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "Button.FileColumnHeader:pointerover /template/ ContentPresenter", StringComparison.Ordinal));
        Assert.Contains(
            headerHoverStyle.Elements(),
            setter => string.Equals(AttributeValue(setter, "Property"), "Background"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value")
, "{DynamicResource ShellSurfaceHoverBrush}", StringComparison.Ordinal));
        Assert.Equal(
            3,
            root.Descendants().Count(element => string.Equals(element.Name.LocalName, "ListBox"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding Entries}",
                    StringComparison.Ordinal)));
        var fileNameLabels = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding Name}",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, fileNameLabels.Length);
        Assert.All(fileNameLabels, label =>
        {
            Assert.Equal("CharacterEllipsis", AttributeValue(label, "TextTrimming"));
            Assert.Equal("{Binding Name}", AttributeValue(label, "ToolTip.Tip"));
        });
        Assert.Contains(
            fileNameLabels,
            label => string.Equals(label.Parent?.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(label, "Grid.Column"),
                    "1",
                    StringComparison.Ordinal));
        var viewOptionsButton = FindUniqueAccessibleElement(
            root,
            "Open file sort and view options");
        var viewOptionsFlyout = Assert.Single(
            viewOptionsButton.Descendants(),
            element => string.Equals(element.Name.LocalName, "Flyout", StringComparison.Ordinal));
        Assert.Equal(
            3,
            viewOptionsFlyout.Descendants().Count(element => string.Equals(element.Name.LocalName, "ComboBox", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            root.Descendants().Where(element => string.Equals(element.Name.LocalName, "ComboBox", StringComparison.Ordinal)),
            element => !element.Ancestors().Contains(viewOptionsFlyout));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding KindLabel}",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "FileToolbarGroup"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "FileToolbarAction"));
        var previewToggle = FindUniqueAccessibleElement(root, "Toggle file preview");
        Assert.Equal("OnTogglePreviewClick", AttributeValue(previewToggle, "Click"));
        Assert.True(HasClass(previewToggle, "InsetToggle"));
        Assert.Equal(
            "{Binding IsPreviewVisible}",
            AttributeValue(previewToggle, "Classes.active"));
        var previewSplitter = FindUniqueAccessibleElement(root, "Resize file preview");
        Assert.Equal(
            "{Binding IsPreviewVisible}",
            AttributeValue(previewSplitter, "IsVisible"));
        Assert.Equal("Columns", AttributeValue(previewSplitter, "ResizeDirection"));
        Assert.Equal(
            "PreviousAndNext",
            AttributeValue(previewSplitter, "ResizeBehavior"));
        foreach (var columnSplitterName in new[]
                 {
                     "Resize file name and size columns",
                     "Resize file size and modified columns",
                 })
        {
            var columnSplitter = FindUniqueAccessibleElement(root, columnSplitterName);
            Assert.Equal("Columns", AttributeValue(columnSplitter, "ResizeDirection"));
            Assert.Equal(
                "PreviousAndNext",
                AttributeValue(columnSplitter, "ResizeBehavior"));
        }

        // No action is written out by hand. The strip and the overflow menu are
        // the same list of actions shown twice, so an action a connection
        // cannot perform leaves both at once and neither can be left behind.
        var actionStrip = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ItemsControl"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding ToolbarActions}",
                    StringComparison.Ordinal));
        Assert.Contains(
            actionStrip.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Command"),
                    "{Binding Command}",
                    StringComparison.Ordinal));

        // One right-click menu over all three layouts and over the space below
        // the last row, filled when it opens from where the press landed.
        // Three menus in the markup would be three places to add the next
        // action to, which is the shape this replaced.
        var contextMenu = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ContextMenu", StringComparison.Ordinal));
        Assert.Equal("OnFileContextMenuOpening", AttributeValue(contextMenu, "Opening"));
        foreach (var menu in new[] { contextMenu, OverflowMenu(root) })
        {
            Assert.Contains(
                menu.Descendants(),
                element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(
                        AttributeValue(element, "Property"),
                        "Command",
                        StringComparison.Ordinal));
            Assert.Contains(
                menu.Descendants(),
                element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(
                        AttributeValue(element, "Property"),
                        "IsEnabled",
                        StringComparison.Ordinal));
        }

        // And the toolbar answers to the panel's own width, not the window's:
        // two of these can share one window at different widths.
        var containerQuery = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ContainerQuery", StringComparison.Ordinal));
        Assert.Equal("filePanel", AttributeValue(containerQuery, "Name"));
        Assert.Equal("Width", AttributeValue(root, "Container.Sizing"));

        var itemCount = FindUniqueAccessibleElement(root, "File Viewer item count");
        Assert.Equal("{Binding Status}", AttributeValue(itemCount, "Value"));
        Assert.Equal("{Binding ShortStatus}", AttributeValue(itemCount, "CompactValue"));
        Assert.Equal(
            "{Binding HasListingSummary}",
            AttributeValue(itemCount, "IsVisible"));
        Assert.Equal(
            1,
            root.Descendants().Count(element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding LocationText}",
                StringComparison.Ordinal)));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Load more files",
                StringComparison.Ordinal));
        Assert.True(
            root.Descendants().Count(element => string.Equals(element.Name.LocalName, "VirtualizingStackPanel", StringComparison.Ordinal)) >= 2,
            "The complete details and list views must virtualize their rows.");
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Text"),
                    "BOUNDED PREVIEW",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "BorderThickness"),
                "0,1,0,1",
                StringComparison.Ordinal));
        var transferDropTarget = FindUniqueAccessibleElement(
            root,
            "File transfer drop target");
        Assert.Equal("Rectangle", transferDropTarget.Name.LocalName);
        Assert.Equal("False", AttributeValue(transferDropTarget, "IsVisible"));
        Assert.Equal("4,3", AttributeValue(transferDropTarget, "StrokeDashArray"));
        Assert.Equal(
            "{DynamicResource ShellCardRadius}",
            AttributeValue(transferDropTarget, "RadiusX"));
        Assert.Equal(
            "{DynamicResource ShellCardRadius}",
            AttributeValue(transferDropTarget, "RadiusY"));
        var folderDropStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Selector"),
                    "ListBoxItem.transferDropTarget /template/ ContentPresenter",
                    StringComparison.Ordinal));
        Assert.Contains(
            folderDropStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Property"),
                    "Background",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            folderDropStyle.Elements(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && AttributeValue(element, "Property") is
                    "BorderBrush" or "BorderThickness");

        var application = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml"));
        var scalarCardRadius = Assert.Single(
            application.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Key"),
                "ShellCardRadius",
                StringComparison.Ordinal));
        Assert.Equal("Double", scalarCardRadius.Name.LocalName);

        Assert.Equal(
            "Polite",
            AttributeValue(
                FindUniqueAccessibleElement(root, "File Viewer loading"),
                "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "Assertive",
            AttributeValue(
                FindUniqueAccessibleElement(root, "File Viewer operation status"),
                "AutomationProperties.LiveSetting"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding ShowErrorState}",
                StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding ShowEmptyLocationState}",
                StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding ShowSearchNoResultsState}",
                StringComparison.Ordinal));

        var codeBehind = File.ReadAllText(
            RuntimePanelPath("FileRuntimePanelView", ".axaml.cs"));
        foreach (var interaction in shellInteractions.Keys.Except(
                     [
                         "ActionRequested",
                         "EntryTransferDropRequested",
                         "EntryTransferKeyRequested",
                     ],
                     StringComparer.Ordinal))
        {
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        // The action carries which action it is, so it is raised with an
        // argument the view builds rather than one it was handed.
        Assert.Contains(
            "ActionRequested?.Invoke(this, new FilePanelActionEventArgs(action));",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "EntryTransferKeyRequested?.Invoke(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "EntryTransferDropRequested?.Invoke(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void OnFilePointerMoved",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ActiveFileDrag(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.ShowDragGhost(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.True(
            codeBehind.IndexOf(
                "candidate.Pointer.Capture(this);",
                StringComparison.Ordinal)
            < codeBehind.IndexOf(
                "_activeFileDrag = activeDrag;",
                StringComparison.Ordinal));
        Assert.Contains(
            "ResolveInternalFileDropTarget(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DragDrop.DoDragDropAsync(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveFileDropTarget(e.Source, e.DataTransfer)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "DestinationFolder",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private async ", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "protected override void OnDetachedFromVisualTree(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("ReleaseHtmlPreview();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("preview.Dispose();", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "StatisticsRuntimePanelView",
        "Statistics state",
        "Refresh statistics",
        "Statistics loading",
        "Statistics unavailable")]
    [InlineData(
        "ProcessMonitorRuntimePanelView",
        "Process monitor state",
        "Refresh processes",
        "Process monitor loading",
        "Process monitor unavailable")]
    public void Monitoring_panel_views_preserve_commands_and_live_state_announcements(
        string panelView,
        string statusName,
        string refreshName,
        string loadingName,
        string unavailableName)
    {
        var document = LoadRuntimePanelView(panelView);
        var root = Assert.IsType<XElement>(document.Root);

        var status = FindUniqueAccessibleElement(root, statusName);
        Assert.Equal("Polite", AttributeValue(status, "AutomationProperties.LiveSetting"));

        var refresh = FindUniqueAccessibleElement(root, refreshName);
        Assert.Equal("Button", refresh.Name.LocalName);
        Assert.Equal(
            "{Binding RefreshCommand}",
            AttributeValue(refresh, "Command"));

        var loading = FindUniqueAccessibleElement(root, loadingName);
        Assert.Equal("Polite", AttributeValue(loading, "AutomationProperties.LiveSetting"));

        var unavailable = FindUniqueAccessibleElement(root, unavailableName);
        Assert.Equal(
            "Assertive",
            AttributeValue(unavailable, "AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void Process_monitor_sort_is_owned_by_clickable_column_headers()
    {
        var document = LoadRuntimePanelView("ProcessMonitorRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        var sortHeaders = new Dictionary<
            string,
            (string Handler, string StateBinding, string Padding, string? Width)>(StringComparer.Ordinal)
        {
            ["Sort processes by PID"] = (
                "OnSortProcessIdClick",
                "{Binding IsSortingByProcessId}",
                "0",
                "70"),
            ["Sort processes by name"] = (
                "OnSortNameClick",
                "{Binding IsSortingByName}",
                "{controls:Inset Horizontal=Sm}",
                null),
            ["Sort processes by CPU"] = (
                "OnSortCpuClick",
                "{Binding IsSortingByCpu}",
                "{controls:Inset Horizontal=Sm}",
                "90"),
            ["Sort processes by memory"] = (
                "OnSortMemoryClick",
                "{Binding IsSortingByMemory}",
                "{controls:Inset Horizontal=Sm}",
                "110"),
        };

        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Process sort order",
                StringComparison.Ordinal));
        foreach (var (accessibleName, sortHeader) in sortHeaders)
        {
            var button = FindUniqueAccessibleElement(root, accessibleName);
            Assert.Equal("Button", button.Name.LocalName);
            Assert.Equal(sortHeader.Handler, AttributeValue(button, "Click"));
            Assert.True(HasClass(button, "ProcessColumnHeader"));
            Assert.Equal(
                sortHeader.StateBinding,
                AttributeValue(button, "Classes.sorted"));
            Assert.Equal(
                "{Binding IsSortDescending}",
                AttributeValue(button, "Classes.descending"));
            Assert.Equal(
                sortHeader.Padding,
                AttributeValue(button, "Padding"));
            Assert.Equal(sortHeader.Width, AttributeValue(button, "Width"));
            Assert.Null(AttributeValue(button, "Margin"));
            Assert.Contains(
                button.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Symbol"),
                    "ArrowUp",
                    StringComparison.Ordinal));
            Assert.Contains(
                button.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Symbol"),
                    "ArrowDown",
                    StringComparison.Ordinal));
        }

        var startedHeader = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock", StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Text"),
                    "Started",
                    StringComparison.Ordinal));
        Assert.True(HasClass(startedHeader, "StartedCell"));

        var headerStyle = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style", StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.ProcessColumnHeader",
                    StringComparison.Ordinal));
        Assert.Contains(
            headerStyle.Elements(),
            setter => string.Equals(
                    AttributeValue(setter, "Property"),
                    "HorizontalAlignment",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(setter, "Value"),
                    "Stretch",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style", StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Selector"),
                    "Button.ProcessColumnHeader:pointerover /template/ ContentPresenter",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Statistics_panel_presents_host_metrics_with_bounded_history_charts()
    {
        var document = LoadRuntimePanelView("StatisticsRuntimePanelView");
        var root = Assert.IsType<XElement>(document.Root);
        var charts = root
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "TimeSeriesChart", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, charts.Length);
        Assert.Contains(
            charts,
            chart => string.Equals(AttributeValue(chart, "Values"), "{Binding CpuHistory}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(chart, "Maximum"), "100", StringComparison.Ordinal));
        Assert.Contains(
            charts,
            chart => string.Equals(AttributeValue(chart, "Values"), "{Binding MemoryHistory}", StringComparison.Ordinal));
        Assert.Contains(
            charts,
            chart => string.Equals(AttributeValue(chart, "Values"), "{Binding NetworkReceivedHistory}", StringComparison.Ordinal));
        Assert.Contains(
            charts,
            chart => string.Equals(AttributeValue(chart, "Values"), "{Binding NetworkSentHistory}", StringComparison.Ordinal));

        var visibleCopy = string.Join(
            " ",
            root.Descendants()
                .Select(element => AttributeValue(element, "Text"))
                .OfType<string>());
        Assert.DoesNotContain("OBSERVED", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASURA CPU", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASURA MEMORY", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CPU usage", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("Process memory", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("Network receive", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("Network send", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("Running processes", visibleCopy, StringComparison.Ordinal);
    }

    private static XElement OverflowMenu(XElement root) => Assert.Single(
        root.Descendants(),
        element => string.Equals(element.Name.LocalName, "MenuFlyout", StringComparison.Ordinal));

    private static XElement FindUniqueAccessibleElement(
        XElement root,
        string accessibleName) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                accessibleName,
                StringComparison.Ordinal));

    private static XDocument DesignSystem() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "DesignSystem.axaml"));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadRuntimePanelView(string panelView) =>
        XDocument.Load(RuntimePanelPath(panelView, ".axaml"));

    private static string RuntimePanelPath(string panelView, string extension) =>
        Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "RuntimePanels",
            $"{panelView}{extension}");

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;

    private static bool HasClass(XElement element, string className) =>
        (AttributeValue(element, "Classes") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.Ordinal);
}
