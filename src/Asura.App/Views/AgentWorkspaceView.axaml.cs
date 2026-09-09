using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Asura.App.Views;

public sealed partial class AgentWorkspaceView : UserControl
{
    /// <summary>Small enough to tuck away, large enough to keep a whole conversation column.</summary>
    private const double MinimumFloatingWidth = 320;

    private const double MinimumFloatingHeight = 360;

    private static readonly Lazy<Cursor> HorizontalResizeCursor = new(
        () => new Cursor(StandardCursorType.SizeWestEast));

    private static readonly Lazy<Cursor> VerticalResizeCursor = new(
        () => new Cursor(StandardCursorType.SizeNorthSouth));

    private bool _wasFloating;
    private double? _floatingWidth;
    private double? _floatingHeight;
    private double? _dockedWidth;
    private QueuedFollowUpDrag? _queuedFollowUpDrag;
    private Grid? _queuedFollowUpDropTarget;

    public AgentWorkspaceView()
    {
        InitializeComponent();
        InitializeAgentChatTranscript();
        AgentChatPromptInput.AddHandler(
            InputElement.KeyDownEvent,
            OnAgentPromptKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _wasFloating = Classes.Contains("floating");
        Classes.CollectionChanged += (_, _) =>
        {
            PreserveSizeAcrossPresentationChanges();
            UpdateResizeCursor();
        };
        UpdateResizeCursor();
    }

    private void PreserveSizeAcrossPresentationChanges()
    {
        var isFloating = Classes.Contains("floating");
        if (isFloating == _wasFloating)
        {
            return;
        }

        if (_wasFloating)
        {
            _floatingWidth = PreferredSize(Width, Bounds.Width);
            _floatingHeight = PreferredSize(Height, Bounds.Height);
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            if (_dockedWidth is { } dockedWidth)
            {
                Width = dockedWidth;
            }
        }
        else
        {
            _dockedWidth = PreferredSize(Width, Bounds.Width);
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            if (_floatingWidth is { } floatingWidth)
            {
                Width = floatingWidth;
            }

            if (_floatingHeight is { } floatingHeight)
            {
                Height = floatingHeight;
            }
        }

        _wasFloating = isFloating;
    }

    private static double? PreferredSize(double configured, double arranged) =>
        double.IsFinite(configured) && configured > 0
            ? configured
            : double.IsFinite(arranged) && arranged > 0
                ? arranged
                : null;

    /// <summary>
    /// A north-east/south-west double arrow, drawn: macOS exposes no diagonal
    /// resize cursor, so <c>StandardCursorType.BottomLeftCorner</c> falls back
    /// to a crosshair there. White under black keeps it legible on any
    /// surface. One per process — cursors are shared, not per-control.
    /// </summary>
    private static readonly Lazy<Cursor> DiagonalResizeCursor = new(() =>
    {
        const int size = 24;
        var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(size, size),
            new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var head = 5.0;
            var southWest = new Point(5, 19);
            var northEast = new Point(19, 5);
            foreach (var pen in new[]
            {
                new Pen(Brushes.White, 4.5, lineCap: PenLineCap.Round),
                new Pen(Brushes.Black, 2, lineCap: PenLineCap.Round),
            })
            {
                context.DrawLine(pen, southWest, northEast);
                context.DrawLine(pen, northEast, northEast + new Vector(-head, 0));
                context.DrawLine(pen, northEast, northEast + new Vector(0, head));
                context.DrawLine(pen, southWest, southWest + new Vector(head, 0));
                context.DrawLine(pen, southWest, southWest + new Vector(0, -head));
            }
        }

        return new Cursor(bitmap, new PixelPoint(size / 2, size / 2));
    });

    /// <summary>
    /// The flyout sits in the content zone's top-right corner, so the
    /// bottom-left grip grows it leftward and downward. Clamped to the
    /// hosting overlay, less this panel's own margins.
    /// </summary>
    private void OnFloatingResizeDragDelta(object? sender, VectorEventArgs e)
    {
        _ = sender;
        if (Parent is not Control host)
        {
            return;
        }

        var availableWidth = host.Bounds.Width - Margin.Left - Margin.Right;
        if (Classes.Contains("edgeResizable"))
        {
            var widthDelta = Classes.Contains("edgeLeft")
                ? e.Vector.X
                : -e.Vector.X;
            var width = Math.Clamp(
                Bounds.Width + widthDelta,
                MinimumFloatingWidth,
                Math.Max(MinimumFloatingWidth, availableWidth));
            Width = width;
            if (Classes.Contains("floating"))
            {
                _floatingWidth = width;
            }
            else
            {
                _dockedWidth = width;
            }

            return;
        }

        var availableHeight = host.Bounds.Height - Margin.Top - Margin.Bottom;
        _floatingWidth = Math.Clamp(
            Bounds.Width - e.Vector.X,
            MinimumFloatingWidth,
            Math.Max(MinimumFloatingWidth, availableWidth));
        _floatingHeight = Math.Clamp(
            Bounds.Height + e.Vector.Y,
            MinimumFloatingHeight,
            Math.Max(MinimumFloatingHeight, availableHeight));
        Width = _floatingWidth.Value;
        Height = _floatingHeight.Value;
    }

    private void UpdateResizeCursor() =>
        (FloatingResizeHandle.Cursor, FloatingHeightResizeHandle.Cursor) =
            Classes.Contains("edgeResizable")
                ? (HorizontalResizeCursor.Value, VerticalResizeCursor.Value)
                : (DiagonalResizeCursor.Value, VerticalResizeCursor.Value);

    private void OnFloatingHeightResizeDragDelta(object? sender, VectorEventArgs e)
    {
        _ = sender;
        if (Parent is not Control host || !Classes.Contains("edgeResizable"))
        {
            return;
        }

        var availableHeight = Math.Max(
            1,
            host.Bounds.Height - Margin.Top - Margin.Bottom);
        var minimumHeight = Math.Min(MinimumFloatingHeight, availableHeight);
        var heightDelta = Classes.Contains("anchorBottom")
            ? -e.Vector.Y
            : e.Vector.Y;
        _floatingHeight = Math.Clamp(
            Bounds.Height + heightDelta,
            minimumHeight,
            availableHeight);
        Height = _floatingHeight.Value;
    }

    public event EventHandler<RoutedEventArgs>? ApproveAgentActionRequested;

    public event EventHandler<KeyEventArgs>? AgentQuestionResponseKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentSavedScreenTargetRequested;

    public event EventHandler<RoutedEventArgs>? CreateAgentSavedScreenTargetRequested;

    public event EventHandler<RoutedEventArgs>? DeclineAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? DenyAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? DisableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentCapabilityAskRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? KeepAgentCapabilityOffRequested;

    public event EventHandler<RoutedEventArgs>? LoadOlderAgentAuditRequested;

    public event EventHandler<AgentQueuedFollowUpMoveRequestedEventArgs>?
        MoveQueuedFollowUpRequested;

    public event EventHandler<RoutedEventArgs>? StartNewAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? OpenAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? DeleteAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? CopyAgentMessageRequested;

    public event EventHandler<RoutedEventArgs>? ForkAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? SelectAgentModelRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentModelFavoriteRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentModelsRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? ApplyAgentHistoryRetentionRequested;

    public event EventHandler<RoutedEventArgs>? AuthorizeAgentSavedScreenTargetRequested;

    public event EventHandler<RoutedEventArgs>? ExportAgentHistoryRequested;

    public event EventHandler<RoutedEventArgs>? SendAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? QueueAgentSteeringRequested;

    public event EventHandler<RoutedEventArgs>? AttachAgentImageRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentImagesRequested;

    public event EventHandler<RoutedEventArgs>? ShowAgentSettingsRequested;

    public event EventHandler<RoutedEventArgs>? SubmitAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentPinRequested;

    private void OnAgentQuestionResponseKeyDown(object? sender, KeyEventArgs e) =>
        AgentQuestionResponseKeyDownRequested?.Invoke(sender, e);

    private void OnAgentPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShouldQueueSteering(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            QueueAgentSteeringRequested?.Invoke(sender, e);
            return;
        }

        if (!ShouldSubmitPrompt(e.Key, e.KeyModifiers))
        {
            return;
        }

        e.Handled = true;
        SendAgentChatRequested?.Invoke(sender, e);
    }

    private void OnQueuedFollowUpDragPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (_queuedFollowUpDrag is not null
            || sender is not Control
            {
                DataContext: AgentQueuedFollowUpViewModel item,
            } source
            || item.IsEditing
            || !e.Pointer.IsPrimary)
        {
            return;
        }

        var point = e.GetCurrentPoint(source);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        var currentIndex = ResolveQueuedFollowUpIndex(item);
        if (currentIndex < 0 || CountQueuedFollowUpsInGroup(item) < 2)
        {
            return;
        }

        _queuedFollowUpDrag = new QueuedFollowUpDrag(
            source,
            point.Position,
            e.Pointer,
            item,
            currentIndex,
            currentIndex,
            IsDragging: false);
        e.Pointer.Capture(source);
        e.Handled = true;
    }

    private void OnQueuedFollowUpDragMoved(object? sender, PointerEventArgs e)
    {
        if (_queuedFollowUpDrag is not { } drag
            || !ReferenceEquals(sender, drag.Source)
            || !ReferenceEquals(e.Pointer, drag.Pointer))
        {
            return;
        }

        var point = e.GetCurrentPoint(drag.Source);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            CancelQueuedFollowUpDrag(drag.Pointer);
            return;
        }

        if (!drag.IsDragging)
        {
            var delta = point.Position - drag.Origin;
            if (Math.Abs(delta.X) < 5 && Math.Abs(delta.Y) < 5)
            {
                return;
            }

            drag = drag with { IsDragging = true };
        }

        var destinationIndex = ResolveQueuedFollowUpDestination(e, drag.Item);
        drag = drag with { DestinationIndex = destinationIndex };
        _queuedFollowUpDrag = drag;
        ShowQueuedFollowUpDropTarget(drag.Item, destinationIndex);
        e.Handled = true;
    }

    private void OnQueuedFollowUpDragReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (_queuedFollowUpDrag is not { } drag
            || !ReferenceEquals(sender, drag.Source)
            || !ReferenceEquals(e.Pointer, drag.Pointer))
        {
            return;
        }

        _queuedFollowUpDrag = null;
        ClearQueuedFollowUpDropTarget();
        drag.Pointer.Capture(null);
        if (drag.IsDragging && drag.DestinationIndex != drag.SourceIndex)
        {
            MoveQueuedFollowUpRequested?.Invoke(
                this,
                new AgentQueuedFollowUpMoveRequestedEventArgs(
                    drag.Item,
                    drag.DestinationIndex));
        }

        e.Handled = true;
    }

    private void OnQueuedFollowUpDragCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_queuedFollowUpDrag is { } drag
            && ReferenceEquals(e.Pointer, drag.Pointer))
        {
            _queuedFollowUpDrag = null;
            ClearQueuedFollowUpDropTarget();
        }
    }

    private void CancelQueuedFollowUpDrag(IPointer pointer)
    {
        _queuedFollowUpDrag = null;
        ClearQueuedFollowUpDropTarget();
        pointer.Capture(null);
    }

    private int ResolveQueuedFollowUpDestination(
        PointerEventArgs e,
        AgentQueuedFollowUpViewModel source)
    {
        var rows = QueuedFollowUpRows();
        var compatibleRows = rows
            .Where(row => row.Item.IsSteering == source.IsSteering)
            .ToArray();
        var groupStart = rows.FindIndex(
            row => row.Item.IsSteering == source.IsSteering);
        var pointerY = e.GetPosition(AgentQueuedFollowUps).Y;
        var relativeIndex = compatibleRows.Count(row =>
            row.Item != source
            && row.Row.TranslatePoint(
                new Point(0, row.Row.Bounds.Height / 2),
                AgentQueuedFollowUps) is { } center
            && center.Y < pointerY);
        return groupStart + relativeIndex;
    }

    private void ShowQueuedFollowUpDropTarget(
        AgentQueuedFollowUpViewModel source,
        int destinationIndex)
    {
        ClearQueuedFollowUpDropTarget();
        var rows = QueuedFollowUpRows();
        var compatibleRows = rows
            .Where(row => row.Item.IsSteering == source.IsSteering
                && row.Item != source)
            .ToArray();
        var groupStart = rows.FindIndex(
            row => row.Item.IsSteering == source.IsSteering);
        var relativeIndex = destinationIndex - groupStart;
        if (compatibleRows.Length == 0)
        {
            return;
        }

        if (relativeIndex < compatibleRows.Length)
        {
            _queuedFollowUpDropTarget = compatibleRows[relativeIndex].Row;
            _queuedFollowUpDropTarget.Classes.Add("queueDropBefore");
        }
        else
        {
            _queuedFollowUpDropTarget = compatibleRows[^1].Row;
            _queuedFollowUpDropTarget.Classes.Add("queueDropAfter");
        }
    }

    private void ClearQueuedFollowUpDropTarget()
    {
        _queuedFollowUpDropTarget?.Classes.Remove("queueDropBefore");
        _queuedFollowUpDropTarget?.Classes.Remove("queueDropAfter");
        _queuedFollowUpDropTarget = null;
    }

    private int ResolveQueuedFollowUpIndex(AgentQueuedFollowUpViewModel item) =>
        QueuedFollowUpRows().FindIndex(row => row.Item == item);

    private int CountQueuedFollowUpsInGroup(AgentQueuedFollowUpViewModel item) =>
        QueuedFollowUpRows().Count(
            row => row.Item.IsSteering == item.IsSteering);

    private List<QueuedFollowUpRow> QueuedFollowUpRows() =>
        [.. AgentQueuedFollowUps
            .GetVisualDescendants()
            .OfType<Grid>()
            .Where(row => row.Classes.Contains("agentQueueRow"))
            .Select(row => new QueuedFollowUpRow(
                row,
                (AgentQueuedFollowUpViewModel)row.DataContext!))
            .OrderBy(row => row.Row.TranslatePoint(default, AgentQueuedFollowUps)?.Y)];

    internal static bool ShouldSubmitPrompt(Key key, KeyModifiers modifiers) =>
        key == Key.Enter && modifiers == KeyModifiers.None;

    internal static bool ShouldQueueSteering(Key key, KeyModifiers modifiers) =>
        key == Key.Enter && modifiers == KeyModifiers.Meta;

    private void OnApproveAgentActionClick(object? sender, RoutedEventArgs e) =>
        ApproveAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentActionClick(object? sender, RoutedEventArgs e) =>
        CancelAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentChatClick(object? sender, RoutedEventArgs e) =>
        CancelAgentChatRequested?.Invoke(sender, e);

    private void OnClearAgentChatClick(object? sender, RoutedEventArgs e) =>
        ClearAgentChatRequested?.Invoke(sender, e);

    private void OnClearAgentSavedScreenTargetClick(object? sender, RoutedEventArgs e) =>
        ClearAgentSavedScreenTargetRequested?.Invoke(sender, e);

    private void OnCreateAgentSavedScreenTargetClick(object? sender, RoutedEventArgs e) =>
        CreateAgentSavedScreenTargetRequested?.Invoke(sender, e);

    private void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        DeclineAgentQuestionRequested?.Invoke(sender, e);

    private void OnDenyAgentActionClick(object? sender, RoutedEventArgs e) =>
        DenyAgentActionRequested?.Invoke(sender, e);

    private void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        AgentAccessModeButton.Flyout?.Hide();
        DisableAgentYoloRequested?.Invoke(sender, e);
    }

    private void OnEnableAgentCapabilityAskClick(object? sender, RoutedEventArgs e) =>
        EnableAgentCapabilityAskRequested?.Invoke(sender, e);

    private void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        AgentAccessModeButton.Flyout?.Hide();
        EnableAgentYoloRequested?.Invoke(sender, e);
    }

    private void OnKeepAgentCapabilityOffClick(object? sender, RoutedEventArgs e) =>
        KeepAgentCapabilityOffRequested?.Invoke(sender, e);

    private void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e) =>
        LoadOlderAgentAuditRequested?.Invoke(sender, e);

    private void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentAuditRequested?.Invoke(sender, e);

    private void OnApplyAgentHistoryRetentionClick(object? sender, RoutedEventArgs e) =>
        ApplyAgentHistoryRetentionRequested?.Invoke(sender, e);

    private void OnAuthorizeAgentSavedScreenTargetClick(object? sender, RoutedEventArgs e) =>
        AuthorizeAgentSavedScreenTargetRequested?.Invoke(sender, e);

    private void OnExportAgentHistoryClick(object? sender, RoutedEventArgs e) =>
        ExportAgentHistoryRequested?.Invoke(sender, e);

    private void OnSendAgentChatClick(object? sender, RoutedEventArgs e) =>
        SendAgentChatRequested?.Invoke(sender, e);

    private void OnAttachAgentImageClick(object? sender, RoutedEventArgs e) =>
        AttachAgentImageRequested?.Invoke(sender, e);

    private void OnClearAgentImagesClick(object? sender, RoutedEventArgs e) =>
        ClearAgentImagesRequested?.Invoke(sender, e);

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowAgentSettingsRequested?.Invoke(sender, e);

    private void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        SubmitAgentQuestionRequested?.Invoke(sender, e);

    private void OnToggleAgentPinClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentPinRequested?.Invoke(sender, e);

    private void OnStartNewConversationClick(object? sender, RoutedEventArgs e) =>
        StartNewAgentConversationRequested?.Invoke(sender, e);

    private void OnOpenConversationClick(object? sender, RoutedEventArgs e) =>
        OpenAgentConversationRequested?.Invoke(sender, e);

    private void OnDeleteConversationClick(object? sender, RoutedEventArgs e) =>
        DeleteAgentConversationRequested?.Invoke(sender, e);

    private void OnCopyAgentMessageClick(object? sender, RoutedEventArgs e) =>
        CopyAgentMessageRequested?.Invoke(sender, e);

    private void OnForkAgentConversationClick(object? sender, RoutedEventArgs e) =>
        ForkAgentConversationRequested?.Invoke(sender, e);

    private void OnSelectModelClick(object? sender, RoutedEventArgs e) =>
        SelectAgentModelRequested?.Invoke(sender, e);

    private void OnToggleFavoriteModelClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentModelFavoriteRequested?.Invoke(sender, e);

    private void OnRefreshModelsClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentModelsRequested?.Invoke(sender, e);

    private sealed record QueuedFollowUpDrag(
        Control Source,
        Point Origin,
        IPointer Pointer,
        AgentQueuedFollowUpViewModel Item,
        int SourceIndex,
        int DestinationIndex,
        bool IsDragging);

    private sealed record QueuedFollowUpRow(
        Grid Row,
        AgentQueuedFollowUpViewModel Item);
}

public sealed class AgentQueuedFollowUpMoveRequestedEventArgs(
    AgentQueuedFollowUpViewModel item,
    int destinationIndex) : EventArgs
{
    public AgentQueuedFollowUpViewModel Item { get; } = item;

    public int DestinationIndex { get; } = destinationIndex;
}
