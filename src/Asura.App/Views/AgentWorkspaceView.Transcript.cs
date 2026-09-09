using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Views;

public partial class AgentWorkspaceView
{
    private const int AgentTranscriptPageSize = 24;
    private const int AgentTranscriptScrollSettlePasses = 3;
    private const double AgentTranscriptEndTolerance = 12;
    private const double AgentTranscriptHistoryThreshold = 48;

    private readonly ObservableCollection<AgentChatMessageViewModel>
        _materializedAgentChatMessages = [];
    private readonly HashSet<Control> _agentChatAnchorCandidates = [];
    private IAgentWorkspaceHost? _agentChatHost;
    private ObservableCollection<AgentChatMessageViewModel>? _agentChatMessageSource;
    private int _materializedAgentChatStartIndex;
    private bool _agentChatSourceSynchronizationPending;
    private bool _agentChatShowsTail = true;
    private bool _followAgentChatEnd = true;
    private bool _agentChatScrollPending;
    private bool _agentChatMaterializationInProgress;
    private bool _agentChatHistoryLoadPending;
    private int _agentChatScrollGeneration;

    private void InitializeAgentChatTranscript()
    {
        AgentChatMessages.ItemsSource = _materializedAgentChatMessages;
        _materializedAgentChatMessages.CollectionChanged +=
            OnMaterializedAgentChatMessagesCollectionChanged;
        AgentChatMessages.ContainerPrepared += OnAgentChatMessageContainerPrepared;
        AgentChatMessages.ContainerClearing += OnAgentChatMessageContainerClearing;
        AgentChatTranscript.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnAgentChatTranscriptPointerWheelChanged,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DataContextChanged += OnAgentChatDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetAgentChatHost(DataContext as IAgentWorkspaceHost);
        RequestAgentChatScrollToEnd(force: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetAgentChatHost(null);
        _agentChatScrollGeneration++;
        _agentChatScrollPending = false;
        foreach (var candidate in _agentChatAnchorCandidates)
        {
            AgentChatTranscript.UnregisterAnchorCandidate(candidate);
        }

        _agentChatAnchorCandidates.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAgentChatDataContextChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (VisualRoot is null)
        {
            return;
        }

        SetAgentChatHost(DataContext as IAgentWorkspaceHost);
        RequestAgentChatScrollToEnd(force: true);
    }

    private void SetAgentChatHost(IAgentWorkspaceHost? host)
    {
        if (ReferenceEquals(_agentChatHost, host))
        {
            BindAgentChatMessageSource();
            return;
        }

        if (_agentChatHost is INotifyPropertyChanged previousNotifications)
        {
            previousNotifications.PropertyChanged -=
                OnAgentChatHostPropertyChanged;
        }

        _agentChatHost = host;
        if (_agentChatHost is INotifyPropertyChanged notifications)
        {
            notifications.PropertyChanged += OnAgentChatHostPropertyChanged;
        }

        BindAgentChatMessageSource();
    }

    private void OnAgentChatHostPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        _ = sender;
        if (!string.IsNullOrEmpty(e.PropertyName)
            && !string.Equals(
                e.PropertyName,
                nameof(IAgentWorkspaceHost.AgentChat),
                StringComparison.Ordinal))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RebindAgentChatMessageSource);
            return;
        }

        RebindAgentChatMessageSource();
    }

    private void RebindAgentChatMessageSource()
    {
        BindAgentChatMessageSource();
        RequestAgentChatScrollToEnd(force: true);
    }

    private void BindAgentChatMessageSource() =>
        SetAgentChatMessageSource(_agentChatHost?.AgentChat?.Messages);

    private void SetAgentChatMessageSource(
        ObservableCollection<AgentChatMessageViewModel>? source)
    {
        if (ReferenceEquals(_agentChatMessageSource, source))
        {
            return;
        }

        _agentChatMessageSource?.CollectionChanged -=
            OnAgentChatMessageSourceCollectionChanged;

        _agentChatMessageSource = source;
        _agentChatMessageSource?.CollectionChanged +=
            OnAgentChatMessageSourceCollectionChanged;

        _agentChatShowsTail = true;
        _materializedAgentChatStartIndex = Math.Max(
            0,
            (_agentChatMessageSource?.Count ?? 0) - AgentTranscriptPageSize);
        SynchronizeMaterializedAgentChatMessages();
    }

    private void OnAgentChatMessageSourceCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_agentChatSourceSynchronizationPending)
        {
            return;
        }

        _agentChatSourceSynchronizationPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _agentChatSourceSynchronizationPending = false;
                SynchronizeMaterializedAgentChatMessages();
            },
            DispatcherPriority.Background);
    }

    private void SynchronizeMaterializedAgentChatMessages()
    {
        _agentChatMaterializationInProgress = true;
        try
        {
            SynchronizeMaterializedAgentChatMessagesCore();
        }
        finally
        {
            _agentChatMaterializationInProgress = false;
        }
    }

    private void SynchronizeMaterializedAgentChatMessagesCore()
    {
        if (_agentChatMessageSource is null)
        {
            _materializedAgentChatStartIndex = 0;
            _materializedAgentChatMessages.Clear();
            return;
        }

        if (_agentChatShowsTail)
        {
            SynchronizeAgentChatTail();
            return;
        }

        if (!MaterializedAgentChatRangeStillMatchesSource())
        {
            _agentChatShowsTail = true;
            _materializedAgentChatStartIndex = Math.Max(
                0,
                _agentChatMessageSource.Count - AgentTranscriptPageSize);
        }

        ReplaceMaterializedAgentChatMessages(
            _agentChatMessageSource.Skip(_materializedAgentChatStartIndex));
    }

    private void SynchronizeAgentChatTail()
    {
        if (_agentChatMessageSource is null)
        {
            return;
        }

        var desiredStart = Math.Max(
            0,
            _agentChatMessageSource.Count - AgentTranscriptPageSize);
        var removedFromFront = desiredStart - _materializedAgentChatStartIndex;
        var retainedCount = _materializedAgentChatMessages.Count - removedFromFront;
        var tailStillMatches = removedFromFront >= 0
            && retainedCount >= 0
            && retainedCount
                <= _agentChatMessageSource.Count - desiredStart;
        for (var index = 0; tailStillMatches && index < retainedCount; index++)
        {
            tailStillMatches = _materializedAgentChatMessages[index + removedFromFront]
                == _agentChatMessageSource[desiredStart + index];
        }

        if (!tailStillMatches)
        {
            _materializedAgentChatStartIndex = desiredStart;
            ReplaceMaterializedAgentChatMessages(
                _agentChatMessageSource.Skip(desiredStart));
            return;
        }

        for (var index = 0; index < removedFromFront; index++)
        {
            _materializedAgentChatMessages.RemoveAt(0);
        }

        _materializedAgentChatStartIndex = desiredStart;
        for (var index = desiredStart + _materializedAgentChatMessages.Count;
             index < _agentChatMessageSource.Count;
             index++)
        {
            _materializedAgentChatMessages.Add(_agentChatMessageSource[index]);
        }
    }

    private bool MaterializedAgentChatRangeStillMatchesSource()
    {
        if (_agentChatMessageSource is null
            || _materializedAgentChatStartIndex > _agentChatMessageSource.Count
            || _materializedAgentChatMessages.Count
                > _agentChatMessageSource.Count - _materializedAgentChatStartIndex)
        {
            return false;
        }

        for (var index = 0; index < _materializedAgentChatMessages.Count; index++)
        {
            if (!Equals(
                    _materializedAgentChatMessages[index],
                    _agentChatMessageSource[_materializedAgentChatStartIndex + index]))
            {
                return false;
            }
        }

        return true;
    }

    private void ReplaceMaterializedAgentChatMessages(
        IEnumerable<AgentChatMessageViewModel> source)
    {
        var replacement = source as IReadOnlyList<AgentChatMessageViewModel>
            ?? [.. source];
        var shared = 0;
        var maximumShared = Math.Min(
            _materializedAgentChatMessages.Count,
            replacement.Count);
        while (shared < maximumShared
            && _materializedAgentChatMessages[shared] == replacement[shared])
        {
            shared++;
        }

        for (var index = _materializedAgentChatMessages.Count - 1;
             index >= shared;
             index--)
        {
            _materializedAgentChatMessages.RemoveAt(index);
        }

        for (var index = shared; index < replacement.Count; index++)
        {
            _materializedAgentChatMessages.Add(replacement[index]);
        }
    }

    private void OnMaterializedAgentChatMessagesCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RequestAgentChatScrollToEnd(force: false);
    }

    private void RequestAgentChatScrollToEnd(bool force)
    {
        if (force)
        {
            _followAgentChatEnd = true;
            _agentChatScrollGeneration++;
            _agentChatScrollPending = false;
        }

        if (!_followAgentChatEnd || _agentChatScrollPending)
        {
            return;
        }

        _agentChatScrollPending = true;
        var generation = _agentChatScrollGeneration;
        Dispatcher.UIThread.Post(
            () => SettleAgentChatScrollToEnd(generation, pass: 0),
            DispatcherPriority.Loaded);
    }

    private void SettleAgentChatScrollToEnd(int generation, int pass)
    {
        if (generation != _agentChatScrollGeneration)
        {
            return;
        }

        if (!_followAgentChatEnd || VisualRoot is null)
        {
            _agentChatScrollPending = false;
            return;
        }

        AgentChatTranscript.UpdateLayout();
        AgentChatTranscript.ScrollToEnd();
        if (pass + 1 < AgentTranscriptScrollSettlePasses)
        {
            Dispatcher.UIThread.Post(
                () => SettleAgentChatScrollToEnd(generation, pass + 1),
                DispatcherPriority.Loaded);
            return;
        }

        _agentChatScrollPending = false;
    }

    private void OnAgentChatTranscriptScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer transcript)
        {
            return;
        }

        var endOffset = Math.Max(
            0,
            transcript.Extent.Height - transcript.Viewport.Height);
        var isManualUpwardScroll = e.OffsetDelta.Y < 0
            && e.ExtentDelta.Y == 0
            && !_agentChatMaterializationInProgress;
        if (isManualUpwardScroll)
        {
            _followAgentChatEnd = false;
            _agentChatScrollGeneration++;
            _agentChatScrollPending = false;
        }
        else if (transcript.Offset.Y >= endOffset - AgentTranscriptEndTolerance)
        {
            _followAgentChatEnd = true;
        }

        if (_followAgentChatEnd && e.ExtentDelta.Y > 0)
        {
            RequestAgentChatScrollToEnd(force: false);
        }

        if (!_agentChatHistoryLoadPending
            && isManualUpwardScroll
            && transcript.Offset.Y <= AgentTranscriptHistoryThreshold)
        {
            LoadOlderAgentChatMessages();
        }
    }

    private void OnAgentChatTranscriptPointerWheelChanged(
        object? sender,
        PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer transcript || e.Delta.Y <= 0)
        {
            return;
        }

        _followAgentChatEnd = false;
        _agentChatScrollGeneration++;
        _agentChatScrollPending = false;
        if (transcript.Offset.Y <= AgentTranscriptHistoryThreshold
            && !_agentChatHistoryLoadPending
            && !_agentChatMaterializationInProgress)
        {
            LoadOlderAgentChatMessages();
        }
    }

    private void LoadOlderAgentChatMessages()
    {
        if (_agentChatMessageSource is null
            || _materializedAgentChatStartIndex == 0)
        {
            return;
        }

        _agentChatHistoryLoadPending = true;
        _agentChatShowsTail = false;
        var previousStart = _materializedAgentChatStartIndex;
        _materializedAgentChatStartIndex = Math.Max(
            0,
            _materializedAgentChatStartIndex - AgentTranscriptPageSize);
        for (var index = previousStart - 1;
             index >= _materializedAgentChatStartIndex;
             index--)
        {
            _materializedAgentChatMessages.Insert(
                0,
                _agentChatMessageSource[index]);
        }
        Dispatcher.UIThread.Post(
            () =>
            {
                // ScrollViewer keeps the registered visible message anchored
                // while the exact-height rows above it are inserted.
                _agentChatHistoryLoadPending = false;
            },
            DispatcherPriority.Background);
    }

    private void OnAgentChatMessageContainerPrepared(
        object? sender,
        ContainerPreparedEventArgs e)
    {
        _ = sender;
        var candidate = e.Container;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!candidate.IsAttachedToVisualTree()
                    || !ReferenceEquals(
                        candidate.FindAncestorOfType<ScrollViewer>(),
                        AgentChatTranscript)
                    || !_agentChatAnchorCandidates.Add(candidate))
                {
                    return;
                }

                AgentChatTranscript.RegisterAnchorCandidate(candidate);
            },
            DispatcherPriority.Background);
    }

    private void OnAgentChatMessageContainerClearing(
        object? sender,
        ContainerClearingEventArgs e)
    {
        _ = sender;
        if (_agentChatAnchorCandidates.Remove(e.Container))
        {
            AgentChatTranscript.UnregisterAnchorCandidate(e.Container);
        }
    }
}
