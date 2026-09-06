using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string> ShellInteractions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ActivateTabRequested"] = "OnActivateTabClick",
            ["AgentQuestionResponseKeyDownRequested"] = "OnAgentQuestionResponseKeyDown",
            ["ApproveAgentActionRequested"] = "OnApproveAgentActionClick",
            ["CancelAgentActionRequested"] = "OnCancelAgentActionClick",
            ["CancelAgentChatRequested"] = "OnCancelAgentChatClick",
            ["CancelFileTransferRequested"] = "OnCancelFileTransferClick",
            ["ClearAgentChatRequested"] = "OnClearAgentChatClick",
            ["CloseRuntimeTabRequested"] = "OnCloseRuntimeTabClick",
            ["DeclineAgentQuestionRequested"] = "OnDeclineAgentQuestionClick",
            ["DenyAgentActionRequested"] = "OnDenyAgentActionClick",
            ["DisableAgentYoloRequested"] = "OnDisableAgentYoloClick",
            ["EnableAgentCapabilityAskRequested"] = "OnEnableAgentCapabilityAskClick",
            ["EnableAgentYoloRequested"] = "OnEnableAgentYoloClick",
            ["KeepAgentCapabilityOffRequested"] = "OnKeepAgentCapabilityOffClick",
            ["LoadOlderAgentAuditRequested"] = "OnLoadOlderAgentAuditClick",
            ["OpenWorkspaceRequested"] = "OnOpenWorkspaceClick",
            ["RefreshAgentAuditRequested"] = "OnRefreshAgentAuditClick",
            ["RetryFileTransferRequested"] = "OnRetryFileTransferClick",
            ["RuntimeTabDragEnterRequested"] = "OnRuntimeTabDragEnter",
            ["RuntimeTabDragLeaveRequested"] = "OnRuntimeTabDragLeave",
            ["RuntimeTabDragOverRequested"] = "OnRuntimeTabDragOver",
            ["RuntimeTabDragPointerCaptureLostRequested"] =
                "OnRuntimeTabDragPointerCaptureLost",
            ["RuntimeTabDragPointerMovedRequested"] = "OnRuntimeTabDragPointerMoved",
            ["RuntimeTabDragPointerPressedRequested"] = "OnRuntimeTabDragPointerPressed",
            ["RuntimeTabDragPointerReleasedRequested"] = "OnRuntimeTabDragPointerReleased",
            ["RuntimeTabDropRequested"] = "OnRuntimeTabDrop",
            ["SendAgentChatRequested"] = "OnSendAgentChatClick",
            ["ShowAgentSettingsRequested"] = "OnShowAgentSettingsClick",
            ["ShowCommandPaletteRequested"] = "OnShowCommandPaletteClick",
            ["ShowLauncherRequested"] = "OnShowLauncherClick",
            ["ShowNewItemRequested"] = "OnShowNewItemClick",
            ["ShowNewPanelRequested"] = "OnShowNewPanelClick",
            ["ShowSettingsRequested"] = "OnShowSettingsClick",
            ["SubmitAgentQuestionRequested"] = "OnSubmitAgentQuestionClick",
            ["ToggleAgentRequested"] = "OnToggleAgentClick",
        };

    [Fact]
    public void Workspace_rail_draws_the_isolation_mark_in_the_free_corner()
    {
        var root = Assert.IsType<XElement>(LoadView("WorkspaceView").Root);
        var tile = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                element.Name.LocalName,
                "WorkspaceRailTile",
                StringComparison.Ordinal));
        Assert.Equal("{Binding IsIsolated}", AttributeValue(tile, "IsIsolated"));

        var designSystem = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "DesignSystem.axaml"));
        var mask = Assert.Single(
            designSystem.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "PART_IsolationMask",
                StringComparison.Ordinal));
        var mark = Assert.Single(
            mask.Descendants(),
            element => string.Equals(AttributeValue(element, "Symbol"), "Box", StringComparison.Ordinal));
        Assert.Equal("Box", AttributeValue(mark, "Symbol"));
        Assert.Equal("Right", AttributeValue(mask, "HorizontalAlignment"));
        Assert.Equal("Bottom", AttributeValue(mask, "VerticalAlignment"));
        Assert.Equal("{TemplateBinding RestingBrush}", AttributeValue(mask, "Background"));
        Assert.Equal("{TemplateBinding IsIsolated}", AttributeValue(mask, "IsVisible"));
    }

    [Fact]
    public void Workspace_network_status_avoids_Avalonia_macOS_empty_live_region_crash()
    {
        var root = Assert.IsType<XElement>(LoadView("WorkspaceView").Root);
        var status = FindNamedElement(root, "WorkspaceNetworkStatus");

        Assert.Equal(
            "{Binding WorkspaceNetwork.StatusText}",
            AttributeValue(status, "Text"));
        Assert.Equal(
            "Workspace network status",
            AttributeValue(status, "AutomationProperties.Name"));
        Assert.Null(AttributeValue(status, "AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void Agent_toolbar_robot_pulses_for_a_run_in_any_workspace()
    {
        var root = Assert.IsType<XElement>(LoadView("WorkspaceView").Root);
        var toggle = FindNamedElement(root, "AgentToggleButton");
        var pulse = Assert.Single(
            toggle.Descendants(),
            element => string.Equals(AttributeValue(element, "Name")
, "AgentToolbarActivityPulseIcon", StringComparison.Ordinal));

        Assert.Equal("Bot", AttributeValue(pulse, "Symbol"));
        Assert.Equal("0", AttributeValue(pulse, "Opacity"));
        Assert.Equal(
            "{Binding HasRunningAgent}",
            AttributeValue(pulse, "Classes.running"));
        Assert.Equal(
            "{DynamicResource ShellAccentBrush}",
            AttributeValue(pulse, "Foreground"));

        var designSystem = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "DesignSystem.axaml"));
        var pulseStyle = Assert.Single(
            designSystem.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "icons|SymbolIcon.AgentToolbarActivityPulse.running", StringComparison.Ordinal));
        var animation = Assert.Single(
            pulseStyle.Descendants(),
            element => string.Equals(element.Name.LocalName, "Animation", StringComparison.Ordinal));
        Assert.Equal("0:0:2.5", AttributeValue(animation, "Duration"));
        Assert.Equal(
            ["0%", "50%", "100%"],
            animation.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "KeyFrame", StringComparison.Ordinal))
                .Select(element => AttributeValue(element, "Cue")), StringComparer.Ordinal);
    }

    [Fact]
    public void Main_window_delegates_the_workspace_route_to_one_named_view()
    {
        var mainWindow = LoadView("MainWindow");
        var workspace = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "WorkspaceView", StringComparison.Ordinal));

        Assert.Equal("WorkspaceRouteView", AttributeValue(workspace, "Name"));
        Assert.Equal(
            "{Binding IsWorkspaceVisible}",
            AttributeValue(workspace, "IsVisible"));

        foreach (var (interaction, handler) in ShellInteractions)
        {
            Assert.Equal(handler, AttributeValue(workspace, interaction));
        }

        foreach (var extractedName in RouteControlNames)
        {
            Assert.DoesNotContain(
                mainWindow.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => HasClass(element, "AgentPanel"));
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "RuntimePanelLayoutPanel", StringComparison.Ordinal));
    }

    [Fact]
    public void Workspace_view_preserves_route_geometry_bindings_and_accessibility()
    {
        var workspace = LoadView("WorkspaceView");
        var root = Assert.IsType<XElement>(workspace.Root);

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));
        AssertTitleBarDragRegion(root);

        var surface = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal));
        // Chrome, canvas, bottom tab strip. The fourth row was a status band.
        Assert.Equal("Auto,*,Auto", AttributeValue(surface, "RowDefinitions"));

        foreach (var extractedName in WorkspaceControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        var tabStrip = FindNamedElement(root, "RuntimeTabStrip");
        foreach (var interactiveChromeName in new[]
                 {
                     "RuntimeTabStrip",
                     "WorkspaceTitleBarActions",
                 })
        {
            Assert.Equal(
                "User",
                AttributeValue(
                    FindNamedElement(root, interactiveChromeName),
                    "WindowDecorationProperties.ElementRole"));
        }
        // The strip is a reusable control hosted at whichever edge the profile
        // selects; the template it renders lives in that component.
        Assert.Equal("RuntimeTabStripView", tabStrip.Name.LocalName);
        Assert.Equal("Horizontal", AttributeValue(tabStrip, "Orientation"));
        Assert.Equal("Open runtime tabs", AttributeValue(
            tabStrip,
            "AutomationProperties.Name"));
        Assert.Equal(
            "{Binding RuntimeWorkspace.Tabs}",
            AttributeValue(tabStrip, "Tabs"));
        // The strip carries tabs and nothing else. A home button rode along in
        // it while there was a screen to go to; what stands in that corner now
        // is the workspace menu, which is the chrome's business rather than the
        // strip's.
        Assert.Null(AttributeValue(tabStrip, "ShowHomeTab"));
        var workspacesMenu = FindNamedElement(root, "WorkspacesMenuButton");
        Assert.Equal("Workspaces", AttributeValue(workspacesMenu, "ToolTip.Tip"));
        var workspacesMenuIcon = Assert.Single(
            workspacesMenu.Elements(),
            element => string.Equals(element.Name.LocalName, "Panel", StringComparison.Ordinal));
        Assert.DoesNotContain(
            workspacesMenuIcon.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol"), "Bot", StringComparison.Ordinal));
        Assert.Single(
            workspacesMenu.Descendants(),
            element => string.Equals(element.Name.LocalName, "ItemsControl"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding Workspaces}",
                    StringComparison.Ordinal));
        Assert.Single(
            workspacesMenu.Descendants(),
            element => string.Equals(element.Name.LocalName, "ToggleSwitch"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "IsChecked"),
                    "{Binding ShowWorkspacesPanel, Mode=TwoWay}",
                    StringComparison.Ordinal));

        // Every edge the setting offers has a host, and each is bound to the
        // stored placement rather than being always visible.
        foreach (var (name, visibility) in new[]
                 {
                     ("RuntimeTabStrip", "{Binding IsTabStripVisibleOnTop}"),
                     ("RuntimeTabStripBottom", "{Binding IsTabStripVisibleOnBottom}"),
                     ("RuntimeTabStripSide", "{Binding IsTabStripVisibleOnSide}"),
                 })
        {
            var host = FindNamedElement(root, name);
            Assert.Equal("RuntimeTabStripView", host.Name.LocalName);
            Assert.Equal(
                "{Binding RuntimeWorkspace.Tabs}",
                AttributeValue(host, "Tabs"));
            _ = visibility;
        }

        Assert.Equal(
            "Vertical",
            AttributeValue(FindNamedElement(root, "RuntimeTabStripSide"), "Orientation"));

        // The rail's edge and visibility follow the stored appearance profile, so
        // both are bindings rather than fixed values.
        var rail = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "DockPanel.Dock"),
                    "{Binding WorkspacePanelDock}",
                    StringComparison.Ordinal));
        Assert.True(HasClass(rail, "FloatingSidebar"));
        Assert.Equal(
            "{Binding ShowWorkspacesPanel}",
            AttributeValue(rail, "IsVisible"));

        // The agent panel is an in-window overlay in both of its states: the
        // view itself never docks, so pinning cannot move it between parents
        // and lose the conversation. Docked is a spacer in the layout opening
        // up the exact slot the overlay already occupies.
        var agentWorkspace = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "AgentWorkspaceView", StringComparison.Ordinal));
        Assert.Equal("AgentWorkspaceSurface", AttributeValue(agentWorkspace, "Name"));
        Assert.Null(AttributeValue(agentWorkspace, "DockPanel.Dock"));
        Assert.Equal("Right", AttributeValue(agentWorkspace, "HorizontalAlignment"));
        var agentDockSpacer = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding IsAgentPanelDockedVisible}",
                StringComparison.Ordinal));
        Assert.Equal("Border", agentDockSpacer.Name.LocalName);
        Assert.Equal("Right", AttributeValue(agentDockSpacer, "DockPanel.Dock"));
        // No inline geometry: docked and floating each get width and margin
        // from a state style — an inline value would silently override both,
        // and the floating resize handle needs local values it can set and
        // later clear back to the style's defaults.
        Assert.Null(AttributeValue(agentWorkspace, "Margin"));
        Assert.Null(AttributeValue(agentWorkspace, "Width"));
        Assert.Equal(
            "{Binding !IsAgentPanelDocked}",
            AttributeValue(agentWorkspace, "Classes.floating"));
        Assert.Equal(
            "{Binding IsAgentPanelDocked}",
            AttributeValue(agentWorkspace, "Classes.edgeResizable"));
        Assert.Equal(
            "{Binding IsAgentPanelDocked}",
            AttributeValue(agentWorkspace, "Classes.edgeRight"));
        Assert.Equal(
            "{Binding #AgentWorkspaceSurface.Bounds.Width}",
            AttributeValue(agentDockSpacer, "Width"));
        var agentStates = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Style", StringComparison.Ordinal))
            .ToDictionary(
                element => AttributeValue(element, "Selector") ?? string.Empty,
                element => element, StringComparer.Ordinal);
        // The base style supplies the initial width. Once the docked resize
        // edge changes it, the spacer follows the surface's live bounds.
        var agentBase = agentStates["views|AgentWorkspaceView"];
        Assert.Contains(
            agentBase.Descendants(),
            setter => string.Equals(AttributeValue(setter, "Property"), "Width"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "352", StringComparison.Ordinal));
        Assert.True(agentStates.ContainsKey("views|AgentWorkspaceView.floating"));
        // Floating wears the sidebar's own swatch as glass over the blurred
        // snapshot of what it covers, ringed by the flyout shadow — spread on
        // every edge, because an offset shadow on near-black only ever peeks
        // out at the corners.
        var floatingSurface = agentStates["views|AgentWorkspaceView.floating Border.AgentPanel"];
        Assert.Contains(
            floatingSurface.Descendants(),
            setter => string.Equals(AttributeValue(setter, "Property"), "Background"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "{DynamicResource ShellSidebarOverlayBrush}", StringComparison.Ordinal));
        Assert.Contains(
            floatingSurface.Descendants(),
            setter => string.Equals(AttributeValue(setter, "Property"), "BoxShadow"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "{DynamicResource ShellFlyoutShadow}", StringComparison.Ordinal));
        var agentOverlay = agentWorkspace.Parent!;
        Assert.Equal("Panel", agentOverlay.Name.LocalName);
        Assert.Equal(
            "{Binding IsAgentPanelVisible}",
            AttributeValue(agentOverlay, "IsVisible"));
        // A backgroundless Panel is not hit-testable, so the canvas beneath
        // stays live everywhere the agent panel is not; anything giving the
        // overlay a background would silently swallow the whole workspace.
        Assert.Null(AttributeValue(agentOverlay, "Background"));
        // The flyout's shadow lives outside the panel's bounds. One clipping
        // ancestor deletes it wholesale — which is exactly what happened — so
        // the chain states its openness explicitly, the way the rail does.
        Assert.Equal("False", AttributeValue(agentOverlay, "ClipToBounds"));
        Assert.Equal("False", AttributeValue(agentWorkspace, "ClipToBounds"));
        foreach (var interaction in AgentInteractionNames)
        {
            Assert.Equal(
                ShellInteractions[interaction],
                AttributeValue(agentWorkspace, interaction));
        }

        Assert.DoesNotContain(
            root.Descendants(),
            element => HasClass(element, "AgentPanel"));
        foreach (var controlName in AgentControlNames)
        {
            Assert.DoesNotContain(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    controlName,
                    StringComparison.Ordinal));
        }

        // One canvas per open workspace, and switching shows a different one
        // rather than rebuilding the only one. A dock control does not swap
        // layouts in place — it holds the outgoing tree, drops to bare chrome
        // after the frame, and builds the incoming one later — and it will not
        // build a layout while it is not the canvas being shown, so there is no
        // preparing one aside either. Each workspace keeps its own.
        var canvases = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ItemsControl", StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding OpenWorkspaces}",
                    StringComparison.Ordinal));
        var dockControl = Assert.Single(
            canvases.Descendants(),
            element => string.Equals(element.Name.LocalName, "RuntimeDockControl", StringComparison.Ordinal));
        // The typed canvas owns Dock's presentation lifecycle. Binding Layout
        // directly lets Dock consume an uninitialized restored model and leaves
        // the first frame empty until a tab switch publishes it again.
        Assert.Equal("{Binding ActiveTab}", AttributeValue(dockControl, "RuntimeTab"));
        Assert.Equal(
            "{StaticResource RuntimeDockControlTheme}",
            AttributeValue(dockControl, "Theme"));
        Assert.Null(AttributeValue(dockControl, "Layout"));
        Assert.Null(AttributeValue(dockControl, "Factory"));
        var runtimeCanvasTheme = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ControlTheme", StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Key"),
                    "RuntimeDockControlTheme",
                    StringComparison.Ordinal));
        var runtimeContentHost = Assert.Single(
            runtimeCanvasTheme.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "PART_ContentControl",
                StringComparison.Ordinal));
        Assert.Equal("ContentControl", runtimeContentHost.Name.LocalName);
        // Shown, not in front: the workspace being left keeps its canvas on
        // screen and above the arriving one until that one has built, because a
        // dock control builds only what it is showing.
        Assert.Equal("{Binding IsCanvasShown}", AttributeValue(dockControl, "IsVisible"));
        Assert.Equal("{Binding CanvasDepth}", AttributeValue(dockControl, "ZIndex"));
        // Docking pauses while the layout designer overlay is open: Dock resolves
        // drop targets across every registered DockControl, and the designer's
        // canvas must not be able to dock a slot into the live workspace beneath.
        Assert.Contains(
            "IsLayoutDesignerVisible",
            AttributeValue(dockControl, "IsDockingEnabled") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal("True", AttributeValue(dockControl, "InitializeFactory"));
        Assert.Equal("False", AttributeValue(dockControl, "InitializeLayout"));
        // Dock windows are real platform windows. The managed-window layer would
        // constrain them to the workspace canvas and add a second in-app title
        // bar instead of providing actual floating panels.
        Assert.Null(AttributeValue(dockControl, "EnableManagedWindowLayer"));
        Assert.Null(AttributeValue(dockControl, "MinWidth"));
        Assert.Null(AttributeValue(dockControl, "MinHeight"));
        Assert.NotEqual(
            "ScrollViewer",
            dockControl.Parent?.Name.LocalName, StringComparer.Ordinal);
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "RuntimePanelLayoutPanel", StringComparison.Ordinal));

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding TabReorderStatus}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.LiveSetting"),
                    "Polite",
                    StringComparison.Ordinal));
        var transferManagerButton = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "{Binding HasFileTransfers}",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Open transfer manager",
                    StringComparison.Ordinal));
        Assert.Equal(
            "OnToggleFileTransferManagerClick",
            AttributeValue(transferManagerButton, "Click"));
        Assert.DoesNotContain(
            transferManagerButton.Descendants(),
            element => element.Name.LocalName is "Flyout" or "Popup");
        Assert.Contains(
            transferManagerButton.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Symbol"),
                    "ArrowSort",
                    StringComparison.Ordinal));
        var transferManager = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "SurfaceCard"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "File transfer manager",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsVisible"),
                    "False",
                    StringComparison.Ordinal));
        Assert.Equal("1", AttributeValue(transferManager, "Grid.Row"));
        Assert.Equal(
            "Cycle",
            AttributeValue(
                transferManager,
                "KeyboardNavigation.TabNavigation"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ItemsControl"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding FileTransfers}",
                    StringComparison.Ordinal));
        // The band along the bottom is gone, and with it the two things it
        // spent its life saying: the name of the tab you were looking at, and
        // how many tabs and connections were open. Both were already on screen,
        // in the strip that names the tabs.
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding HostStatus}",
                    StringComparison.Ordinal)
                || string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding WorkspaceStatus}",
                    StringComparison.Ordinal)
                || string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding RuntimeWorkspace.ActiveTab.Source}",
                    StringComparison.Ordinal));
    }

    private static void AssertTitleBarDragRegion(XElement root)
    {
        var titleBar = Assert.Single(
            root.Descendants(),
            element => HasClass(element, "TopChrome"));
        Assert.Equal(
            "TitleBar",
            AttributeValue(titleBar, "WindowDecorationProperties.ElementRole"));
        Assert.Null(AttributeValue(titleBar, "PointerPressed"));
        Assert.Equal(
            "{Binding $parent[views:MainWindow].TitleBarChromeHeight}",
            AttributeValue(titleBar, "MinHeight"));
    }

    [Fact]
    public void Workspace_view_forwards_input_without_taking_shell_ownership()
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class WorkspaceView");

        foreach (var interaction in ShellInteractions.Keys)
        {
            Assert.Contains($" {interaction};", codeBehind, StringComparison.Ordinal);
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "event EventHandler<KeyEventArgs>? AgentQuestionResponseKeyDownRequested;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OnAgentChatTranscriptScrollChanged",
            codeBehind,
            StringComparison.Ordinal);

        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_lifetime", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeTabDragCandidate", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("DataFormat.CreateInProcessFormat", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Dock_active_content_is_materialized_without_the_deferred_queue()
    {
        var themePath = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Styles",
            "GhostShellDockImmediateTheme.axaml");
        var theme = XDocument.Load(themePath);
        var themeRoot = Assert.IsType<XElement>(theme.Root);

        Assert.DoesNotContain(
            themeRoot.Descendants(),
            element => string.Equals(
                element.Name.LocalName,
                "DeferredContentControl",
                StringComparison.Ordinal));
        var rootHost = Assert.Single(
            themeRoot.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "PART_MainContent",
                StringComparison.Ordinal));
        Assert.Equal("ContentControl", rootHost.Name.LocalName);
        var panelHost = Assert.Single(
            themeRoot.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "PART_ContentPresenter",
                StringComparison.Ordinal));
        Assert.Equal("RuntimePanelContentControl", panelHost.Name.LocalName);

        var app = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "App.axaml"));
        Assert.Contains(
            app.Descendants(),
            element => string.Equals(element.Name.LocalName, "StyleInclude", StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Source"),
                    "avares://GhostShell.App/Styles/GhostShellDockImmediateTheme.axaml",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Main_window_retains_runtime_templates_policy_and_visual_focus()
    {
        var mainWindow = LoadView("MainWindow");
        var runtimeTemplateTypes = mainWindow.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "DataTemplate", StringComparison.Ordinal))
            .Select(element => AttributeValue(element, "DataType"))
            .Where(dataType => dataType?.EndsWith(
                "RuntimePanelViewModel",
                StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(10, runtimeTemplateTypes.Length);

        var mainWindowCode = ApplicationViews.FindPartialClassSources("MainWindow");
        var focusNavigator = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "ShellFocusNavigator.cs"));
        var tabDragController = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "RuntimeTabDragController.cs"));
        var dragGhost = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "DragGhostPresenter",
                StringComparison.Ordinal));
        Assert.Equal("SurfaceCard", dragGhost.Name.LocalName);
        Assert.Equal("Overlay", AttributeValue(dragGhost, "Elevation"));
        Assert.Equal("0", AttributeValue(dragGhost, "Opacity"));
        var dragGhostLayer = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                "DragGhostLayer",
                StringComparison.Ordinal));
        Assert.Equal("1000", AttributeValue(dragGhostLayer, "ZIndex"));
        Assert.Contains(
            "ResolveDragGhostPresenter() is not { } presenter",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "this.FindControl<Control>(\"DragGhostPresenter\")",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>",
            tabDragController,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowDragGhost(",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "MoveDragGhost(",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DragDrop.DoDragDropAsync(",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains("viewModel.MoveTabAsync(", tabDragController, StringComparison.Ordinal);
        Assert.Contains("RunCloseFlowAsync(", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentYoloConfirmationDialog(", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("control.Classes.Contains(\"RuntimeTabActivator\")", tabDragController, StringComparison.Ordinal);
        Assert.Contains("control.Classes.Contains(\"RuntimePanelFocusTarget\")", focusNavigator, StringComparison.Ordinal);
        Assert.Contains(".OfType<TerminalPresentationHost>()", focusNavigator, StringComparison.Ordinal);
        Assert.Contains(".OfType<BrowserPresentationHost>()", focusNavigator, StringComparison.Ordinal);
    }

    private static readonly string[] AgentInteractionNames =
    [
        "AgentQuestionResponseKeyDownRequested",
        "ApproveAgentActionRequested",
        "CancelAgentActionRequested",
        "CancelAgentChatRequested",
        "ClearAgentChatRequested",
        "DeclineAgentQuestionRequested",
        "DenyAgentActionRequested",
        "DisableAgentYoloRequested",
        "EnableAgentCapabilityAskRequested",
        "EnableAgentYoloRequested",
        "KeepAgentCapabilityOffRequested",
        "LoadOlderAgentAuditRequested",
        "RefreshAgentAuditRequested",
        "SendAgentChatRequested",
        "ShowAgentSettingsRequested",
        "SubmitAgentQuestionRequested",
    ];

    private static readonly string[] WorkspaceControlNames =
    [
        "RuntimeTabStrip",
    ];

    private static readonly string[] AgentControlNames =
    [
        "AgentChatTranscript",
        "AgentCurrentProgress",
        "AgentPendingQuestion",
        "AgentQuestionResponseInput",
        "AgentPendingCapabilityRequest",
        "AgentRunAudit",
        "AgentChatPromptInput",
    ];

    private static readonly string[] RouteControlNames =
    [
        .. WorkspaceControlNames,
        .. AgentControlNames,
    ];

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static bool HasClass(XElement element, string className) =>
        (AttributeValue(element, "Classes") ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains(className, StringComparer.Ordinal);

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
