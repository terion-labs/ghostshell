using System.Text.RegularExpressions;
using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class QuickTerminalPresentationContractTests
{
    [Fact]
    public void Main_window_reconciles_macos_backing_scale_after_display_changes()
    {
        var repositoryRoot = ApplicationViewCatalog.Load().RepositoryRoot;
        var window = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "MainWindow.axaml.cs"));
        var backingScale = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "MacOsWindowBackingScale.cs"));

        Assert.Contains("ScalingChanged += OnWindowScalingChanged", window, StringComparison.Ordinal);
        Assert.Contains("Screens.Changed += OnScreensChanged", window, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(750)", window, StringComparison.Ordinal);
        Assert.Contains("QueueBackingScaleReconciliation();", window, StringComparison.Ordinal);
        Assert.Contains("viewDidChangeBackingProperties", backingScale, StringComparison.Ordinal);
        Assert.Contains("intermediate pixel size", backingScale, StringComparison.Ordinal);
    }

    [Fact]
    public void Quick_terminal_reveals_inside_a_clipped_transparent_window()
    {
        var document = XDocument.Load(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "QuickTerminalWindow.axaml"));
        var root = Assert.IsType<XElement>(document.Root);
        var viewport = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Name"), "RevealViewport", StringComparison.Ordinal));
        var slidingPanel = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Name"), "SlidingPanel", StringComparison.Ordinal));

        Assert.Equal("Transparent", AttributeValue(root, "Background"));
        Assert.Null(AttributeValue(root, "TransparencyBackgroundFallback"));
        Assert.Equal("True", AttributeValue(viewport, "ClipToBounds"));
        Assert.Equal("Transparent", AttributeValue(viewport, "Background"));
        Assert.Equal("Transparent", AttributeValue(slidingPanel, "Background"));
        Assert.Equal("0", AttributeValue(slidingPanel, "CornerRadius"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Name"), "ResizeGrip"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "PointerPressed"), "OnResizeGripPointerPressed", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "PanelConnectionSelectorView"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Options"), "{Binding ConnectionOptions}", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "RuntimeTabStripView"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Tabs"), "{Binding Tabs}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IconPickerPlacement"), "TopEdgeAlignedLeft"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AddTabRequested"), "OnAddTabRequested", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes"), "PanelAgentGlow"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "BoxShadow")
, "{DynamicResource ShellAgentPanelGlowShadow}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{Binding ActiveTab.HasAgentActivity}", StringComparison.Ordinal));
        var agentGlow = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes"), "PanelAgentGlow", StringComparison.Ordinal));
        Assert.Null(AttributeValue(agentGlow, "Margin"));
        Assert.Equal("0", AttributeValue(agentGlow, "CornerRadius"));
        var controlBar = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "ColumnDefinitions"), "*,Auto,Auto"
, StringComparison.Ordinal) && element.Descendants().Any(descendant => string.Equals(AttributeValue(descendant, "Click"), "OnHideClick", StringComparison.Ordinal)));
        Assert.Equal(
            "{controls:Inset Left=Md, Right=Xs}",
            AttributeValue(controlBar, "Margin"));
        var statusBackground = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Name")
, "QuickTerminalStatusBackground", StringComparison.Ordinal));
        Assert.Equal(
            "{DynamicResource ShellSurfaceBrush}",
            AttributeValue(statusBackground, "Background"));
        var statusDivider = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Name")
, "QuickTerminalStatusDivider", StringComparison.Ordinal));
        Assert.Equal("1", AttributeValue(statusDivider, "Height"));
        Assert.Equal("Top", AttributeValue(statusDivider, "VerticalAlignment"));
        Assert.Equal(
            "{DynamicResource ShellBorderBrush}",
            AttributeValue(statusDivider, "Background"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "AgentWorkspaceView"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Name"), "QuickTerminalAgentSurface"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "MaxHeight")
, "{Binding #AgentViewport.Bounds.Height}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes.floating")
, "{Binding !IsAgentPanelDocked}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes"), "edgeResizable"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes.docked")
, "{Binding IsAgentPanelDocked}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes.edgeLeft")
, "{Binding IsAgentPanelOnLeft}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes.edgeRight")
, "{Binding IsAgentPanelOnRight}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes.anchorBottom")
, "{Binding IsAgentPanelAnchoredBottom}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes.anchorTop")
, "{Binding IsAgentPanelAnchoredTop}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "HorizontalAlignment")
, "{Binding AgentPanelAlignment}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "VerticalAlignment")
, "{Binding AgentPanelVerticalAlignment}", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Width")
, "{Binding #QuickTerminalAgentSurface.Bounds.Width}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{Binding IsAgentPanelDockedVisible}", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Click"), "OnToggleAgentClick"
, StringComparison.Ordinal) && element.Descendants().Any(descendant => string.Equals(AttributeValue(descendant, "Symbol"), "Bot", StringComparison.Ordinal)));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes")
, "AgentToolbarActivityPulse"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Classes.running")
, "{Binding AgentChat.IsBusy}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Opacity"), "0", StringComparison.Ordinal));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Click"), "OnSettingsClick", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "CornerRadius"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value"), "0"
, StringComparison.Ordinal) && element.Parent is { } style
                && string.Equals(AttributeValue(style, "Selector")
, "views|AgentWorkspaceView.docked Border.AgentPanel", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Setter"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Property"), "BorderThickness"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Value"), "1,0,1,0"
, StringComparison.Ordinal) && element.Parent is { } style
                && string.Equals(AttributeValue(style, "Selector")
, "views|AgentWorkspaceView.docked Border.AgentPanel", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "views|AgentWorkspaceView.docked.nativeMaterial Border.AgentPanel"
, StringComparison.Ordinal) && element.Elements().Any(setter => string.Equals(AttributeValue(setter, "Property"), "Background"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "Transparent", StringComparison.Ordinal)));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "views|AgentWorkspaceView.floating.anchorBottom"
, StringComparison.Ordinal) && element.Elements().Any(setter => string.Equals(AttributeValue(setter, "Property"), "Margin"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "0,12,0,0", StringComparison.Ordinal)));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Style"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Selector")
, "views|AgentWorkspaceView.floating.anchorTop"
, StringComparison.Ordinal) && element.Elements().Any(setter => string.Equals(AttributeValue(setter, "Property"), "Margin"
, StringComparison.Ordinal) && string.Equals(AttributeValue(setter, "Value"), "0,0,0,12", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "Text") is
                "{Binding ProfileName, StringFormat={}Profile · {0}}"
                or "{Binding ShortcutStatus}"
                or "{Binding EscapeStatus}");
    }

    [Fact]
    public void Quick_terminal_moves_one_fixed_size_native_surface()
    {
        var repositoryRoot = ApplicationViewCatalog.Load().RepositoryRoot;
        var controller = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "QuickTerminalController.cs"));
        var window = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "QuickTerminalWindow.axaml.cs"));
        var toggle = Regex.Match(
            controller,
            @"public void Toggle\(\)(?<body>.*?)public void Hide\(\)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(toggle.Success);
        Assert.Contains("window.PlaceAt(workingArea.Position, scale)", controller, StringComparison.Ordinal);
        Assert.Contains("CompletePreparedReveal", controller, StringComparison.Ordinal);
        Assert.Contains("AnimateReveal", controller, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplySettings",
            toggle.Groups["body"].Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TryAnimate",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TryClearWindowBacking",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TryKeepBackdropActive",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsWindowMaterial.TrySit",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsMaterial.HudWindow",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TrySetChromeMaterial",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacOsQuickTerminalReveal.TrySetAgentMaterial",
            window,
            StringComparison.Ordinal);
        Assert.Contains("MacOsMaterial.Sidebar", window, StringComparison.Ordinal);
        var nativeReveal = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "MacOsQuickTerminalReveal.cs"));
        Assert.Contains("SetRevealFrames", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("NSVisualEffectView", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("setState:", nativeReveal, StringComparison.Ordinal);
        Assert.Contains(
            "AsuraQuickTerminalChromeView",
            nativeReveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "AsuraQuickTerminalAgentView",
            nativeReveal,
            StringComparison.Ordinal);
        Assert.Contains("objc_allocateClassPair", nativeReveal, StringComparison.Ordinal);
        Assert.DoesNotContain("setTag:", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("MacOsMaterial.HudWindow", window, StringComparison.Ordinal);
        Assert.Contains("AvnView", nativeReveal, StringComparison.Ordinal);
        Assert.Contains("MacOsQuickTerminalFocus.CaptureFrontmostApplication", controller, StringComparison.Ordinal);
        Assert.Contains("MacOsQuickTerminalFocus.TryRestoreFrontmostApplication", controller, StringComparison.Ordinal);
        Assert.Contains("PositionForProgress", window, StringComparison.Ordinal);
        Assert.DoesNotContain("HeightForProgress", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ElementComposition", window, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WindowTransparencyLevel.None",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Quick_terminal_does_not_republish_an_unchanged_native_transparency_hint()
    {
        var window = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "QuickTerminalWindow.axaml.cs"));

        Assert.Contains(
            "if (TransparencyLevelHint.SequenceEqual(hint))",
            window,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(
            window,
            @"TransparencyLevelHint\s*=\s*hint;",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Quick_terminal_tab_drag_preserves_an_ordinary_tab_click()
    {
        var window = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "QuickTerminalWindow.axaml.cs"));
        var pressed = Regex.Match(
            window,
            @"private void OnTabReorderPointerPressed\(.*?private void OnTabReorderPointerMoved\(",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var moved = Regex.Match(
            window,
            @"private void OnTabReorderPointerMoved\(.*?private void OnTabReorderPointerReleased\(",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var released = Regex.Match(
            window,
            @"private void OnTabReorderPointerReleased\(.*?private void OnTabReorderPointerCaptureLost\(",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(pressed.Success);
        Assert.True(moved.Success);
        Assert.True(released.Success);
        Assert.DoesNotContain("Capture(", pressed.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Handled = true", pressed.Value, StringComparison.Ordinal);
        Assert.Contains("reorder.Pointer.Capture(reorder.Source)", moved.Value, StringComparison.Ordinal);
        Assert.Contains("if (!reorder.IsDragging)", released.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Quick_terminal_forced_close_is_safe_when_shutdown_reenters()
    {
        var window = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "QuickTerminalWindow.axaml.cs"));
        var closing = Regex.Match(
            window,
            @"private void OnWindowClosing\(.*?private void RequestDismiss",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(closing.Success);
        Assert.Contains("if (_allowClose)", closing.Value, StringComparison.Ordinal);
        Assert.Contains("_lifetime.Cancel()", closing.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(".Dispose()", closing.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_new_tab_command_targets_the_active_quick_terminal()
    {
        var repositoryRoot = ApplicationViewCatalog.Load().RepositoryRoot;
        var application = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "App.axaml.cs"));
        var controller = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "QuickTerminalController.cs"));

        Assert.Contains(
            "await QuickTerminalController.TryAddTabToActiveQuickTerminalAsync()",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_quickWindowIsActive || _quickWindow?.IsVisible != true)",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("await _viewModel.AddTabAsync()", controller, StringComparison.Ordinal);
        Assert.Contains("_quickWindow.FocusTerminal()", controller, StringComparison.Ordinal);
        Assert.Contains(
            "await QuickTerminalController.TryCloseTabInActiveQuickTerminalAsync()",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _viewModel.CloseTabAsync(activeTab)",
            controller,
            StringComparison.Ordinal);
    }

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes()
            .SingleOrDefault(attribute => string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value;
}
