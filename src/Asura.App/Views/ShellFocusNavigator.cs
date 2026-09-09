using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.Overlays;
using Asura.Application;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Views;

/// <summary>
/// Restores focus to the interactive surface that owns the current shell route.
/// The window lends this navigator its visual root and lifetime; the navigator
/// owns the route, realization-retry, and cancelled-close focus rules.
/// </summary>
internal sealed class ShellFocusNavigator(
    Window window,
    MainWindowViewModel viewModel,
    Func<SettingsView> settingsRoute,
    Func<LayoutDesignerView> layoutDesigner,
    Func<WorkspaceEditorView?> workspaceEditor,
    CancellationToken lifetime)
{
    private const int PanelFocusAttempts = 6;

    private bool _restoreRouteWhenActivated;

    public void FocusCurrentRoute()
    {
        if (viewModel.IsWorkspaceVisible)
        {
            FocusActivePanel();
        }
        else if (viewModel.IsSettingsVisible)
        {
            FocusSettingsBackButton();
        }
    }

    public void FocusActivePanel() => FocusActivePanel(PanelFocusAttempts);

    public void FocusSettingsBackButton() => Post(() =>
        settingsRoute().FocusBackButton());

    public void FocusSavedScreenUndo() => Post(() =>
        settingsRoute().FocusSavedScreenUndo());

    public void FocusLayoutDesignerName() => Post(() =>
    {
        if (viewModel.IsLayoutDesignerVisible)
        {
            layoutDesigner().FocusNameEditor();
        }
    });

    public void FocusDefinitionEditor() => Post(() =>
    {
        if (viewModel.IsDefinitionEditorVisible
            && workspaceEditor() is { } editor)
        {
            editor.FocusInitialControl();
        }
    });

    public TerminalPresentationHost? FindActiveTerminalHost()
    {
        var activePanel = viewModel.ActivePanel;
        return activePanel is null
            ? null
            : window.GetVisualDescendants()
                .OfType<TerminalPresentationHost>()
                .FirstOrDefault(control =>
                    ReferenceEquals(control.DataContext, activePanel));
    }

    public void RestoreAfterCancelledClose()
    {
        _restoreRouteWhenActivated = true;
        window.Activate();
        Post(RestoreRouteIfActive);
    }

    public void NotifyWindowActivated()
    {
        if (_restoreRouteWhenActivated)
        {
            Post(RestoreRouteIfActive);
        }
    }

    private void FocusActivePanel(int attemptsRemaining) => Post(() =>
    {
        if (viewModel.ActivePanel is not { } activePanel)
        {
            return;
        }

        var terminal = FindActiveTerminalHost();
        if (terminal is not null)
        {
            terminal.RequestInputFocus();
            return;
        }

        var browser = window.GetVisualDescendants()
            .OfType<BrowserPresentationHost>()
            .FirstOrDefault(control =>
                ReferenceEquals(control.DataContext, activePanel));
        if (browser is not null)
        {
            browser.RequestInputFocus();
            return;
        }

        // Render-backed panels are realized during layout. Retrying at Loaded
        // priority avoids moving focus elsewhere while a native surface is
        // still becoming available.
        if (attemptsRemaining > 0
            && activePanel is TerminalRuntimePanelViewModel
                or BrowserRuntimePanelViewModel)
        {
            FocusActivePanel(attemptsRemaining - 1);
            return;
        }

        if (activePanel is TerminalRuntimePanelViewModel)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "terminal.focus.fallback",
                SecretSafeDiagnosticKind.Unexpected);
        }

        window.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                ReferenceEquals(control.DataContext, activePanel)
                && control.Classes.Contains("RuntimePanelFocusTarget"))
            ?.Focus();
    });

    private void RestoreRouteIfActive()
    {
        if (!_restoreRouteWhenActivated || !window.IsVisible || !window.IsActive)
        {
            return;
        }

        _restoreRouteWhenActivated = false;
        FocusCurrentRoute();
    }

    private void Post(Action action) => Dispatcher.UIThread.Post(
        () =>
        {
            if (!lifetime.IsCancellationRequested)
            {
                action();
            }
        },
        DispatcherPriority.Loaded);
}
