using System.ComponentModel;
using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns the recovery projection lifecycle for the active runtime workspace:
/// subscriptions, safe serialization, queued writes, and write-failure display.
/// </summary>
public sealed class RuntimeWorkspaceRecoveryCoordinator : IDisposable
{
    private readonly RuntimeRecoveryWriter? _writer;
    private readonly Func<RuntimeWorkspaceViewModel?> _currentWorkspace;
    private readonly Func<RuntimeHistorySource?> _currentSource;
    private readonly Func<bool> _isShutdown;
    private readonly Action _queueWorkspaceAutoSave;
    private readonly Action<string> _setError;
    private readonly IUiThreadDispatcher _uiThreadDispatcher;
    private bool _sealed;
    private bool _disposed;

    public RuntimeWorkspaceRecoveryCoordinator(
        RuntimeRecoveryWriter? writer,
        Func<RuntimeWorkspaceViewModel?> currentWorkspace,
        Func<RuntimeHistorySource?> currentSource,
        Func<bool> isShutdown,
        Action queueWorkspaceAutoSave,
        Action<string> setError,
        IUiThreadDispatcher uiThreadDispatcher)
    {
        _writer = writer;
        _currentWorkspace = currentWorkspace
            ?? throw new ArgumentNullException(nameof(currentWorkspace));
        _currentSource = currentSource
            ?? throw new ArgumentNullException(nameof(currentSource));
        _isShutdown = isShutdown
            ?? throw new ArgumentNullException(nameof(isShutdown));
        _queueWorkspaceAutoSave = queueWorkspaceAutoSave
            ?? throw new ArgumentNullException(nameof(queueWorkspaceAutoSave));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _uiThreadDispatcher = uiThreadDispatcher
            ?? throw new ArgumentNullException(nameof(uiThreadDispatcher));
        _writer?.WriteFailed += OnWriteFailed;
    }

    public void Track(RuntimeWorkspaceViewModel? workspace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (workspace is null)
        {
            return;
        }

        foreach (var panel in workspace.Tabs.SelectMany(tab => tab.Panels))
        {
            Track(panel);
        }

        foreach (var tab in workspace.Tabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            tab.PropertyChanged += OnTabPropertyChanged;
        }
    }

    public void Track(RuntimePanelViewModel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecoveryRelevant(panel))
        {
            panel.PropertyChanged += OnPanelPropertyChanged;
        }
    }

    public void Untrack(RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        foreach (var panel in workspace.Tabs.SelectMany(tab => tab.Panels))
        {
            Untrack(panel);
        }

        foreach (var tab in workspace.Tabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
        }
    }

    public void Untrack(RuntimePanelViewModel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (IsRecoveryRelevant(panel))
        {
            panel.PropertyChanged -= OnPanelPropertyChanged;
        }
    }

    public void QueueSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queueWorkspaceAutoSave();
        if (_writer is null || _sealed || _isShutdown())
        {
            return;
        }

        string payload;
        try
        {
            payload = RuntimeWorkspaceRecoveryCodec.Serialize(
                _currentWorkspace(),
                _currentSource());
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or System.Text.Json.JsonException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "recovery.snapshot-prepare.failed",
                exception);
            _setError($"Runtime recovery state could not be prepared. {exception.Message}");
            return;
        }

        var queued = _writer.Enqueue(
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            payload);
        if (!queued.IsSuccess)
        {
            PresentWriteFailure(queued.Error!);
        }
    }

    public void Seal()
    {
        if (_sealed)
        {
            return;
        }

        _sealed = true;
        _writer?.WriteFailed -= OnWriteFailed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Seal();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (sender switch
        {
            FileRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(FileRuntimePanelViewModel.SelectedProfile)
                or nameof(FileRuntimePanelViewModel.CurrentLocation)
                or nameof(FileRuntimePanelViewModel.ShowHidden),
            BrowserRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(BrowserRuntimePanelViewModel.CurrentAddress)
                or nameof(BrowserRuntimePanelViewModel.ConnectionId),
            DatabaseRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(DatabaseRuntimePanelViewModel.RecoveryTarget)
                or nameof(DatabaseRuntimePanelViewModel.TunnelConnectionId),
            RedisRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(RedisRuntimePanelViewModel.RecoveryTarget)
                or nameof(RedisRuntimePanelViewModel.TunnelConnectionId),
            TerminalRuntimePanelViewModel => eventArgs.PropertyName is
                nameof(TerminalRuntimePanelViewModel.MultiplexerSession),
            _ => false,
        })
        {
            QueueSnapshot();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (sender is RuntimeTabViewModel
            && string.Equals(
                eventArgs.PropertyName,
                nameof(RuntimeTabViewModel.DockLayoutRevision),
                StringComparison.Ordinal))
        {
            QueueSnapshot();
        }
    }

    private void OnWriteFailed(
        object? sender,
        RuntimeRecoveryWriteFailedEventArgs eventArgs)
    {
        _ = sender;
        PresentWriteFailure(eventArgs.Error);
    }

    private void PresentWriteFailure(ApplicationRunError error)
    {
        SecretSafeDiagnosticProjection.WriteStandardError(
            "recovery.runtime-write.failed",
            SecretSafeDiagnosticKind.Unexpected);
        _ = _uiThreadDispatcher.InvokeAsync(
            () => _setError($"Runtime recovery is unavailable ({error.Code})."),
            CancellationToken.None);
    }

    private static bool IsRecoveryRelevant(RuntimePanelViewModel panel) =>
        panel is FileRuntimePanelViewModel
            or BrowserRuntimePanelViewModel
            or DatabaseRuntimePanelViewModel
            or RedisRuntimePanelViewModel
            or TerminalRuntimePanelViewModel;
}
