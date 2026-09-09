using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class TransientOverlayViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string>
        CommandPaletteInteractions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ActivateSearchResultRequested"] = "OnLauncherSearchResultClick",
                ["CloseRequested"] = "OnCloseOverlayClick",
                ["SearchKeyDownRequested"] = "OnCommandSearchKeyDown",
            };

    private static readonly IReadOnlyDictionary<string, string>
        LayoutDesignerInteractions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AddSlotRequested"] = "OnLayoutAddSlotClick",
                ["CloseRequested"] = "OnCloseOverlayClick",
                ["EditLayoutRequested"] = "OnEditLayoutClick",
                ["RemoveSlotRequested"] = "OnLayoutRemoveSlotClick",
                ["ResetRequested"] = "OnResetLayoutClick",
                ["SaveRequested"] = "OnSaveLayoutDesignerClick",
                ["SlotSelectedRequested"] = "OnLayoutSlotClick",
                ["SplitSlotDownRequested"] = "OnLayoutSplitSlotDownClick",
                ["SplitSlotRightRequested"] = "OnLayoutSplitSlotRightClick",
            };

    private static readonly IReadOnlyDictionary<string, string>
        NewPanelChooserInteractions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AddBrowserPanelRequested"] = "OnAddBrowserPanelClick",
                ["AddFilePanelRequested"] = "OnAddFilePanelClick",
                ["AddProcessMonitorPanelRequested"] =
                    "OnAddProcessMonitorPanelClick",
                ["AddStatisticsPanelRequested"] = "OnAddStatisticsPanelClick",
                ["AddTerminalPanelRequested"] = "OnAddTerminalPanelClick",
                ["CloseRequested"] = "OnCloseOverlayClick",
                ["ShowLayoutDesignerRequested"] = "OnShowLayoutDesignerClick",
            };

    [Fact]
    public void Main_window_defers_transient_overlays_behind_named_hosts()
    {
        var mainWindow = LoadView("MainWindow");
        AssertDelegatedOverlay(
            mainWindow,
            "CommandPaletteView",
            "CommandPaletteOverlayTemplate",
            "CommandPaletteOverlayHost",
            "{Binding IsCommandPaletteVisible}",
            CommandPaletteInteractions);
        AssertDelegatedOverlay(
            mainWindow,
            "LayoutDesignerView",
            "LayoutDesignerOverlayTemplate",
            "LayoutDesignerOverlayHost",
            "{Binding IsLayoutDesignerVisible}",
            LayoutDesignerInteractions);
        AssertDelegatedOverlay(
            mainWindow,
            "NewPanelChooserView",
            "NewPanelOverlayTemplate",
            "NewPanelOverlayHost",
            "{Binding IsNewPanelVisible}",
            NewPanelChooserInteractions);
        var definitionTemplate = Assert.Single(
            mainWindow.Descendants(),
            element => AttributeValue(element, "Key") == "DefinitionEditorOverlayTemplate");
        Assert.Contains(
            definitionTemplate.Descendants(),
            element => element.Name.LocalName == "WorkspaceEditorView");
        var definitionHost = Assert.Single(
            mainWindow.Descendants(),
            element => AttributeValue(element, "Name") == "DefinitionEditorOverlayHost");
        Assert.Equal(
            "{Binding IsDefinitionEditorVisible}",
            AttributeValue(definitionHost, "IsVisible"));
        Assert.Empty(definitionHost.Elements());

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                mainWindow.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        var overlayHost = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding HasOverlay}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Background"),
                    "{DynamicResource ShellScrimBrush}",
                    StringComparison.Ordinal));
        var titleBar = Assert.Single(
            overlayHost.Elements(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "OverlayTitleBarDragRegion",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding $parent[views:MainWindow].TitleBarChromeHeight}",
            AttributeValue(titleBar, "Height"));
        Assert.Equal("Top", AttributeValue(titleBar, "VerticalAlignment"));
        Assert.Equal(
            "TitleBar",
            AttributeValue(titleBar, "WindowDecorationProperties.ElementRole"));
        Assert.Null(AttributeValue(titleBar, "PointerPressed"));
    }

    [Fact]
    public void Command_palette_view_preserves_geometry_search_and_accessibility()
    {
        var commandPalette = LoadOverlay("CommandPaletteView");
        var root = Assert.IsType<XElement>(commandPalette.Root);
        AssertStretchingUserControl(root);

        var card = AssertOverlayCard(root);
        Assert.Equal("680", AttributeValue(card, "Width"));
        Assert.Equal("700", AttributeValue(card, "MaxHeight"));

        var search = FindNamedElement(root, "CommandSearchBox");
        Assert.Equal(
            "{Binding LauncherSearchQuery}",
            AttributeValue(search, "Text"));
        Assert.Equal("OnSearchKeyDown", AttributeValue(search, "KeyDown"));
        Assert.Equal(
            "Search commands and launch targets",
            AttributeValue(search, "AutomationProperties.Name"));

        var results = FindNamedElement(root, "LauncherSearchResultList");
        Assert.Equal(
            "{Binding LauncherSearchResults}",
            AttributeValue(results, "ItemsSource"));
        Assert.Equal(
            "{Binding SelectedLauncherSearchResult, Mode=TwoWay}",
            AttributeValue(results, "SelectedItem"));
        Assert.Contains(
            results.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnActivateSearchResultClick",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding IsAvailable}",
                    StringComparison.Ordinal));

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Launcher search has no results",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.LiveSetting"),
                    "Polite",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Layout_designer_preserves_geometry_dock_canvas_and_interactions()
    {
        var designer = LoadOverlay("LayoutDesignerView");
        var root = Assert.IsType<XElement>(designer.Root);
        AssertStretchingUserControl(root);

        var card = AssertOverlayCard(root);
        Assert.Equal("1000", AttributeValue(card, "Width"));
        Assert.Equal("648", AttributeValue(card, "Height"));
        Assert.Equal("Center", AttributeValue(card, "HorizontalAlignment"));
        Assert.Equal("Center", AttributeValue(card, "VerticalAlignment"));

        var layout = Assert.Single(
            card.Elements(),
            element => string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal));
        Assert.Equal(
            "60,Auto,*,60",
            AttributeValue(layout, "RowDefinitions"));
        Assert.Contains(
            layout.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "ColumnDefinitions"),
                    "*,300",
                    StringComparison.Ordinal));

        var nameEditor = FindNamedElement(root, "NewLayoutName");
        Assert.Equal(
            "{Binding LayoutDesignerEditor.Name, Mode=TwoWay}",
            AttributeValue(nameEditor, "Text"));
        Assert.Equal("My layout", AttributeValue(nameEditor, "PlaceholderText"));

        // The canvas is the runtime's own Dock control: the designer must not
        // reintroduce a private layout engine beside the one the workspace uses.
        var canvas = FindNamedElement(root, "LayoutDesignerDock");
        Assert.Equal("DockControl", canvas.Name.LocalName);
        Assert.Equal(
            "{Binding LayoutDesignerEditor.DockLayout}",
            AttributeValue(canvas, "Layout"));
        Assert.Equal(
            "{Binding LayoutDesignerEditor.DockFactory}",
            AttributeValue(canvas, "Factory"));
        Assert.Equal("True", AttributeValue(canvas, "IsDockingEnabled"));
        Assert.Equal("False", AttributeValue(canvas, "InitializeLayout"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "NumericUpDown", StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => element.Name.LocalName is "LayoutDesignerPreviewPanel"
                or "LayoutDesignerGridVisual");

        // Every slot exposes the workspace's own gestures from its own chrome.
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "PanelDockHandle", StringComparison.Ordinal));
        foreach (var itemsSource in new[]
                 {
                     "{Binding LayoutDesignerEditor.Slots}",
                     "{Binding Layouts}",
                 })
        {
            var items = Assert.Single(
                root.Descendants(),
                element => string.Equals(
                        element.Name.LocalName,
                        "ItemsControl",
                        StringComparison.Ordinal)
                    && string.Equals(
                        AttributeValue(element, "ItemsSource"),
                        itemsSource,
                        StringComparison.Ordinal));
            var itemButton = Assert.Single(
                items.Descendants(),
                element => string.Equals(
                    element.Name.LocalName,
                    "Button",
                    StringComparison.Ordinal));
            Assert.Equal(
                "Stretch",
                AttributeValue(itemButton, "HorizontalAlignment"));
            Assert.Equal(
                "Stretch",
                AttributeValue(itemButton, "HorizontalContentAlignment"));
        }
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Content"),
                    "Save layout",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding LayoutDesignerEditor.CanSave}",
                    StringComparison.Ordinal));

        Assert.Equal(9, LayoutDesignerInteractions.Count);
        foreach (var handler in LayoutDesignerInteractions.Values)
        {
            Assert.Contains(
                root.DescendantsAndSelf().SelectMany(element =>
                    element.Attributes()),
                attribute => string.Equals(
                    attribute.Value,
                    handler,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void New_item_catalog_preserves_its_collections_and_creation_inputs()
    {
        // The catalog had an overlay wrapper of its own, with a margin and a
        // maximum size, because asking what to open was a modal. It is a panel
        // now, so it fills whatever cell it is placed in and the only host left
        // is the placed cell itself.
        var placeholder = LoadRuntimePanel("PanelPlaceholderView");
        var hostedLauncher = Assert.Single(
            Assert.IsType<XElement>(placeholder.Root).Descendants(),
            element => string.Equals(element.Name.LocalName, "LauncherView", StringComparison.Ordinal));
        Assert.Equal("False", AttributeValue(hostedLauncher, "ShowCloseAction"));

        var catalog = LoadComponent("LauncherView");
        var catalogRoot = Assert.IsType<XElement>(catalog.Root);

        // The reference frame heads this overlay "Start something new" rather than
        // naming the thing being created; Home's own button still says whether it
        // opens a tab or a session.
        Assert.Contains(
            catalogRoot.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Text"),
                    "Start something new",
                    StringComparison.Ordinal));

        var choices = catalogRoot.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ChooserTile", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(8, choices.Length);

        var initialAction = FindNamedElement(catalogRoot, "NewTerminalButton");
        Assert.Equal(
            "OnNewLocalTerminalClick",
            AttributeValue(initialAction, "Click"));
        Assert.Contains(
            choices,
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Open a new browser",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding CanStartBrowserSession}",
                    StringComparison.Ordinal));

        // The reusable catalog, not the overlay wrapper, owns all collections.
        foreach (var catalogBinding in new[]
                 {
                     "{Binding Workspaces}",
                     // Saved targets, not saved connection cards: one row per
                     // target carrying every adapter it supports, rather than
                     // one row per (target, adapter) pair.
                     "{Binding SavedConnectionShortcuts}",
                     "{Binding Screens}",
                     // What has been open before belongs to the same catalog:
                     // the launcher is the one answer to what to open, and
                     // history was the last thing answering it elsewhere. It is
                     // the searched list, not the raw one — history is bounded
                     // by the retention policy, not by what fits on screen.
                     "{Binding FilteredHistorySessions}",
                 })
        {
            Assert.Contains(
                catalogRoot.Descendants(),
                element => string.Equals(element.Name.LocalName, "ItemsControl"
, StringComparison.Ordinal) && string.Equals(
                        AttributeValue(element, "ItemsSource"),
                        catalogBinding,
                        StringComparison.Ordinal));
        }

        Assert.Equal(
            4,
            catalogRoot.Descendants()
                .Count(element => string.Equals(element.Name.LocalName, "CountPill", StringComparison.Ordinal)));
        // Screens and recent sessions. The saved-connection row is the same
        // list row, but it lives in its own component because it also carries
        // the other adapters its target supports.
        Assert.Equal(
            2,
            catalogRoot.Descendants()
                .Count(element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClasses(element, "ListRow", "LauncherListRow")));

        // No inline create form: the launcher opens things, and creating a
        // screen or workspace belongs to the page that owns that list. The one
        // text box narrows what is already there rather than adding to it.
        var textBox = Assert.Single(
            catalogRoot.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBox", StringComparison.Ordinal));
        Assert.Equal("HistorySearchBox", AttributeValue(textBox, "Name"));
        Assert.Equal("{Binding HistorySearchQuery}", AttributeValue(textBox, "Text"));

        var workspaceList = Assert.Single(
            catalogRoot.Descendants(),
            element => string.Equals(element.Name.LocalName, "ScrollViewer"
, StringComparison.Ordinal) && element.Descendants().Any(item => string.Equals(item.Name.LocalName, "ItemsControl"
, StringComparison.Ordinal) && string.Equals(
                        AttributeValue(item, "ItemsSource"),
                        "{Binding Workspaces}",
                        StringComparison.Ordinal)));
        Assert.Equal(
            "Auto",
            AttributeValue(workspaceList, "HorizontalScrollBarVisibility"));
        Assert.Equal(
            "{Binding HasWorkspaces}",
            AttributeValue(workspaceList, "IsVisible"));
        Assert.Contains(
            catalogRoot.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClasses(element, "SearchButton")
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Search commands, connections, screens, workspaces, and session history",
                    StringComparison.Ordinal));
        var searchButton = Assert.Single(
            catalogRoot.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClasses(element, "SearchButton"));
        var searchRow = Assert.IsType<XElement>(searchButton.Parent);
        Assert.Null(AttributeValue(searchRow, "ColumnSpacing"));
        var closeButton = Assert.Single(
            searchRow.Elements(),
            element => HasClasses(element, "IconButton", "Large"));
        Assert.Equal(
            "{controls:Inset Left=Md}",
            AttributeValue(closeButton, "Margin"));
    }

    [Fact]
    public void New_panel_chooser_preserves_geometry_choices_and_availability()
    {
        var chooser = LoadOverlay("NewPanelChooserView");
        var root = Assert.IsType<XElement>(chooser.Root);
        AssertStretchingUserControl(root);

        var card = AssertOverlayCard(root);
        Assert.Equal("900", AttributeValue(card, "Width"));

        var choices = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ChooserTile", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(8, choices.Length);

        var initialAction = FindNamedElement(root, "NewPanelTerminalButton");
        Assert.Equal(
            "OnAddTerminalPanelClick",
            AttributeValue(initialAction, "Click"));
        Assert.Contains(
            choices,
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Add browser panel",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding CanCreateBrowserPanel}",
                    StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Content"),
                    "Open layout designer instead",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnShowLayoutDesignerClick",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Overlay_views_forward_original_events_and_own_only_namescope_mechanics()
    {
        var commandPaletteCode = ApplicationViews
            .FindUniqueCodeBehindSourceContaining(
                "public sealed partial class CommandPaletteView");
        AssertForwardingContract(
            commandPaletteCode,
            CommandPaletteInteractions.Keys);
        Assert.Contains("internal void FocusSearch()", commandPaletteCode);
        Assert.Contains(
            "internal void ScrollSelectedResultIntoView()",
            commandPaletteCode);
        Assert.Contains(
            "LauncherSearchResultList.ScrollIntoView(selected);",
            commandPaletteCode);

        var layoutDesignerCode = ApplicationViews
            .FindUniqueCodeBehindSourceContaining(
                "public sealed partial class LayoutDesignerView");
        AssertForwardingContract(
            layoutDesignerCode,
            LayoutDesignerInteractions.Keys);
        Assert.Contains(
            "internal void FocusNameEditor()",
            layoutDesignerCode);
        Assert.Contains(
            "NewLayoutName.Focus(NavigationMethod.Tab);",
            layoutDesignerCode);
        Assert.DoesNotContain("RequestCancel()", layoutDesignerCode);
        Assert.DoesNotContain("DismissLayoutDesigner()", layoutDesignerCode);
        Assert.DoesNotContain("SaveLayoutDesignerAsync(", layoutDesignerCode);

        var newItemChooserCode = ApplicationViews
            .FindUniqueCodeBehindSourceContaining(
                "public sealed partial class LauncherView");
        Assert.Contains(
            "NewTerminalButton.Focus(NavigationMethod.Tab);",
            newItemChooserCode);

        var newPanelChooserCode = ApplicationViews
            .FindUniqueCodeBehindSourceContaining(
                "public sealed partial class NewPanelChooserView");
        AssertForwardingContract(
            newPanelChooserCode,
            NewPanelChooserInteractions.Keys);
        Assert.Contains(
            "internal void FocusInitialAction()",
            newPanelChooserCode);
        Assert.Contains(
            "NewPanelTerminalButton.Focus(NavigationMethod.Tab);",
            newPanelChooserCode);
    }

    [Fact]
    public void Main_window_uses_typed_overlay_bridges_and_retains_effect_ownership()
    {
        var mainWindowCode = ApplicationViews.FindPartialClassSources("MainWindow");

        Assert.Contains(
            "MaterializeRoute<CommandPaletteView>(",
            mainWindowCode);
        Assert.Contains(
            "MaterializeRoute<LayoutDesignerView>(",
            mainWindowCode);
        Assert.Contains(
            "MaterializeRoute<NewPanelChooserView>(",
            mainWindowCode);
        Assert.Contains("CommandPaletteOverlay.FocusSearch();", mainWindowCode);
        Assert.Contains(
            "CommandPaletteOverlay.ScrollSelectedResultIntoView();",
            mainWindowCode);
        Assert.Contains(
            "FocusNavigator.FocusLayoutDesignerName();",
            mainWindowCode);
        Assert.Contains(
            "NewPanelChooserOverlay.FocusInitialAction();",
            mainWindowCode);

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain($"\"{extractedName}\"", mainWindowCode);
        }

        Assert.Contains(
            "CloseCoordinator.TryCloseOverlayAsync();",
            mainWindowCode);
        Assert.Contains("Confirmations.DiscardChanges()", mainWindowCode);
        Assert.Contains("ExecuteLauncherSearchTargetAsync(", mainWindowCode);
        Assert.Contains("ViewModel.AddLocalTerminalPanelAsync(", mainWindowCode);
        foreach (var adapter in new[]
                 {
                     "Browser",
                     "FileViewer",
                     "Statistics",
                     "ProcessMonitor",
                 })
        {
            Assert.Contains(
                $"RequestNewAdapterTabAsync(PanelKind.{adapter})",
                mainWindowCode);
            Assert.Contains(
                $"ViewModel.Add{adapter}TabAsync(",
                mainWindowCode);
        }
        Assert.Contains("new SavedScreenEditorDialog(", mainWindowCode);
        Assert.Contains(
            "ViewModel.LayoutDesignerEditor?.RequestCancel()",
            mainWindowCode);
        Assert.Contains("ViewModel.SaveLayoutDesignerAsync(", mainWindowCode);
        Assert.Contains("ViewModel.DismissLayoutDesigner();", mainWindowCode);
        Assert.Contains("ViewModel.BeginEditLayout(layout.Id);", mainWindowCode);
        Assert.Contains("FocusCurrentRoute();", mainWindowCode);
    }

    /// <summary>
    /// Creating a screen closes whatever opened the editor and hands focus back to
    /// the route, rather than leaving the shell focused on a dismissed overlay.
    /// </summary>
    [Fact]
    public void Main_window_restores_route_focus_after_successful_new_item_creation()
    {
        var createScreen = ExtractMethod(
            ApplicationViews.FindPartialClassSources("MainWindow"),
            "private async void OnCreateScreenClick");

        AssertOccursInOrder(
            createScreen,
            "ViewModel.CloseOverlay();",
            "FocusCurrentRoute();");
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method signature '{signature}'.");
        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart > start, $"Could not find method body for '{signature}'.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
            if (depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException(
            $"Could not find the end of method '{signature}'.");
    }

    private static void AssertOccursInOrder(
        string source,
        params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(
                fragment,
                previous + 1,
                StringComparison.Ordinal);
            Assert.True(
                current > previous,
                $"Could not find '{fragment}' after the preceding operation.");
            previous = current;
        }
    }

    private static readonly string[] ExtractedControlNames =
    [
        "CommandSearchBox",
        "LayoutDesignerDock",
        "LauncherSearchResultList",
        "NewLayoutName",
        "NewScreenName",
        "NewTerminalButton",
        "NewWorkspaceName",
        "NewPanelTerminalButton",
    ];

    private static void AssertDelegatedOverlay(
        XDocument mainWindow,
        string viewName,
        string templateName,
        string hostName,
        string visibilityBinding,
        IReadOnlyDictionary<string, string> interactions)
    {
        var overlay = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, viewName, StringComparison.Ordinal));
        var template = Assert.IsType<XElement>(overlay.Parent);
        Assert.Equal("DataTemplate", template.Name.LocalName);
        Assert.Equal(templateName, AttributeValue(template, "Key"));
        var host = Assert.Single(
            mainWindow.Descendants(),
            element => AttributeValue(element, "Name") == hostName);
        Assert.Equal(visibilityBinding, AttributeValue(host, "IsVisible"));
        Assert.Empty(host.Elements());

        foreach (var (interaction, handler) in interactions)
        {
            Assert.Equal(handler, AttributeValue(overlay, interaction));
        }
    }

    private static void AssertStretchingUserControl(XElement root)
    {
        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal(
            "Stretch",
            AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal(
            "Stretch",
            AttributeValue(root, "VerticalContentAlignment"));
    }

    private static XElement AssertOverlayCard(XElement root)
    {
        // The overlay card is the shell's one card control, configured to float.
        // It used to be a Border carrying an "OverlayCard" style class; the class
        // is gone because what a card looks like is no longer decided per view.
        var card = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "SurfaceCard"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Elevation"),
                    "Overlay",
                    StringComparison.Ordinal));
        Assert.Equal(
            "Cycle",
            AttributeValue(card, "KeyboardNavigation.TabNavigation"));
        return card;
    }

    private static void AssertForwardingContract(
        string codeBehind,
        IEnumerable<string> interactions)
    {
        foreach (var interaction in interactions)
        {
            Assert.Contains($" {interaction};", codeBehind);

            // The payload is named, not spelled: most overlays forward the
            // routed args as `e`, and the ones carrying a typed choice forward
            // that choice under its own name. What matters either way is that
            // the original sender and payload go on untouched.
            Assert.Contains(
                $"{interaction}?.Invoke(sender, ",
                codeBehind);
        }

        Assert.DoesNotContain("async ", codeBehind);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind);
        Assert.DoesNotContain("ShowDialog", codeBehind);
        Assert.DoesNotContain("_lifetime", codeBehind);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind);
    }

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static bool HasClasses(XElement element, params string[] classes)
    {
        var actual = (AttributeValue(element, "Classes") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return classes.All(expected =>
            actual.Contains(expected, StringComparer.Ordinal));
    }

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadOverlay(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "Overlays",
            $"{view}.axaml"));

    private static XDocument LoadComponent(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "Components",
            $"{view}.axaml"));

    private static XDocument LoadRuntimePanel(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "RuntimePanels",
            $"{view}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
