using Asura.Application;

namespace Asura.App.ViewModels;

internal sealed partial class ShellNotificationCenter
{
    /// <summary>
    /// Leaves a workspace-level mark for work that has no originating panel,
    /// such as an agent run finishing after its workspace was sent behind the
    /// current one.
    /// </summary>
    public void NotifyWorkspace(RuntimeWorkspaceViewModel workspace)
    {
        NotifyWorkspace(
            workspace,
            new PanelNotificationEvent(
                0,
                PanelNotificationKind.Notification,
                workspace.Name,
                "Background work finished.",
                DateTimeOffset.UtcNow));
    }

    public void NotifyWorkspace(
        RuntimeWorkspaceViewModel workspace,
        PanelNotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(notification);
        PublishWorkspaceNotification(
            workspace,
            notification,
            ShellNotificationVisibility.Workspace,
            _isWindowFocused()
            && _isWorkspaceSurfaceVisible()
            && ReferenceEquals(_frontWorkspace(), workspace));
    }

    public void NotifyWorkspaceSource(
        RuntimeWorkspaceViewModel workspace,
        PanelNotificationEvent notification,
        bool sourceIsVisible)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(notification);
        var isBeingLookedAt = sourceIsVisible
            && _isWindowFocused()
            && _isWorkspaceSurfaceVisible()
            && ReferenceEquals(_frontWorkspace(), workspace);
        PublishWorkspaceNotification(
            workspace,
            notification,
            ShellNotificationVisibility.WorkspaceSource,
            isBeingLookedAt);
    }

    private void PublishWorkspaceNotification(
        RuntimeWorkspaceViewModel workspace,
        PanelNotificationEvent notification,
        ShellNotificationVisibility visibility,
        bool isBeingLookedAt)
    {
        var route = new NativeNotificationRoute(workspace.Id);
        var record = Record(route, notification, visibility, isBeingLookedAt);
        if (!isBeingLookedAt
            && notification.Effects.HasFlag(PanelNotificationEffects.System))
        {
            ShowNative(record);
        }

        if (isBeingLookedAt
            || !notification.Effects.HasFlag(PanelNotificationEffects.Visual))
        {
            return;
        }

        var marks = visibility == ShellNotificationVisibility.Workspace
            ? _workspaceNotifications
            : _workspaceSourceNotifications;
        if (marks.Add(workspace))
        {
            Reaggregate(workspace);
        }
    }

    /// <summary>
    /// Clears whatever the user can currently see. Called wherever the front
    /// workspace, its active tab, its active panel, or the window's focus
    /// changes.
    /// </summary>
    public void MarkVisibleSeen()
    {
        if (!_isWindowFocused()
            || !_isWorkspaceSurfaceVisible()
            || _frontWorkspace() is not { } workspace)
        {
            return;
        }

        var changed = _workspaceNotifications.Remove(workspace);
        var activePanelId = workspace.ActiveTab?.ActivePanel?.Id;
        for (var index = 0; index < _history.Count; index++)
        {
            var record = _history[index];
            var isVisibleWorkspaceNotification =
                record.Route.WorkspaceId == workspace.Id
                && record.Visibility == ShellNotificationVisibility.Workspace;
            var isVisiblePanelNotification =
                record.Route.WorkspaceId == workspace.Id
                && record.Visibility == ShellNotificationVisibility.Panel
                && activePanelId is { } panelId
                && record.Route.PanelId == panelId;
            if (!record.IsRead
                && (isVisibleWorkspaceNotification || isVisiblePanelNotification))
            {
                _history[index] = record with { IsRead = true };
            }
        }

        if (workspace.ActiveTab is { ActivePanel: { } panel } tab
            && panel.HasAttention)
        {
            panel.HasAttention = false;
            tab.HasAttention = tab.Panels.Any(candidate => candidate.HasAttention);
            changed = true;
        }

        if (changed)
        {
            Reaggregate(workspace);
        }
    }

    /// <summary>
    /// Marks workspace-owned work seen only when its own surface becomes
    /// visible. Opening the workspace alone must not clear a hidden agent run.
    /// </summary>
    public void MarkWorkspaceSourceSeen(RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null
            || !_isWindowFocused()
            || !_isWorkspaceSurfaceVisible()
            || !ReferenceEquals(_frontWorkspace(), workspace))
        {
            return;
        }

        var changed = _workspaceSourceNotifications.Remove(workspace);
        for (var index = 0; index < _history.Count; index++)
        {
            var record = _history[index];
            if (!record.IsRead
                && record.Route.WorkspaceId == workspace.Id
                && record.Visibility == ShellNotificationVisibility.WorkspaceSource)
            {
                _history[index] = record with { IsRead = true };
            }
        }

        if (changed)
        {
            Reaggregate(workspace);
        }
    }

    private void OnNotification(
        RuntimeWorkspaceViewModel emittedWorkspace,
        RuntimePanelViewModel emittedPanel,
        PanelNotificationEvent notification)
    {
        RuntimePanelViewModel panel;
        PanelAttachment attachment;
        if (_watched.TryGetValue(emittedPanel, out var direct)
            && ReferenceEquals(direct.Workspace, emittedWorkspace))
        {
            panel = emittedPanel;
            attachment = direct;
        }
        else
        {
            // The event was already accepted from this workspace's source,
            // but graph reconciliation may replace its VM before the queued UI
            // dispatch runs. Stable panel identity lets the unread moment
            // follow a same-ID replacement instead of disappearing.
            var rebound = _watched.FirstOrDefault(entry =>
                ReferenceEquals(entry.Value.Workspace, emittedWorkspace)
                && entry.Key.Id == emittedPanel.Id);
            if (rebound.Key is null)
            {
                return;
            }

            panel = rebound.Key;
            attachment = rebound.Value;
        }

        var workspace = attachment.Workspace;
        var tab = attachment.Tab;
        var isBeingLookedAt = IsBeingLookedAt(workspace, tab, panel);
        var route = new NativeNotificationRoute(workspace.Id, tab.Id, panel.Id);
        var record = Record(
            route,
            notification,
            ShellNotificationVisibility.Panel,
            isBeingLookedAt);
        if (!isBeingLookedAt
            && notification.Effects.HasFlag(PanelNotificationEffects.System))
        {
            ShowNative(record, panel.Title);
        }

        if (isBeingLookedAt)
        {
            if (notification.Effects.HasFlag(PanelNotificationEffects.Visual))
            {
                PulseVisiblePanel(panel);
            }

            return;
        }

        if (!notification.Effects.HasFlag(PanelNotificationEffects.Visual))
        {
            return;
        }

        panel.HasAttention = true;
        tab.HasAttention = true;
        Reaggregate(workspace);
    }

    /// <summary>
    /// A notification that arrives under the user's eyes is already read, but
    /// silently swallowing it makes a terminal notification look broken. Give
    /// the exact panel one short acknowledgement without creating unread state
    /// at the panel, tab, or workspace levels.
    /// </summary>
    private void PulseVisiblePanel(RuntimePanelViewModel panel)
    {
        ClearPanelPulse(panel);

        var pulse = new PanelPulse(panel);
        _panelPulses.Add(panel, pulse);
        panel.IsNotificationPulseActive = true;
        pulse.Timer = _timeProvider.CreateTimer(
            static state =>
            {
                var callback = (PanelPulseCallback)state!;
                callback.Center.Dispatch(
                    () => callback.Center.CompletePanelPulse(callback.Pulse));
            },
            new PanelPulseCallback(this, pulse),
            VisiblePanelPulseDuration,
            Timeout.InfiniteTimeSpan);
    }

    private void CompletePanelPulse(PanelPulse pulse)
    {
        if (!_panelPulses.TryGetValue(pulse.Panel, out var current)
            || !ReferenceEquals(current, pulse))
        {
            return;
        }

        _panelPulses.Remove(pulse.Panel);
        pulse.Timer?.Dispose();
        pulse.Panel.IsNotificationPulseActive = false;
    }

    private void ClearPanelPulse(RuntimePanelViewModel panel)
    {
        if (_panelPulses.Remove(panel, out var pulse))
        {
            pulse.Timer?.Dispose();
        }

        panel.IsNotificationPulseActive = false;
    }
}
