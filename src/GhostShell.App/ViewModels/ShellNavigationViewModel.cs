using System.Runtime.InteropServices;

namespace GhostShell.App.ViewModels;

public enum ShellRoute
{
    Workspace,
    Settings,
}

public enum SettingsPage
{
    Appearance,
    Workspaces,
    Networking,
    Keybindings,
    Files,
    Browser,
    Terminal,
    QuickTerminal,
    Secrets,
    Diagnostics,
    Agent,
    Mcp,
    About,
}

public enum ShellOverlay
{
    None,
    CommandPalette,
    NewPanel,
    LayoutDesigner,
    DefinitionEditor,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ShellNavigationSnapshot(
    ShellRoute Route,
    ShellOverlay Overlay,
    long OverlayRevision);

/// <summary>
/// Owns the shell's route, settings subroute, and transient overlay state.
/// Runtime operations capture a snapshot before awaiting the host, then use
/// the revision to avoid replacing navigation that happened while they waited.
/// </summary>
public sealed class ShellNavigationViewModel : ObservableObject
{
    private ShellRoute _route = ShellRoute.Workspace;
    private SettingsPage _settingsPage = SettingsPage.Appearance;
    private ShellOverlay _overlay;
    private long _overlayRevision;

    public ShellRoute Route
    {
        get => _route;
        private set
        {
            if (SetProperty(ref _route, value))
            {
                OnPropertyChanged(nameof(IsWorkspaceVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
            }
        }
    }

    public SettingsPage SettingsPage
    {
        get => _settingsPage;
        set
        {
            if (SetProperty(ref _settingsPage, value))
            {
                OnPropertyChanged(nameof(IsAppearanceSettingsVisible));
                OnPropertyChanged(nameof(IsWorkspaceSettingsVisible));
                OnPropertyChanged(nameof(IsNetworkingSettingsVisible));
                OnPropertyChanged(nameof(IsKeybindingSettingsVisible));
                OnPropertyChanged(nameof(IsFilesSettingsVisible));
                OnPropertyChanged(nameof(IsBrowserSettingsVisible));
                OnPropertyChanged(nameof(IsTerminalSettingsVisible));
                OnPropertyChanged(nameof(IsQuickTerminalSettingsVisible));
                OnPropertyChanged(nameof(IsSecretsSettingsVisible));
                OnPropertyChanged(nameof(IsDiagnosticsSettingsVisible));
                OnPropertyChanged(nameof(IsAgentSettingsVisible));
                OnPropertyChanged(nameof(IsMcpSettingsVisible));
                OnPropertyChanged(nameof(IsAboutSettingsVisible));
            }
        }
    }

    public ShellOverlay Overlay
    {
        get => _overlay;
        private set
        {
            if (SetProperty(ref _overlay, value))
            {
                _overlayRevision++;
                OnPropertyChanged(nameof(HasOverlay));
                OnPropertyChanged(nameof(IsCommandPaletteVisible));
                OnPropertyChanged(nameof(IsNewPanelVisible));
                OnPropertyChanged(nameof(IsLayoutDesignerVisible));
                OnPropertyChanged(nameof(IsDefinitionEditorVisible));
                OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
            }
        }
    }

    public bool IsWorkspaceVisible => Route == ShellRoute.Workspace;

    public bool IsSettingsVisible => Route == ShellRoute.Settings;

    public bool IsWorkspaceCanvasVisible => IsWorkspaceVisible && !HasOverlay;

    public bool IsAppearanceSettingsVisible => SettingsPage == SettingsPage.Appearance;

    public bool IsWorkspaceSettingsVisible => SettingsPage == SettingsPage.Workspaces;

    public bool IsNetworkingSettingsVisible => SettingsPage == SettingsPage.Networking;

    public bool IsKeybindingSettingsVisible => SettingsPage == SettingsPage.Keybindings;

    public bool IsFilesSettingsVisible => SettingsPage == SettingsPage.Files;

    public bool IsBrowserSettingsVisible => SettingsPage == SettingsPage.Browser;

    public bool IsTerminalSettingsVisible => SettingsPage == SettingsPage.Terminal;

    public bool IsQuickTerminalSettingsVisible => SettingsPage == SettingsPage.QuickTerminal;

    public bool IsSecretsSettingsVisible => SettingsPage == SettingsPage.Secrets;

    public bool IsDiagnosticsSettingsVisible => SettingsPage == SettingsPage.Diagnostics;

    public bool IsAgentSettingsVisible => SettingsPage is SettingsPage.Agent or SettingsPage.Mcp;

    public bool IsMcpSettingsVisible => SettingsPage == SettingsPage.Mcp;

    public bool IsAboutSettingsVisible => SettingsPage == SettingsPage.About;

    public bool HasOverlay => Overlay != ShellOverlay.None;

    public bool IsCommandPaletteVisible => Overlay == ShellOverlay.CommandPalette;

    public bool IsNewPanelVisible => Overlay == ShellOverlay.NewPanel;

    public bool IsLayoutDesignerVisible => Overlay == ShellOverlay.LayoutDesigner;

    public bool IsDefinitionEditorVisible => Overlay == ShellOverlay.DefinitionEditor;

    public void ShowSettings(SettingsPage page)
    {
        DismissOverlay();
        SettingsPage = page;
        Route = ShellRoute.Settings;
    }

    public void ShowWorkspace()
    {
        DismissOverlay();
        Route = ShellRoute.Workspace;
    }

    public void ShowOverlay(ShellOverlay overlay) => Overlay = overlay;

    public void DismissOverlay() => Overlay = ShellOverlay.None;

    internal ShellNavigationSnapshot CaptureRuntimeMutation() =>
        new(Route, Overlay, _overlayRevision);

    internal void CompleteRuntimeMutation(ShellNavigationSnapshot initiatingState)
    {
        if (Overlay is ShellOverlay.DefinitionEditor or ShellOverlay.LayoutDesigner)
        {
            return;
        }

        var initiatingOverlayStillOpen =
            _overlayRevision == initiatingState.OverlayRevision
            && Overlay == initiatingState.Overlay;
        var initiatingOverlayWasDismissed =
            initiatingState.Overlay != ShellOverlay.None
            && Overlay == ShellOverlay.None
            && Route == initiatingState.Route;
        var initiatingSurfaceIsUnchanged =
            initiatingState.Overlay == ShellOverlay.None
            && Overlay == ShellOverlay.None
            && Route == initiatingState.Route;
        if (!initiatingOverlayStillOpen
            && !initiatingOverlayWasDismissed
            && !initiatingSurfaceIsUnchanged)
        {
            return;
        }

        Route = ShellRoute.Workspace;
        if (initiatingOverlayStillOpen
            && initiatingState.Overlay is ShellOverlay.CommandPalette
                or ShellOverlay.NewPanel)
        {
            DismissOverlay();
        }
    }

    internal void ShowRoute(ShellRoute route) => Route = route;
}
