using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

internal sealed partial class ShellNotificationCenter
{
    private ShellNotificationRecord Record(
        NativeNotificationRoute route,
        PanelNotificationEvent notification,
        ShellNotificationVisibility visibility,
        bool isBeingLookedAt)
    {
        notification = PanelNotificationTextBudget.Clamp(notification);
        var leavesVisualMark =
            notification.Effects.HasFlag(PanelNotificationEffects.Visual);
        var record = new ShellNotificationRecord(
            Guid.NewGuid().ToString("N"),
            route,
            notification,
            visibility,
            isBeingLookedAt || !leavesVisualMark);
        _history.Add(record);
        _historyUtf8Bytes += PanelNotificationTextBudget.Measure(notification);
        while (_history.Count > HistoryCapacity
               || _historyUtf8Bytes > MaximumHistoryUtf8Bytes)
        {
            _historyUtf8Bytes -= PanelNotificationTextBudget.Measure(
                _history[0].Notification);
            _history.RemoveAt(0);
        }

        return record;
    }

    private void RebindUnreadHistory(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId)
    {
        for (var index = 0; index < _history.Count; index++)
        {
            var record = _history[index];
            if (record.IsRead
                || record.Route.WorkspaceId != workspaceId
                || record.Route.PanelId != panelId
                || record.Route.TabId == tabId)
            {
                continue;
            }

            _history[index] = record with
            {
                Route = record.Route with { TabId = tabId },
            };
        }
    }

    private void ShowNative(
        ShellNotificationRecord record,
        string? fallbackTitle = null)
    {
        if (_nativeNotifications is null)
        {
            return;
        }

        var notification = record.Notification;
        var titleSource = string.IsNullOrWhiteSpace(notification.Title)
            ? fallbackTitle ?? "Asura"
            : notification.Title;
        var bodySource = string.IsNullOrWhiteSpace(notification.Body)
            ? DefaultBody(notification.Kind)
            : notification.Body;
        var title = PanelNotificationTextBudget.TruncateTitle(titleSource).Trim();
        var body = PanelNotificationTextBudget.TruncateBody(bodySource).Trim();
        ValueTask delivery;
        try
        {
            delivery = _nativeNotifications.ShowAsync(
                new NativeNotification(
                    record.Id,
                    notification.Kind,
                    title,
                    body,
                    notification.TimestampUtc,
                    record.Route),
                _lifetime.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportNativeFailure(exception);
            return;
        }

        if (!delivery.IsCompletedSuccessfully)
        {
            _ = ObserveNativeDeliveryAsync(delivery);
        }
    }

    private static string DefaultBody(PanelNotificationKind kind) => kind switch
    {
        PanelNotificationKind.Bell => "The terminal rang its bell.",
        PanelNotificationKind.AgentCompleted => "The agent finished its work.",
        PanelNotificationKind.AgentFailed => "The agent run failed.",
        PanelNotificationKind.FileTransferCompleted => "The file transfer completed.",
        PanelNotificationKind.FileTransferFailed => "The file transfer failed.",
        _ => "New notification",
    };

    private async Task ObserveNativeDeliveryAsync(ValueTask delivery)
    {
        try
        {
            await delivery.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportNativeFailure(exception);
        }
    }

    private static void ReportNativeFailure(Exception exception) =>
        SecretSafeDiagnostics.WriteTraceAndStandardError(
            "notifications.native-delivery.failed",
            exception);

    private void OnNativeNotificationActivated(
        object? sender,
        NativeNotificationActivatedEventArgs eventArgs)
    {
        _ = sender;
        Dispatch(() =>
        {
            var index = _history.FindIndex(record =>
                string.Equals(
                    record.Id,
                    eventArgs.NotificationId,
                    StringComparison.Ordinal));
            if (index >= 0 && !_history[index].IsRead)
            {
                _history[index] = _history[index] with { IsRead = true };
            }

            var kind = index >= 0
                ? _history[index].Notification.Kind
                : eventArgs.Kind;
            _notificationActivated?.Invoke(eventArgs.Route, kind);
        });
    }

    private void Dispatch(Action action)
    {
        Task dispatch;
        try
        {
            dispatch = _dispatcher.InvokeAsync(action, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportDispatchFailure(exception);
            return;
        }

        if (!dispatch.IsCompletedSuccessfully)
        {
            _ = ObserveDispatchAsync(dispatch);
        }
    }

    private async Task ObserveDispatchAsync(Task dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportDispatchFailure(exception);
        }
    }

    private static void ReportDispatchFailure(Exception exception) =>
        SecretSafeDiagnostics.WriteTraceAndStandardError(
            "notifications.ui-dispatch.failed",
            exception);
}
