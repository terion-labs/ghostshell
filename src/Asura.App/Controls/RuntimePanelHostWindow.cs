using Asura.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Asura.App.Controls;

/// <summary>
/// Hosts a floated Dock document with the same runtime-panel templates that the
/// main window uses. The workspace view creates this only after the main window
/// exists, so restored and newly floated panels share the same event routing.
/// </summary>
internal sealed class RuntimePanelHostWindow : HostWindow
{
    public RuntimePanelHostWindow()
    {
        RefreshRuntimePanelTemplates();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RefreshRuntimePanelTemplates();
        base.OnAttachedToVisualTree(e);
    }

    /// <summary>
    /// Copies the templates after the desktop lifetime has acquired its main
    /// window. Restored Dock windows can be constructed while that main window
    /// is still being built, so constructor-time copying alone is not enough.
    /// </summary>
    internal void RefreshRuntimePanelTemplates()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: MainWindow mainWindow,
                })
        {
            return;
        }

        foreach (var template in mainWindow.DataTemplates)
        {
            if (!DataTemplates.Contains(template))
            {
                DataTemplates.Add(template);
            }
        }
    }
}
