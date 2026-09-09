using Asura.Application;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

/// <summary>
/// One independently owned terminal session in the process-wide Quick Terminal window.
/// </summary>
public sealed class QuickTerminalTabViewModel : ObservableObject, IRuntimeTabStripItem
{
    private ConnectionId? _connectionId;
    private string _title;
    private string _icon = "terminal";
    private bool _hasCustomIdentity;
    private EnsureTerminalSessionRequest? _terminalRequest;
    private bool _isActive;
    private bool _canClose;
    private string _agentActivity = string.Empty;
    private bool _isInitializing;
    private string _terminalUnavailableMessage = string.Empty;
    private long _initializationGeneration;

    internal QuickTerminalTabViewModel(ConnectionId? connectionId, string title)
    {
        ConnectionId = connectionId;
        _title = title;
        Id = TabInstanceId.New();
        PanelId = PanelInstanceId.New();
    }

    public TabInstanceId Id { get; }

    /// <summary>
    /// Stable panel identity for this tab. Quick Terminal has one terminal panel
    /// per tab, but it still participates in the same workspace graph contract
    /// as the main window.
    /// </summary>
    public PanelInstanceId PanelId { get; }

    public ConnectionId? ConnectionId
    {
        get => _connectionId;
        private set => SetProperty(ref _connectionId, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Icon => _icon;

    public Symbol IconSymbol => WorkspaceIcons.SymbolFor(_icon);

    public EnsureTerminalSessionRequest? TerminalRequest
    {
        get => _terminalRequest;
        private set
        {
            if (SetProperty(ref _terminalRequest, value))
            {
                OnPropertyChanged(nameof(IsTerminalAvailable));
                OnPropertyChanged(nameof(ShowTerminalPlaceholder));
            }
        }
    }

    public bool IsTerminalAvailable => TerminalRequest is not null;

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    public bool CanClose
    {
        get => _canClose;
        internal set => SetProperty(ref _canClose, value);
    }

    public bool HasAttention => false;

    public string AgentActivity => _agentActivity;

    public bool IsAgentActive => AgentActivity.Length > 0;

    public bool HasAgentActivity => IsAgentActive;

    internal void SetAgentActivity(string? activity)
    {
        var next = string.IsNullOrWhiteSpace(activity)
            ? string.Empty
            : string.Concat(activity);
        if (!SetProperty(ref _agentActivity, next, nameof(AgentActivity)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsAgentActive));
        OnPropertyChanged(nameof(HasAgentActivity));
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (SetProperty(ref _isInitializing, value))
            {
                OnPropertyChanged(nameof(ShowTerminalPlaceholder));
                OnPropertyChanged(nameof(TerminalPlaceholderTitle));
            }
        }
    }

    public bool ShowTerminalPlaceholder => !IsTerminalAvailable;

    public string TerminalPlaceholderTitle => IsInitializing
        ? "Preparing Quick Terminal"
        : "Quick Terminal unavailable";

    public string TerminalUnavailableMessage
    {
        get => _terminalUnavailableMessage;
        private set => SetProperty(ref _terminalUnavailableMessage, value);
    }

    internal long BeginInitialization(ConnectionId connectionId, string title)
    {
        ConnectionId = connectionId;
        if (!_hasCustomIdentity)
        {
            Title = title;
        }
        TerminalRequest = null;
        TerminalUnavailableMessage = "Preparing the connection…";
        IsInitializing = true;
        return ++_initializationGeneration;
    }

    internal void SetUnavailable(string title, string message)
    {
        _initializationGeneration++;
        ConnectionId = null;
        if (!_hasCustomIdentity)
        {
            Title = title;
        }
        TerminalRequest = null;
        TerminalUnavailableMessage = message;
        IsInitializing = false;
    }

    internal bool SetIdentity(string title, string icon)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        Title = title.Trim();
        _hasCustomIdentity = true;
        var normalizedIcon = WorkspaceIcons.OptionFor(icon).Id;
        if (SetProperty(ref _icon, normalizedIcon, nameof(Icon)))
        {
            OnPropertyChanged(nameof(IconSymbol));
        }

        return true;
    }

    internal void SetProgress(long generation, string message)
    {
        if (generation == _initializationGeneration && IsInitializing)
        {
            TerminalUnavailableMessage = message;
        }
    }

    internal void CompleteInitialization(
        long generation,
        EnsureTerminalSessionRequest? request,
        string? error = null)
    {
        if (generation != _initializationGeneration)
        {
            return;
        }

        TerminalRequest = request;
        if (!string.IsNullOrWhiteSpace(error))
        {
            TerminalUnavailableMessage = error;
        }

        IsInitializing = false;
    }
}
