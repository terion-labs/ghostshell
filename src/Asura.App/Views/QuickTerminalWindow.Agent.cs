using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace Asura.App.Views;

public sealed partial class QuickTerminalWindow
{
    private static readonly FilePickerFileType AgentHistoryFileType = new(
        "Asura agent history")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"],
    };
    private void OnToggleAgentClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is QuickTerminalViewModel viewModel)
        {
            viewModel.ToggleAgentPanel();
            Dispatcher.UIThread.Post(UpdateNativeAgentMaterial);
        }
    }

    private void OnToggleAgentPinClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is QuickTerminalViewModel viewModel)
        {
            viewModel.ToggleAgentPanelPin();
            Dispatcher.UIThread.Post(UpdateNativeAgentMaterial);
        }
    }

    private async void OnSendAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.SendAgentPromptAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnQueueAgentSteeringClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel { AgentChat: { } agentChat })
        {
            return;
        }

        try
        {
            if (agentChat.CanOfferFollowUpQueue)
            {
                await agentChat.QueueSteeringAsync(_lifetime.Token);
            }
            else if (DataContext is QuickTerminalViewModel viewModel)
            {
                await viewModel.SendAgentPromptAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnAttachAgentImageClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel
            {
                AgentChat: { CanAttachImages: true } agentChat,
            })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Attach an image to the agent prompt",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"],
                        MimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"],
                    },
                ],
            });
        if (files.Count != 1)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            var bytes = await MainWindow.ReadBoundedImageAsync(stream, _lifetime.Token);
            agentChat.AddPendingImage(
                new AgentImageAttachment(
                    files[0].Name,
                    MainWindow.DetectImageMediaType(bytes),
                    bytes));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or IOException)
        {
            agentChat.ReportTargetUnavailable(exception.Message);
        }
    }

    private void OnClearAgentImagesClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is QuickTerminalViewModel { AgentChat: { } agentChat })
        {
            agentChat.ClearPendingImages();
        }
    }

    private async void OnAgentQuestionResponseKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter
            || e.KeyModifiers != AvaloniaKeyModifiers.None
            || DataContext is not QuickTerminalViewModel
            {
                AgentChat.CanSubmitQuestionResponse: true,
            } viewModel)
        {
            return;
        }

        e.Handled = true;
        await RunAgentActionAsync(
            viewModel,
            (agent, token) => agent.SubmitQuestionResponseAsync(token));
    }

    private async void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.SubmitQuestionResponseAsync(token));

    private async void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.DeclineQuestionAsync(token));

    private async void OnEnableAgentCapabilityAskClick(
        object? sender,
        RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.EnableCapabilityAskAsync(token));

    private async void OnKeepAgentCapabilityOffClick(
        object? sender,
        RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.KeepCapabilityOffAsync(token));

    private async void OnCancelAgentChatClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.StopAsync(token));

    private async void OnCancelAgentActionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.CancelActiveActionAsync(token));

    private async void OnClearAgentChatClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.ClearAsync(token));

    private async void OnStartNewAgentConversationClick(object? sender, RoutedEventArgs e) =>
        await RunQuickAgentConversationActionAsync(
            sender,
            (agent, token) => agent.StartNewConversationAsync(token));

    private async void OnOpenAgentConversationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AgentRunId runId })
        {
            await RunQuickAgentConversationActionAsync(
                sender,
                (agent, token) => agent.OpenConversationAsync(runId, token));
        }
    }

    private async void OnDeleteAgentConversationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AgentRunId runId }
            && DataContext is QuickTerminalViewModel { AgentChat: { } agent }
            && agent.Conversations.FirstOrDefault(item => item.RunId == runId) is { } item
            && await Confirmations.AgentConversationDelete(item.Title).ShowDialog<bool>(this))
        {
            await RunQuickAgentConversationActionAsync(
                sender,
                (agent, token) => agent.DeleteConversationAsync(runId, token),
                hideHistoryFlyout: false);
        }
    }

    private async void OnApplyAgentHistoryRetentionClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel
            {
                AgentChat: { CanApplyHistoryRetention: true } agent,
            }
            || !await Confirmations
                .AgentHistoryRetentionChange(agent.SelectedHistoryRetentionOption)
                .ShowDialog<bool>(this))
        {
            return;
        }

        await agent.ApplyHistoryRetentionAsync(_lifetime.Token);
    }

    private async void OnExportAgentHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel
            {
                AgentChat: { CanExportHistory: true } agent,
            })
        {
            return;
        }

        var selected = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export metadata-only agent history",
                SuggestedFileName = AgentRunHistoryExportController.SuggestedFileName,
                DefaultExtension = "json",
                FileTypeChoices = [AgentHistoryFileType],
                ShowOverwritePrompt = true,
            });
        if (selected?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        _ = await AgentRunHistoryExportController.ExportAsync(
            agent,
            path,
            _lifetime.Token);
    }

    private async void OnCopyAgentMessageClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Button { Tag: AgentChatMessageViewModel message }
            || Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(message.Content);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private async void OnForkAgentConversationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AgentConversationForkPoint forkPoint })
        {
            await RunQuickAgentConversationActionAsync(
                sender,
                (agent, token) => agent.ForkConversationAsync(forkPoint, token),
                hideHistoryFlyout: false);
        }
    }

    private async void OnSelectAgentModelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AiProviderModelDescriptor model })
        {
            await RunQuickAgentConversationActionAsync(
                sender,
                (agent, token) => agent.SelectModelAsync(model, token),
                hideHistoryFlyout: false);
            (sender as Control)?.FindAncestorOfType<AgentWorkspaceView>()?
                .FindControl<Button>("AgentModelPickerButton")?.Flyout?.Hide();
        }
    }

    private async void OnRefreshAgentModelsClick(object? sender, RoutedEventArgs e) =>
        await RunQuickAgentConversationActionAsync(
            sender,
            (agent, token) => agent.RefreshModelsAsync(token),
            hideHistoryFlyout: false);

    private async void OnToggleAgentModelFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AgentModelPickerItemViewModel item })
        {
            await RunQuickAgentConversationActionAsync(
                sender,
                (agent, token) => agent.ToggleFavoriteModelAsync(item, token),
                hideHistoryFlyout: false);
        }
    }

    private async void OnMoveAgentQueuedFollowUpRequested(
        object? sender,
        AgentQueuedFollowUpMoveRequestedEventArgs e)
    {
        _ = sender;
        if (DataContext is not QuickTerminalViewModel { AgentChat: { } agent })
        {
            return;
        }

        try
        {
            await agent.MoveQueuedFollowUpAsync(e.Item, e.DestinationIndex);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RunQuickAgentConversationActionAsync(
        object? sender,
        Func<AgentChatViewModel, CancellationToken, Task> action,
        bool hideHistoryFlyout = true)
    {
        if (DataContext is not QuickTerminalViewModel { AgentChat: { } agent })
        {
            return;
        }

        try
        {
            await action(agent, _lifetime.Token);
            if (hideHistoryFlyout)
            {
                (sender as Control)?.FindAncestorOfType<AgentWorkspaceView>()?
                    .FindControl<Button>("AgentConversationHistoryButton")?.Flyout?.Hide();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.SelectAskApprovalAsync(token));

    private async void OnApproveAgentActionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.DecideAsync(approved: true, token));

    private async void OnDenyAgentActionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.DecideAsync(approved: false, token));

    private async void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.RefreshAuditAsync(token));

    private async void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.LoadOlderAuditAsync(token));

    private async void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel
            {
                AgentChat: not null,
            } viewModel)
        {
            return;
        }

        await RunAgentActionAsync(
            viewModel,
            (agent, token) => agent.SelectFullAccessAsync(token));
    }

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        AgentSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAgentActionFromDataContextAsync(
        Func<AgentChatViewModel, CancellationToken, Task> action)
    {
        if (DataContext is QuickTerminalViewModel viewModel)
        {
            await RunAgentActionAsync(viewModel, action);
        }
    }

    private async Task RunAgentActionAsync(
        QuickTerminalViewModel viewModel,
        Func<AgentChatViewModel, CancellationToken, Task> action)
    {
        if (viewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await action(agentChat, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }
}
