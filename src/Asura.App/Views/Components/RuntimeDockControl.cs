using Asura.App.Controls;
using Asura.App.ViewModels;
using Avalonia;
using Dock.Avalonia.Controls;

namespace Asura.App.Views.Components;

/// <summary>
/// Presents one runtime tab through the workspace's synchronous canvas theme.
///
/// Dock's stock root presenter is intentionally deferred. Runtime workspaces are
/// different: their active root must be materialized in the launch frame. The
/// workspace theme therefore uses a normal content presenter, while this typed
/// boundary publishes the model before initializing it exactly once. Dock must
/// own the layout property before InitLayout walks and activates that graph.
/// </summary>
public sealed class RuntimeDockControl : DockControl
{
    public static readonly StyledProperty<RuntimeTabViewModel?> RuntimeTabProperty =
        AvaloniaProperty.Register<RuntimeDockControl, RuntimeTabViewModel?>(nameof(RuntimeTab));

    public RuntimeDockControl()
    {
        HostWindowFactory = static () => new RuntimePanelHostWindow();
    }

    public RuntimeTabViewModel? RuntimeTab
    {
        get => GetValue(RuntimeTabProperty);
        set => SetValue(RuntimeTabProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RuntimeTabProperty)
        {
            Present(change.GetNewValue<RuntimeTabViewModel?>());
        }
    }

    private void Present(RuntimeTabViewModel? tab)
    {
        if (tab is null)
        {
            Layout = null;
            Factory = null;
            return;
        }

        Factory = tab.DockFactory;
        Layout = tab.DockLayout;
        tab.InitializeDockLayoutForPresentation();
    }
}
