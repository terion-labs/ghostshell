using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>
/// Decides what a panel asking to be noticed does to the shell.
///
/// The rule it enforces is one sentence: a request to be noticed leaves a mark
/// unless you were already looking at the panel that made it, and the mark goes
/// when you look. Everything else here is bookkeeping to make that true at
/// three levels at once — the panel, the tab holding it, and the workspace
/// holding that — because the mark on a workspace tile is only meaningful if it
/// answers "is there anything in here I haven't seen".
///
/// It owns the flags rather than each level computing its own, matching how
/// <see cref="RuntimePanelViewModel.IsActive"/> already works: the levels of
/// the runtime tree know nothing about each other, and giving each one a
/// subscription to its children to answer a question the shell already knows
/// the answer to would be a web of listeners for no gain.
/// </summary>
internal sealed partial class ShellNotificationCenter
{
    private const int HistoryCapacity = 256;
    internal const long MaximumHistoryUtf8Bytes = 512 * 1024;
    private static readonly TimeSpan VisiblePanelPulseDuration =
        TimeSpan.FromMilliseconds(900);

    private readonly Dictionary<RuntimePanelViewModel, PanelAttachment> _watched = [];
    private readonly Dictionary<RuntimePanelViewModel, PanelPulse> _panelPulses = [];
    private readonly HashSet<RuntimeWorkspaceViewModel> _workspaceNotifications = [];
    private readonly HashSet<RuntimeWorkspaceViewModel> _workspaceSourceNotifications = [];
    private readonly List<ShellNotificationRecord> _history = [];
    private long _historyUtf8Bytes;
    private readonly Func<RuntimeWorkspaceViewModel?> _frontWorkspace;
    private readonly Func<bool> _isWindowFocused;
    private readonly Func<bool> _isWorkspaceSurfaceVisible;
    private readonly Action _flagsChanged;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly INativeNotificationService? _nativeNotifications;
    private readonly Action<NativeNotificationRoute, PanelNotificationKind>?
        _notificationActivated;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isStopped;

    /// <param name="frontWorkspace">The workspace on screen, or null at the launcher.</param>
    /// <param name="isWindowFocused">
    /// Whether the shell has the user's attention at all. A notification that
    /// arrives while the window is in the background always leaves a mark, even
    /// for the panel that would otherwise have been considered "being looked
    /// at" — nobody was looking.
    /// </param>
    /// <param name="flagsChanged">
    /// Called after any flag moves, so the workspace rails can be re-derived.
    /// </param>
    /// <param name="isWorkspaceSurfaceVisible">
    /// Whether the workspace canvas is actually exposed rather than covered by
    /// Settings or a shell overlay. Defaults to visible for focused unit seams.
    /// </param>
    public ShellNotificationCenter(
        Func<RuntimeWorkspaceViewModel?> frontWorkspace,
        Func<bool> isWindowFocused,
        Action flagsChanged,
        IUiThreadDispatcher? dispatcher = null,
        INativeNotificationService? nativeNotifications = null,
        Action<NativeNotificationRoute, PanelNotificationKind>?
            notificationActivated = null,
        Func<bool>? isWorkspaceSurfaceVisible = null,
        TimeProvider? timeProvider = null)
    {
        _frontWorkspace = frontWorkspace ?? throw new ArgumentNullException(nameof(frontWorkspace));
        _isWindowFocused = isWindowFocused ?? throw new ArgumentNullException(nameof(isWindowFocused));
        _isWorkspaceSurfaceVisible = isWorkspaceSurfaceVisible ?? (static () => true);
        _flagsChanged = flagsChanged ?? throw new ArgumentNullException(nameof(flagsChanged));
        _dispatcher = dispatcher ?? AvaloniaUiThreadDispatcher.Instance;
        _nativeNotifications = nativeNotifications;
        _notificationActivated = notificationActivated;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nativeNotifications?.Activated += OnNativeNotificationActivated;
    }

    public IReadOnlyList<ShellNotificationRecord> History => _history;

    /// <summary>
    /// Reconciles listeners with the workspace's current topology. Safe to call
    /// after every accepted graph mutation: removed panels are detached, moved
    /// panels are rebound to their new tab, and new producers are attached.
    /// </summary>
    public void Watch(RuntimeWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var markedPanelIds = _watched
            .Where(entry => ReferenceEquals(entry.Value.Workspace, workspace))
            .Where(entry => entry.Key.HasAttention)
            .Select(entry => entry.Key.Id)
            .ToHashSet();
        var desired = workspace.Tabs
            .SelectMany(tab => tab.Panels.Select(panel => (Tab: tab, Panel: panel)))
            .ToDictionary(item => item.Panel, item => item.Tab);
        foreach (var (panel, attachment) in _watched
            .Where(entry => ReferenceEquals(entry.Value.Workspace, workspace))
            .ToArray())
        {
            if (desired.TryGetValue(panel, out var tab)
                && ReferenceEquals(attachment.Tab, tab))
            {
                continue;
            }

            Detach(panel, attachment);
        }

        foreach (var tab in workspace.Tabs)
        {
            foreach (var panel in tab.Panels)
            {
                if (markedPanelIds.Contains(panel.Id))
                {
                    panel.HasAttention = true;
                }

                Watch(workspace, tab, panel);
                RebindUnreadHistory(workspace.Id, tab.Id, panel.Id);
            }

            tab.HasAttention = tab.Panels.Any(panel => panel.HasAttention);
        }

        Reaggregate(workspace);
    }

    public void Watch(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(panel);
        if (panel is not IPanelNotificationSource source)
        {
            return;
        }

        if (_watched.TryGetValue(panel, out var current))
        {
            if (ReferenceEquals(current.Workspace, workspace)
                && ReferenceEquals(current.Tab, tab))
            {
                return;
            }

            Detach(panel, current);
        }

        void Handler(object? sender, PanelNotificationEvent notification)
        {
            _ = sender;
            Dispatch(() => OnNotification(workspace, panel, notification));
        }

        _watched[panel] = new PanelAttachment(workspace, tab, source, Handler);
        source.NotificationReceived += Handler;
    }

    /// <summary>Stops listening to a workspace and drops its marks.</summary>
    public void Forget(RuntimeWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        foreach (var (panel, attachment) in _watched
            .Where(entry => ReferenceEquals(entry.Value.Workspace, workspace))
            .ToArray())
        {
            Detach(panel, attachment);
        }

        _workspaceNotifications.Remove(workspace);
        _workspaceSourceNotifications.Remove(workspace);
        _history.RemoveAll(record => record.Route.WorkspaceId == workspace.Id);
        RecountHistoryBytes();
        foreach (var tab in workspace.Tabs)
        {
            foreach (var panel in tab.Panels)
            {
                panel.HasAttention = false;
            }

            tab.HasAttention = false;
        }

        workspace.HasAttention = false;
        _flagsChanged();
    }

    public void ForgetAll()
    {
        if (_isStopped)
        {
            return;
        }

        _isStopped = true;
        foreach (var attachment in _watched.Values)
        {
            attachment.Source.NotificationReceived -= attachment.Handler;
        }

        _watched.Clear();
        foreach (var pulse in _panelPulses.Values)
        {
            pulse.Timer?.Dispose();
            pulse.Panel.IsNotificationPulseActive = false;
        }

        _panelPulses.Clear();
        _workspaceNotifications.Clear();
        _workspaceSourceNotifications.Clear();
        _history.Clear();
        _historyUtf8Bytes = 0;
        _nativeNotifications?.Activated -= OnNativeNotificationActivated;

        _lifetime.Cancel();
    }

    private void Detach(
        RuntimePanelViewModel panel,
        PanelAttachment attachment)
    {
        attachment.Source.NotificationReceived -= attachment.Handler;
        _watched.Remove(panel);
        ClearPanelPulse(panel);
    }

    private bool IsBeingLookedAt(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel) =>
        _isWindowFocused()
        && _isWorkspaceSurfaceVisible()
        && ReferenceEquals(_frontWorkspace(), workspace)
        && ReferenceEquals(workspace.ActiveTab, tab)
        && ReferenceEquals(tab.ActivePanel, panel);

    /// <summary>
    /// A tab is marked when any of its panels is; a workspace when any of its
    /// tabs is. Recomputed from the panels rather than counted up and down,
    /// because a count that drifts is a dot that never goes away.
    /// </summary>
    private void Reaggregate(RuntimeWorkspaceViewModel workspace)
    {
        workspace.HasAttention = _workspaceNotifications.Contains(workspace)
            || _workspaceSourceNotifications.Contains(workspace)
            || workspace.Tabs.Any(candidate => candidate.HasAttention);
        _flagsChanged();
    }

    private void RecountHistoryBytes() =>
        _historyUtf8Bytes = _history.Sum(record =>
            (long)PanelNotificationTextBudget.Measure(record.Notification));

    private sealed record PanelAttachment(
        RuntimeWorkspaceViewModel Workspace,
        RuntimeTabViewModel Tab,
        IPanelNotificationSource Source,
        EventHandler<PanelNotificationEvent> Handler);

    private sealed class PanelPulse(RuntimePanelViewModel panel)
    {
        public RuntimePanelViewModel Panel { get; } = panel;

        public ITimer? Timer { get; set; }
    }

    private sealed record PanelPulseCallback(
        ShellNotificationCenter Center,
        PanelPulse Pulse);
}
