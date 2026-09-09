using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// The rule the centre exists to enforce: a request to be noticed leaves a mark
/// unless you were already looking at the panel that made it, and the mark goes
/// when you look.
/// </summary>
public sealed class ShellNotificationCenterTests
{
    [Fact]
    public void A_notification_marks_the_panel_its_tab_and_its_workspace()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");

        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.True(panel.HasAttention);
        Assert.True(tab.HasAttention);
        Assert.True(workspace.HasAttention);
        Assert.False(panel.IsNotificationPulseActive);
        Assert.True(shell.FlagsRefreshed > 0);
    }

    [Fact]
    public void A_workspace_notification_marks_background_work_without_a_panel()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");

        shell.Center.NotifyWorkspace(workspace);

        Assert.False(panel.HasAttention);
        Assert.False(tab.HasAttention);
        Assert.True(workspace.HasAttention);
        Assert.True(shell.FlagsRefreshed > 0);
    }

    [Fact]
    public void Looking_at_a_workspace_clears_its_workspace_notification()
    {
        var shell = new FakeShell();
        var (workspace, _, _) = shell.AddWorkspace("background");
        shell.Center.NotifyWorkspace(workspace);

        shell.Front = workspace;
        shell.Center.MarkVisibleSeen();

        Assert.False(workspace.HasAttention);
    }

    [Fact]
    public void Workspace_work_finishing_in_the_visible_workspace_leaves_no_mark()
    {
        var shell = new FakeShell();
        var (workspace, _, _) = shell.AddWorkspace("front");
        shell.Front = workspace;

        shell.Center.NotifyWorkspace(workspace);

        Assert.False(workspace.HasAttention);
    }

    [Fact]
    public void Hidden_workspace_owned_activity_notifies_even_when_its_workspace_is_visible()
    {
        var native = new FakeNativeNotificationService();
        var shell = new FakeShell(nativeNotifications: native);
        var (workspace, _, _) = shell.AddWorkspace("front");
        shell.Front = workspace;

        shell.Center.NotifyWorkspaceSource(
            workspace,
            new PanelNotificationEvent(
                1,
                PanelNotificationKind.AgentCompleted,
                "Agent finished",
                workspace.Name,
                DateTimeOffset.UnixEpoch)
            {
                Effects = PanelNotificationEffects.Visual
                    | PanelNotificationEffects.System,
            },
            sourceIsVisible: false);

        Assert.True(workspace.HasAttention);
        Assert.Single(native.Delivered);

        shell.Center.MarkVisibleSeen();
        Assert.True(workspace.HasAttention);

        shell.Center.MarkWorkspaceSourceSeen(workspace);
        Assert.False(workspace.HasAttention);
    }

    /// <summary>
    /// Marking a panel you are already watching would be a dot you could never
    /// clear — it would reappear on the next line of output.
    /// </summary>
    [Fact]
    public void A_notification_from_the_panel_in_front_leaves_no_mark()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("front");
        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);

        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.False(panel.HasAttention);
        Assert.False(workspace.HasAttention);
    }

    [Fact]
    public void A_notification_in_the_exact_visible_panel_pulses_without_unread_or_native_state()
    {
        var clock = new ManualTimerTimeProvider();
        var native = new FakeNativeNotificationService();
        var shell = new FakeShell(
            nativeNotifications: native,
            timeProvider: clock);
        var (workspace, tab, panel) = shell.AddWorkspace("visible");
        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);
        shell.Center.Watch(workspace);

        panel.RaiseNotification(
            PanelNotificationEffects.Visual | PanelNotificationEffects.System);

        Assert.True(panel.IsNotificationPulseActive);
        Assert.False(panel.HasAttention);
        Assert.False(tab.HasAttention);
        Assert.False(workspace.HasAttention);
        Assert.Empty(native.Delivered);
        Assert.True(Assert.Single(shell.Center.History).IsRead);

        clock.FireLatest();

        Assert.False(panel.IsNotificationPulseActive);
    }

    /// <summary>
    /// Same panel, same workspace — but nobody is at the keyboard, so "looking
    /// at it" is not true.
    /// </summary>
    [Fact]
    public void A_notification_marks_the_panel_in_front_when_the_window_is_not_focused()
    {
        var shell = new FakeShell { IsFocused = false };
        var (workspace, tab, panel) = shell.AddWorkspace("front");
        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);

        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.True(panel.HasAttention);
    }

    [Fact]
    public void A_panel_covered_by_another_shell_surface_is_not_treated_as_visible()
    {
        var native = new FakeNativeNotificationService();
        var shell = new FakeShell(nativeNotifications: native);
        var (workspace, tab, panel) = shell.AddWorkspace("covered");
        shell.Front = workspace;
        shell.SurfaceVisible = false;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);
        shell.Center.Watch(workspace);

        panel.RaiseNotification(
            PanelNotificationEffects.Visual | PanelNotificationEffects.System);

        Assert.True(panel.HasAttention);
        Assert.Single(native.Delivered);

        shell.SurfaceVisible = true;
        shell.Center.MarkVisibleSeen();
        Assert.False(panel.HasAttention);
    }

    [Fact]
    public void Looking_at_the_panel_clears_the_whole_chain()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);
        shell.Center.MarkVisibleSeen();

        Assert.False(panel.HasAttention);
        Assert.False(tab.HasAttention);
        Assert.False(workspace.HasAttention);
    }

    /// <summary>
    /// Switching to the workspace is not the same as reading the panel: the
    /// mark belongs to the panel, and a dot that cleared on arrival at the
    /// workspace would send you looking for something already gone.
    /// </summary>
    [Fact]
    public void Arriving_at_the_workspace_on_another_tab_keeps_the_mark()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");
        var otherTab = shell.AddTab(workspace, "other");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        shell.Front = workspace;
        workspace.ActiveTab = otherTab;
        shell.Center.MarkVisibleSeen();

        Assert.True(panel.HasAttention);
        Assert.True(tab.HasAttention);
        Assert.True(workspace.HasAttention);
    }

    /// <summary>
    /// A workspace with two marked panels keeps its dot until both are read —
    /// the aggregate is recomputed rather than counted down.
    /// </summary>
    [Fact]
    public void The_workspace_stays_marked_until_every_panel_is_read()
    {
        var shell = new FakeShell();
        var (workspace, tab, first) = shell.AddWorkspace("background");
        var second = shell.AddPanel(workspace, tab, "second");
        shell.Center.Watch(workspace);
        first.RaiseNotification();
        second.RaiseNotification();

        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(first.Id);
        shell.Center.MarkVisibleSeen();

        Assert.False(first.HasAttention);
        Assert.True(workspace.HasAttention);

        tab.ActivatePanel(second.Id);
        shell.Center.MarkVisibleSeen();

        Assert.False(workspace.HasAttention);
    }

    [Fact]
    public void Forgetting_a_workspace_stops_its_panels_and_drops_its_mark()
    {
        var shell = new FakeShell();
        var (workspace, _, panel) = shell.AddWorkspace("closing");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        shell.Center.Forget(workspace);
        panel.RaiseNotification();

        Assert.False(workspace.HasAttention);
    }

    /// <summary>
    /// A workspace reactivated while it was never closed must not end up with
    /// two subscriptions, which would be harmless for a boolean and a real bug
    /// the moment anything counts.
    /// </summary>
    [Fact]
    public void Watching_a_workspace_twice_subscribes_once()
    {
        var shell = new FakeShell();
        var (workspace, _, panel) = shell.AddWorkspace("reopened");

        shell.Center.Watch(workspace);
        shell.Center.Watch(workspace);

        Assert.Equal(1, panel.SubscriberCount);
    }

    [Fact]
    public void Rewatching_after_a_panel_is_added_attaches_the_new_source()
    {
        var shell = new FakeShell();
        var (workspace, tab, _) = shell.AddWorkspace("dynamic");
        shell.Center.Watch(workspace);
        var added = shell.AddPanel(workspace, tab, "added");

        shell.Center.Watch(workspace);
        added.RaiseNotification();

        Assert.Equal(1, added.SubscriberCount);
        Assert.True(added.HasAttention);
        Assert.True(workspace.HasAttention);
    }

    [Fact]
    public void Rewatching_after_a_marked_panel_is_removed_clears_stale_aggregates()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("remove");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.True(tab.RemovePanel(panel.Id));
        shell.Center.Watch(workspace);

        Assert.Equal(0, panel.SubscriberCount);
        Assert.False(tab.HasAttention);
        Assert.False(workspace.HasAttention);
    }

    [Fact]
    public void Rewatching_a_same_id_replacement_preserves_attention_and_rebinds_source()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("replace");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();
        var replacement = new FakeNotifyingPanel(panel.Id, panel.Title);

        Assert.True(tab.ReplacePanel(panel, replacement));
        shell.Center.Watch(workspace);
        replacement.RaiseNotification();

        Assert.Equal(0, panel.SubscriberCount);
        Assert.Equal(1, replacement.SubscriberCount);
        Assert.True(replacement.HasAttention);
        Assert.True(tab.HasAttention);
    }

    [Fact]
    public void Rewatching_a_panel_moved_between_tabs_routes_its_next_mark_to_the_destination()
    {
        var shell = new FakeShell();
        var (workspace, source, panel) = shell.AddWorkspace("move");
        var destination = shell.AddTab(workspace, "destination");
        shell.Center.Watch(workspace);
        source.Panels.Remove(panel);
        destination.AddPanel(panel);

        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.False(source.HasAttention);
        Assert.True(destination.HasAttention);
    }

    [Fact]
    public void Queued_notification_follows_a_panel_moved_before_dispatch()
    {
        var dispatcher = new QueuedDispatcher();
        var shell = new FakeShell(dispatcher: dispatcher);
        var (workspace, source, panel) = shell.AddWorkspace("move-before-dispatch");
        var destination = shell.AddTab(workspace, "destination");
        shell.Center.Watch(workspace);

        panel.RaiseNotification();
        source.Panels.Remove(panel);
        destination.AddPanel(panel);
        shell.Center.Watch(workspace);
        dispatcher.Drain();

        Assert.False(source.HasAttention);
        Assert.True(destination.HasAttention);
        Assert.True(panel.HasAttention);
    }

    [Fact]
    public void Queued_notification_follows_a_same_id_panel_replacement()
    {
        var dispatcher = new QueuedDispatcher();
        var shell = new FakeShell(dispatcher: dispatcher);
        var (workspace, tab, panel) = shell.AddWorkspace("replace-before-dispatch");
        shell.Center.Watch(workspace);
        var replacement = new FakeNotifyingPanel(panel.Id, panel.Title);

        panel.RaiseNotification();
        Assert.True(tab.ReplacePanel(panel, replacement));
        shell.Center.Watch(workspace);
        dispatcher.Drain();

        Assert.False(panel.HasAttention);
        Assert.True(replacement.HasAttention);
        Assert.True(tab.HasAttention);
        Assert.True(workspace.HasAttention);
    }

    [Fact]
    public void System_effect_delivers_natively_only_when_the_route_is_not_visible()
    {
        var native = new FakeNativeNotificationService();
        var shell = new FakeShell(nativeNotifications: native);
        var (workspace, tab, panel) = shell.AddWorkspace("native");
        shell.Center.Watch(workspace);

        panel.RaiseNotification(
            PanelNotificationEffects.System,
            PanelNotificationKind.Bell);

        var delivered = Assert.Single(native.Delivered);
        Assert.Equal(workspace.Id, delivered.Route.WorkspaceId);
        Assert.Equal(tab.Id, delivered.Route.TabId);
        Assert.Equal(panel.Id, delivered.Route.PanelId);
        Assert.False(panel.HasAttention);

        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);
        panel.RaiseNotification(
            PanelNotificationEffects.System,
            PanelNotificationKind.Bell);

        Assert.Single(native.Delivered);
    }

    [Fact]
    public void Panel_events_are_marshaled_through_the_shell_dispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var shell = new FakeShell(dispatcher: dispatcher);
        var (workspace, _, panel) = shell.AddWorkspace("dispatch");
        shell.Center.Watch(workspace);

        panel.RaiseNotification();

        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.True(panel.HasAttention);
    }

    [Fact]
    public void Looking_at_a_notification_marks_its_retained_record_read()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("history");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();
        Assert.False(Assert.Single(shell.Center.History).IsRead);

        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);
        shell.Center.MarkVisibleSeen();

        Assert.True(Assert.Single(shell.Center.History).IsRead);
    }

    [Fact]
    public void Native_activation_marks_the_exact_record_read_and_returns_its_route()
    {
        var native = new FakeNativeNotificationService();
        NativeNotificationRoute? activatedRoute = null;
        var shell = new FakeShell(
            nativeNotifications: native,
            notificationActivated: (route, _) => activatedRoute = route);
        var (workspace, tab, panel) = shell.AddWorkspace("activation");
        shell.Center.Watch(workspace);
        panel.RaiseNotification(
            PanelNotificationEffects.Visual | PanelNotificationEffects.System);
        var delivered = Assert.Single(native.Delivered);

        native.Activate(delivered);

        Assert.Equal(delivered.Route, activatedRoute);
        Assert.True(Assert.Single(shell.Center.History).IsRead);
        Assert.True(panel.HasAttention);
        Assert.True(tab.HasAttention);
    }

    [Fact]
    public void Native_activation_keeps_its_kind_after_the_record_leaves_bounded_history()
    {
        var native = new FakeNativeNotificationService();
        PanelNotificationKind? activatedKind = null;
        var shell = new FakeShell(
            nativeNotifications: native,
            notificationActivated: (_, kind) => activatedKind = kind);
        var (workspace, _, panel) = shell.AddWorkspace("bounded-history");
        shell.Center.Watch(workspace);

        panel.RaiseNotification(
            PanelNotificationEffects.System,
            PanelNotificationKind.AgentCompleted);
        var oldest = Assert.Single(native.Delivered);
        for (var index = 0; index < 256; index++)
        {
            panel.RaiseNotification(
                PanelNotificationEffects.System,
                PanelNotificationKind.Notification);
        }

        Assert.Equal(256, shell.Center.History.Count);
        native.Activate(oldest);

        Assert.Equal(PanelNotificationKind.AgentCompleted, activatedKind);
    }

    [Fact]
    public void History_is_bounded_by_retained_utf8_bytes_as_well_as_item_count()
    {
        var shell = new FakeShell();
        var (workspace, _, panel) = shell.AddWorkspace("byte-bounded-history");
        shell.Center.Watch(workspace);
        var body = string.Concat(Enumerable.Repeat(
            "\U0001F680",
            PanelNotificationTextBudget.MaximumBodyUtf8Bytes / 4));

        for (var index = 0; index < 256; index++)
        {
            panel.RaiseNotification(
                PanelNotificationEffects.Visual,
                PanelNotificationKind.Notification,
                body);
        }

        Assert.True(shell.Center.History.Count < 256);
        Assert.InRange(
            shell.Center.History.Sum(record =>
                (long)PanelNotificationTextBudget.Measure(record.Notification)),
            1,
            ShellNotificationCenter.MaximumHistoryUtf8Bytes);
    }

    /// <summary>
    /// A panel that can be told to ask for attention, standing in for a
    /// terminal so the rule can be tested without a live session behind it.
    /// </summary>
    private sealed class FakeNotifyingPanel(PanelInstanceId id, string title)
        : RuntimePanelViewModel(id, PanelKind.Terminal, title, "Test"), IPanelNotificationSource
    {
        public event EventHandler<PanelNotificationEvent>? NotificationReceived;

        public int SubscriberCount =>
            NotificationReceived?.GetInvocationList().Length ?? 0;

        public void RaiseNotification(
            PanelNotificationEffects effects = PanelNotificationEffects.Visual,
            PanelNotificationKind kind = PanelNotificationKind.Notification,
            string body = "Waiting for input") =>
            NotificationReceived?.Invoke(
                this,
                new PanelNotificationEvent(
                    1,
                    kind,
                    "Agent",
                    body,
                    DateTimeOffset.UnixEpoch)
                {
                    Effects = effects,
                });
    }

    private sealed class FakeShell
    {
        private int _panelCount;

        public FakeShell(
            IUiThreadDispatcher? dispatcher = null,
            INativeNotificationService? nativeNotifications = null,
            Action<NativeNotificationRoute, PanelNotificationKind>?
                notificationActivated = null,
            TimeProvider? timeProvider = null) =>
            Center = new ShellNotificationCenter(
                () => Front,
                () => IsFocused,
                () => FlagsRefreshed++,
                dispatcher ?? new RecordingDispatcher(),
                nativeNotifications,
                notificationActivated,
                () => SurfaceVisible,
                timeProvider);

        public ShellNotificationCenter Center { get; }

        public RuntimeWorkspaceViewModel? Front { get; set; }

        public bool IsFocused { get; set; } = true;

        public bool SurfaceVisible { get; set; } = true;

        public int FlagsRefreshed { get; private set; }

        public (RuntimeWorkspaceViewModel Workspace, RuntimeTabViewModel Tab, FakeNotifyingPanel Panel)
            AddWorkspace(string name)
        {
            var workspace = new RuntimeWorkspaceViewModel(
                WorkspaceInstanceId.New(),
                name,
                "#B8793A",
                []);
            var tab = AddTab(workspace, $"{name}-tab");
            var panel = AddPanel(workspace, tab, $"{name}-panel");
            return (workspace, tab, panel);
        }

        public RuntimeTabViewModel AddTab(RuntimeWorkspaceViewModel workspace, string name)
        {
            var tab = new RuntimeTabViewModel(new TabInstanceId(name), name, "Test");
            workspace.Tabs.Add(tab);
            return tab;
        }

        public FakeNotifyingPanel AddPanel(
            RuntimeWorkspaceViewModel workspace,
            RuntimeTabViewModel tab,
            string title)
        {
            var panel = new FakeNotifyingPanel(
                new PanelInstanceId($"{title}-{_panelCount++}"),
                title);
            tab.AddPanel(panel);
            return panel;
        }
    }

    private sealed class RecordingDispatcher : IUiThreadDispatcher
    {
        public int InvocationCount { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class QueuedDispatcher : IUiThreadDispatcher
    {
        private readonly Queue<Action> _pending = [];

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pending.Enqueue(action);
            return Task.CompletedTask;
        }

        public void Drain()
        {
            while (_pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class ManualTimerTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void FireLatest()
        {
            Assert.NotEmpty(_timers);
            _timers[^1].Fire();
        }

        private sealed class ManualTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private bool _isDisposed;

            public bool Change(TimeSpan nextDueTime, TimeSpan nextPeriod) =>
                !_isDisposed;

            public void Dispose() => _isDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                Assert.False(_isDisposed);
                Assert.True(dueTime > TimeSpan.Zero);
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                callback(state);
            }
        }
    }

    private sealed class FakeNativeNotificationService : INativeNotificationService
    {
        public List<NativeNotification> Delivered { get; } = [];

        public event EventHandler<NativeNotificationActivatedEventArgs>? Activated;

        public ValueTask ShowAsync(
            NativeNotification notification,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delivered.Add(notification);
            return ValueTask.CompletedTask;
        }

        public void Activate(NativeNotification notification) =>
            Activated?.Invoke(
                this,
                new NativeNotificationActivatedEventArgs(
                    notification.Id,
                    notification.Route,
                    kind: notification.Kind));
    }
}
