using System.Collections.ObjectModel;
using System.Globalization;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record AgentChatMessageViewModel(
    AgentChatMessageRole Role,
    string Content,
    string? ReasoningSummary = null,
    AgentChatUsage? Usage = null,
    IReadOnlyList<AgentChatImage>? Images = null,
    AgentReasoningEffort? RequestedReasoningEffort = null,
    AgentConversationForkPoint? ForkPoint = null)
{
    public bool IsUser => Role == AgentChatMessageRole.User;

    public bool IsAssistant => Role == AgentChatMessageRole.Assistant;

    public string Author => IsUser ? "You" : "Asura";

    public bool HasReasoningSummary =>
        IsAssistant && !string.IsNullOrWhiteSpace(ReasoningSummary);

    public string ReasoningSummaryDisplay =>
        AgentReasoningSummaryPresentation.Format(ReasoningSummary);

    public bool HasUsage => IsAssistant && Usage is not null;

    public bool HasMessageText => !string.IsNullOrWhiteSpace(Content);

    public bool CanFork => IsAssistant && HasMessageText && ForkPoint is not null;

    public bool HasReasoningRequest =>
        IsAssistant
        && (RequestedReasoningEffort is not null and not AgentReasoningEffort.Automatic
            || Usage?.ReasoningTokens > 0);

    public bool HasImages => IsUser && Images is { Count: > 0 };

    public string ImagesLabel => Images is not { Count: > 0 } images
        ? string.Empty
        : images.Count == 1
            ? $"Image · {images[0].FileName}"
            : $"{images.Count.ToString(CultureInfo.InvariantCulture)} images";

    public string UsageLabel => Usage is not { } usage
        ? string.Empty
        : $"{usage.TotalTokens.ToString(CultureInfo.InvariantCulture)} tokens · "
            + $"{usage.InputTokens.ToString(CultureInfo.InvariantCulture)} in / "
            + $"{usage.OutputTokens.ToString(CultureInfo.InvariantCulture)} out"
            + (usage.CachedInputTokens > 0
                ? $" · {usage.CachedInputTokens.ToString(CultureInfo.InvariantCulture)} cached"
                : string.Empty)
            + (usage.ReasoningTokens > 0
                ? $" · {usage.ReasoningTokens.ToString(CultureInfo.InvariantCulture)} reasoning"
                : string.Empty);

    public string ReasoningRequestLabel
    {
        get
        {
            if (RequestedReasoningEffort == AgentReasoningEffort.Off)
            {
                return "Reasoning off";
            }

            var effort = RequestedReasoningEffort ?? AgentReasoningEffort.Automatic;
            var label = effort switch
            {
                AgentReasoningEffort.Automatic => "Automatic",
                AgentReasoningEffort.Minimal => "Minimal",
                AgentReasoningEffort.Low => "Low",
                AgentReasoningEffort.Medium => "Medium",
                AgentReasoningEffort.High => "High",
                AgentReasoningEffort.ExtraHigh => "Extra high",
                AgentReasoningEffort.Max => "Max",
                _ => string.Empty,
            };
            if (Usage is not { } usage)
            {
                return $"{label} reasoning requested";
            }

            return $"{label} reasoning requested · provider reported "
                + $"{usage.ReasoningTokens.ToString(CultureInfo.InvariantCulture)} reasoning tokens";
        }
    }

    public string ReasoningTitle => Usage?.ReasoningTokens > 0
        ? $"Reasoned · {Usage.ReasoningTokens.ToString(CultureInfo.InvariantCulture)} tokens"
        : RequestedReasoningEffort is { } effort
            && effort is not AgentReasoningEffort.Automatic
            ? $"Reasoned · {ReasoningEffortLabel(effort)}"
            : "Reasoning";

    private static string ReasoningEffortLabel(AgentReasoningEffort effort) => effort switch
    {
        AgentReasoningEffort.Off => "Off",
        AgentReasoningEffort.Automatic => "Automatic",
        AgentReasoningEffort.Minimal => "Minimal",
        AgentReasoningEffort.Low => "Low",
        AgentReasoningEffort.Medium => "Medium",
        AgentReasoningEffort.High => "High",
        AgentReasoningEffort.ExtraHigh => "Extra high",
        AgentReasoningEffort.Max => "Max",
        _ => "Reasoning",
    };
}

public sealed record AgentReasoningEffortOption(
    AgentReasoningEffort Value,
    string Label);

public sealed record AgentServiceTierOption(
    AgentServiceTier Value,
    string Label);

public sealed record AgentModelPickerItemViewModel(
    AiProviderModelDescriptor Model,
    string ProviderName,
    bool IsFavorite)
{
    public string Id => Model.Id;

    public string DisplayName => Model.DisplayName;

    public string FavoriteAccessibleName => IsFavorite
        ? $"Remove {DisplayName} from favorite models"
        : $"Add {DisplayName} to favorite models";
}

public sealed record AgentConversationItemViewModel(
    AgentRunId RunId,
    string Title,
    string Model,
    string UpdatedAt,
    bool IsCurrent,
    GovernedAgentConversationAvailability Availability)
{
    public bool IsAvailable =>
        Availability is GovernedAgentConversationAvailability.Available
            or GovernedAgentConversationAvailability.Partial;

    public string Details => Availability switch
    {
        GovernedAgentConversationAvailability.Available => $"{Model} · {UpdatedAt}",
        GovernedAgentConversationAvailability.Partial =>
            $"Partial metadata · {UpdatedAt}",
        GovernedAgentConversationAvailability.Corrupt =>
            $"Corrupt — cannot open or export · {UpdatedAt}",
        GovernedAgentConversationAvailability.Unavailable =>
            $"Temporarily unavailable · {UpdatedAt}",
        _ => $"Unavailable · {UpdatedAt}",
    };
}

public sealed record AgentHistoryRetentionOption(
    string DisplayName,
    int MaximumRuns,
    TimeSpan MaximumAge)
{
    public string Description =>
        $"Keep up to {MaximumRuns} runs for {MaximumAge.TotalDays:N0} days";
}

public sealed record AgentApprovalArgumentViewModel(
    string Name,
    string DisplayValue,
    bool IsSensitive);

public sealed record AgentApprovalCardViewModel(
    AgentApprovalId Id,
    string ToolName,
    string ToolTitle,
    string Risk,
    string Permission,
    string TargetTitle,
    string ExactTarget,
    string Host,
    string WorkingDirectory,
    IReadOnlyList<AgentApprovalArgumentViewModel> Arguments,
    string ExpiresAt,
    bool TemporarilyYieldsTerminalInput)
{
    public bool HasArguments => Arguments.Count > 0;

    public string InputYieldWarning =>
        "Approving temporarily yields terminal input to the agent for this one action. "
        + "Your next physical input preempts it.";
}

public sealed record AgentToolActivityViewModel(
    string ToolName,
    string ToolTitle,
    string Risk,
    string TargetTitle,
    bool CancellationRequested,
    PanelInstanceId? PanelId);

/// <summary>
/// Immutable presentation copy of one bounded, run-local progress report.
/// The message remains untrusted display text and is never promoted to chat
/// history or durable audit evidence.
/// </summary>
public sealed record AgentProgressViewModel
{
    public AgentProgressViewModel(GovernedAgentProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Message = string.Concat(progress.Message);
        Percent = progress.Percent;
        ContentOrigin = progress.ContentOrigin;
    }

    public string Message { get; }

    public int? Percent { get; }

    public string ContentOrigin { get; }

    public bool HasPercent => Percent.HasValue;

    public bool IsIndeterminate => !HasPercent;

    public double ProgressValue => Percent.GetValueOrDefault();

    public string PercentLabel => Percent is { } percent
        ? $"{percent.ToString(CultureInfo.InvariantCulture)}%"
        : string.Empty;

    public string AccessibleName => Percent is { } percent
        ? $"AI agent progress · {Message} · "
            + $"{percent.ToString(CultureInfo.InvariantCulture)} percent"
        : $"AI agent progress · {Message} · in progress";
}

/// <summary>
/// Presentation-only copy of a bounded model clarification. It never
/// participates in approval or authority.
/// </summary>
public sealed record AgentQuestionCardViewModel
{
    public AgentQuestionCardViewModel(GovernedAgentQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        Id = question.Id;
        Question = string.Concat(question.Question);
        ExpiresAt = AgentPresentationTime.Friendly(question.ExpiresAtUtc);
        ContentOrigin = question.ContentOrigin;
    }

    public AgentQuestionId Id { get; }

    public string Question { get; }

    public string ExpiresAt { get; }

    public string ContentOrigin { get; }

    public string AccessibleName =>
        $"AI agent question · {Question} · expires {ExpiresAt}";
}

/// <summary>
/// Presentation-only copy of one authenticated request to change a single
/// run-local capability from Off to Ask. Every visible value originates from
/// trusted runtime metadata; model prose is deliberately absent.
/// </summary>
public sealed record AgentCapabilityRequestCardViewModel(
    AgentCapabilityRequestId Id,
    string DisplayTitle,
    string CapabilityToken,
    string TargetTitle,
    string ExactTarget,
    IReadOnlyList<string> AffectedToolTitles,
    string ExpiresAt)
{
    public string GrantWarning =>
        "Enabling Ask grants no action. Every later terminal, file, browser, "
        + "or process operation still needs its ordinary exact approval.";

    public string AccessibleName =>
        $"AI agent capability request · {DisplayTitle} · Off to Ask for this run · "
        + $"target {TargetTitle} · expires {ExpiresAt}";
}

public sealed record AgentAuditEntryViewModel(
    string Kind,
    string Title,
    string ToolName,
    string Outcome,
    string Evidence,
    string Timeline,
    string Result,
    string TargetBinding,
    string OccurredAt,
    string AccessibleName)
{
    public bool HasToolName => ToolName.Length > 0;

    public bool HasResult => Result.Length > 0;
}

public sealed record AgentContextItemViewModel(
    PanelKind Kind,
    string Title,
    string TabTitle,
    string ExactIdentity,
    string Context,
    string State,
    string Operations,
    string AccessibleName);

public sealed record AgentPolicyCapabilityViewModel(
    string Capability,
    string Permission)
{
    public string AccessibleName => $"{Capability} · {Permission}";
}

public sealed record AgentPolicyComparisonViewModel(
    string Capability,
    string Baseline,
    string Run,
    string Effective)
{
    public bool HasDifference =>
        !string.Equals(Baseline, Run, StringComparison.Ordinal)
        || !string.Equals(Run, Effective, StringComparison.Ordinal);

    public string AccessibleName =>
        $"{Capability} · baseline {Baseline} · run {Run} · effective {Effective}";
}

public sealed record AgentYoloAuthorityViewModel(
    string Scope,
    string Duration,
    string ExpiresAt)
{
    public string Warning =>
        "Agent actions can run without per-action approval. The selected run "
        + "scope, typed host guards, human preemption, stop, and audit remain active.";
}

public sealed class AgentChatViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MinimumStreamingRefreshInterval =
        TimeSpan.FromMilliseconds(33);
    private const string PendingCapabilityNotice =
        "Capabilities are verified against the selected panel scope when you send.";
    private static readonly IReadOnlyList<AgentReasoningEffortOption>
        AllReasoningEffortOptions = Array.AsReadOnly<AgentReasoningEffortOption>(
        [
            new(AgentReasoningEffort.Automatic, "Auto"),
            new(AgentReasoningEffort.Off, "Off"),
            new(AgentReasoningEffort.Minimal, "Minimal"),
            new(AgentReasoningEffort.Low, "Low"),
            new(AgentReasoningEffort.Medium, "Medium"),
            new(AgentReasoningEffort.High, "High"),
            new(AgentReasoningEffort.ExtraHigh, "Extra High"),
            new(AgentReasoningEffort.Max, "Max"),
        ]);
    private static readonly IReadOnlyList<AgentServiceTierOption>
        AllServiceTierOptions = Array.AsReadOnly<AgentServiceTierOption>(
        [
            new(AgentServiceTier.Automatic, "Auto"),
            new(AgentServiceTier.Default, "Standard"),
            new(AgentServiceTier.Flex, "Flex"),
            new(AgentServiceTier.Priority, "Priority"),
        ]);
    private static readonly IReadOnlyList<AgentHistoryRetentionOption>
        AllHistoryRetentionOptions = Array.AsReadOnly<AgentHistoryRetentionOption>(
        [
            new("Compact", 10, TimeSpan.FromDays(30)),
            new("Balanced", 50, TimeSpan.FromDays(90)),
            new("Extended", 200, TimeSpan.FromDays(365)),
        ]);

    private readonly IGovernedAgentRuntime _runtime;
    private readonly IAiProviderProfileRuntime _profiles;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly IAgentRunAuditReader? _auditReader;
    private readonly IAgentModelFavoriteStore? _favoriteStore;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sendGate = new();
    private Task _activeSend = Task.CompletedTask;
    private AiProviderProfileDescriptor? _selectedProvider;
    private AiProviderModelDescriptor? _selectedModel;
    private bool _modelSelectionExplicit;
    private IReadOnlyList<AiProviderModelDescriptor> _models = [];
    private string _modelSearch = string.Empty;
    private string _conversationSearch = string.Empty;
    private bool _isDiscoveringModels;
    private string _modelDiscoveryStatus = string.Empty;
    private long? _contextTokensUsed;
    private readonly HashSet<AgentModelFavorite> _favoriteModels = [];
    private IReadOnlyList<AgentReasoningEffortOption> _reasoningEfforts =
        Array.AsReadOnly([AllReasoningEffortOptions[0]]);
    private AgentReasoningEffortOption _selectedReasoningEffort =
        AllReasoningEffortOptions[0];
    private IReadOnlyList<AgentServiceTierOption> _serviceTiers = [];
    private AgentServiceTierOption _selectedServiceTier = AllServiceTierOptions[0];
    private string _prompt = string.Empty;
    private string _status = string.Empty;
    private string _provisionalAssistantText = string.Empty;
    private string _provisionalReasoningSummary = string.Empty;
    private string _questionResponseDraft = string.Empty;
    private string _targetTitle = "No panel selected";
    private string _exactTarget = string.Empty;
    private string _connectionBoundary = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _capabilityNotice = PendingCapabilityNotice;
    private string _effectivePolicyProvider;
    private string _effectivePolicyModel;
    private long _policyGeneration = 1;
    private GovernedAgentState _state;
    private AgentApprovalCardViewModel? _pendingApproval;
    private AgentQuestionCardViewModel? _pendingQuestion;
    private AgentCapabilityRequestCardViewModel? _pendingCapabilityRequest;
    private AgentToolActivityViewModel? _activeTool;
    private AgentToolActivityViewModel? _panelActivity;
    private AgentProgressViewModel? _currentProgress;
    private AgentYoloAuthorityViewModel? _yoloAuthority;
    private bool _fullAccessSelected;
    private bool _approvalModeInitialized;
    private bool _approvalModeChangePending;
    private AgentRunId? _runId;
    private AgentRunId? _auditRunId;
    private AgentRunAuditCursor? _nextAuditCursor;
    private CancellationTokenSource? _auditCancellation;
    private string _auditStatus =
        "Expand to load recorded actions for this conversation.";
    private AgentPermission _terminalMutationPermission = AgentPermission.Ask;
    private bool _terminalMutationAvailable;
    private bool _runtimeCanSend = true;
    private bool _runtimeCanQueueFollowUp;
    private int _queuedFollowUpCount;
    private bool _runtimeCanStop;
    private bool _isRunBound;
    private bool _runHasTerminal;
    private bool _isContextInspectorExpanded;
    private bool _isAuditExpanded;
    private bool _isAuditLoading;
    private bool _hasActionActivity;
    private bool _actionCancellationInFlight;
    private bool _decisionInFlight;
    private bool _questionResponseInFlight;
    private bool _capabilityDecisionInFlight;
    private bool _policyChangeInFlight;
    private bool _stopInFlight;
    private bool _clearInFlight;
    private bool _disposed;
    private int _refreshPending;
    private int _refreshLoopRunning;
    private AgentPolicy _effectivePolicy;
    private AgentRunHistoryRetention? _historyRetention;
    private AgentHistoryRetentionOption _selectedHistoryRetentionOption =
        AllHistoryRetentionOptions[1];
    private bool _isHistoryRetentionLoading;
    private bool _isHistoryRetentionSaving;
    private bool _isHistoryExporting;
    private string _agentHistoryStatus = "Loading agent history controls…";

    /// <summary>
    /// Raised once when an active turn reaches a terminal presentation state.
    /// The event carries no provider or transcript data; workspace owners use
    /// it only to surface completion while their workspace is in the background.
    /// </summary>
    public event EventHandler? RunFinished;

    public AgentChatViewModel(
        IGovernedAgentRuntime runtime,
        IAiProviderProfileRuntime profiles,
        IUiThreadDispatcher dispatcher,
        IAgentRunAuditReader? auditReader = null,
        IAgentModelFavoriteStore? favoriteStore = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _auditReader = auditReader;
        _favoriteStore = favoriteStore;
        _effectivePolicy = runtime.Snapshot.EffectivePolicy;
        _effectivePolicyProvider = _effectivePolicy.Provider;
        _effectivePolicyModel = _effectivePolicy.Model;
        _runtime.Changed += OnRuntimeChanged;
        _profiles.ProfilesChanged += OnProfilesChanged;
        if (_favoriteStore is not null)
        {
            _favoriteStore.Changed += OnFavoriteModelsChanged;
            _ = LoadFavoriteModelsAsync(_lifetime.Token);
        }

        Refresh();
        _ = RestoreLatestConversationAsync(_lifetime.Token);
        _ = LoadHistoryRetentionAsync(_lifetime.Token);
    }

    private async Task RestoreLatestConversationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.RestoreLatestConversationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public ObservableCollection<AiProviderProfileDescriptor> Providers { get; } = [];

    public ObservableCollection<AgentChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<AgentQueuedFollowUpViewModel> QueuedFollowUps { get; } = [];

    public ObservableCollection<AgentConversationItemViewModel> Conversations { get; } = [];

    public ObservableCollection<AgentConversationItemViewModel> FilteredConversations { get; } = [];

    public ObservableCollection<AgentModelPickerItemViewModel> FilteredModels { get; } = [];

    public bool HasNoModelMatches => FilteredModels.Count == 0;

    public string ConversationSearch
    {
        get => _conversationSearch;
        set
        {
            if (SetProperty(ref _conversationSearch, value ?? string.Empty))
            {
                RefreshFilteredConversations();
            }
        }
    }

    public ObservableCollection<AgentContextItemViewModel> ContextItems { get; } = [];

    public ObservableCollection<AgentPolicyCapabilityViewModel> EffectivePolicyCapabilities
    { get; } = [];

    public ObservableCollection<AgentPolicyComparisonViewModel> PolicyComparison
    { get; } = [];

    public ObservableCollection<AgentAuditEntryViewModel> AuditEntries { get; } = [];

    public ObservableCollection<AgentImageAttachment> PendingImages { get; } = [];

    public IReadOnlyList<AgentHistoryRetentionOption> HistoryRetentionOptions =>
        AllHistoryRetentionOptions;

    public AgentHistoryRetentionOption SelectedHistoryRetentionOption
    {
        get => _selectedHistoryRetentionOption;
        set
        {
            if (SetProperty(ref _selectedHistoryRetentionOption, value))
            {
                OnPropertyChanged(nameof(CanApplyHistoryRetention));
                OnPropertyChanged(nameof(HistoryRetentionEffect));
            }
        }
    }

    public string HistoryRetentionEffect =>
        $"Applying this setting immediately removes older saved conversations "
        + $"outside {SelectedHistoryRetentionOption.MaximumRuns} runs or "
        + $"{SelectedHistoryRetentionOption.MaximumAge.TotalDays:N0} days. "
        + "The current active run is protected.";

    public string AgentHistoryStatus
    {
        get => _agentHistoryStatus;
        private set => SetProperty(ref _agentHistoryStatus, value);
    }

    public bool IsHistoryRetentionLoading
    {
        get => _isHistoryRetentionLoading;
        private set
        {
            if (SetProperty(ref _isHistoryRetentionLoading, value))
            {
                NotifyHistoryAvailabilityChanged();
            }
        }
    }

    public bool IsHistoryRetentionSaving
    {
        get => _isHistoryRetentionSaving;
        private set
        {
            if (SetProperty(ref _isHistoryRetentionSaving, value))
            {
                NotifyHistoryAvailabilityChanged();
            }
        }
    }

    public bool IsHistoryExporting
    {
        get => _isHistoryExporting;
        private set
        {
            if (SetProperty(ref _isHistoryExporting, value))
            {
                NotifyHistoryAvailabilityChanged();
            }
        }
    }

    public bool CanApplyHistoryRetention =>
        _historyRetention is not null
        && !IsHistoryRetentionLoading
        && !IsHistoryRetentionSaving
        && !IsHistoryExporting
        && (_historyRetention.MaximumRuns != SelectedHistoryRetentionOption.MaximumRuns
            || _historyRetention.MaximumAge != SelectedHistoryRetentionOption.MaximumAge);

    public bool CanExportHistory =>
        !IsHistoryRetentionLoading
        && !IsHistoryRetentionSaving
        && !IsHistoryExporting;

    public IReadOnlyList<AiProviderModelDescriptor> Models
    {
        get => _models;
        private set
        {
            if (SetProperty(ref _models, value))
            {
                OnPropertyChanged(nameof(HasMultipleModels));
                OnPropertyChanged(nameof(CanChangeModel));
            }
        }
    }

    public AiProviderModelDescriptor? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (value is not null && !Models.Contains(value))
            {
                throw new ArgumentException(
                    "The model must come from the selected provider's available models.",
                    nameof(value));
            }

            if (SetProperty(ref _selectedModel, value))
            {
                OnPropertyChanged(nameof(SelectedModelName));
                NotifyContextWindowChanged();
                UpdateModelCapabilities(value);
            }
        }
    }

    public string SelectedModelName => SelectedModel?.DisplayName ?? "No model";

    public bool HasContextWindow => SelectedModel?.ContextWindowTokens is not null;

    public long ContextUsedTokens => _contextTokensUsed ?? Messages
        .LastOrDefault(message => message.IsAssistant && message.Usage is not null)
        ?.Usage?.TotalTokens ?? 0;

    public int ContextEffectiveLimit => SelectedModel?.ContextWindowTokens is { } capacity
        ? AgentContextWindowPolicy.EffectiveLimit(capacity)
        : 0;

    public double ContextWindowPercent => ContextEffectiveLimit == 0
        ? 0
        : Math.Clamp(
            ContextUsedTokens * 100d / ContextEffectiveLimit,
            0,
            100);

    public string ContextWindowUsageLabel => HasContextWindow
        ? $"{FormatTokenCount(ContextUsedTokens)} / {FormatTokenCount(ContextEffectiveLimit)} tokens used"
        : string.Empty;

    public string ModelSearch
    {
        get => _modelSearch;
        set
        {
            if (SetProperty(ref _modelSearch, value))
            {
                RefreshFilteredModels();
            }
        }
    }

    public bool IsDiscoveringModels
    {
        get => _isDiscoveringModels;
        private set => SetProperty(ref _isDiscoveringModels, value);
    }

    public string ModelDiscoveryStatus
    {
        get => _modelDiscoveryStatus;
        private set
        {
            if (SetProperty(ref _modelDiscoveryStatus, value))
            {
                OnPropertyChanged(nameof(HasModelDiscoveryStatus));
            }
        }
    }

    public bool HasModelDiscoveryStatus => ModelDiscoveryStatus.Length > 0;

    public bool HasConversationHistory => Conversations.Count > 0;

    public bool HasNoConversationMatches =>
        HasConversationHistory && FilteredConversations.Count == 0;

    public bool HasMultipleModels => Models.Count > 1;

    public IReadOnlyList<AgentReasoningEffortOption> ReasoningEfforts
    {
        get => _reasoningEfforts;
        private set
        {
            if (SetProperty(ref _reasoningEfforts, value))
            {
                OnPropertyChanged(nameof(HasMultipleReasoningEfforts));
            }
        }
    }

    public bool HasMultipleReasoningEfforts => ReasoningEfforts.Count > 1;

    public AgentReasoningEffortOption SelectedReasoningEffort
    {
        get => _selectedReasoningEffort;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!ReasoningEfforts.Contains(value))
            {
                throw new ArgumentException(
                    "The reasoning effort must come from the supported selector options.",
                    nameof(value));
            }

            SetProperty(ref _selectedReasoningEffort, value);
        }
    }

    public IReadOnlyList<AgentServiceTierOption> ServiceTiers
    {
        get => _serviceTiers;
        private set
        {
            if (SetProperty(ref _serviceTiers, value))
            {
                OnPropertyChanged(nameof(HasServiceTiers));
                OnPropertyChanged(nameof(CanSelectServiceTier));
            }
        }
    }

    public bool HasServiceTiers => ServiceTiers.Count > 0;

    public AgentServiceTierOption SelectedServiceTier
    {
        get => _selectedServiceTier;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!ServiceTiers.Contains(value))
            {
                throw new ArgumentException(
                    "The service tier must come from the selected model's supported options.",
                    nameof(value));
            }

            SetProperty(ref _selectedServiceTier, value);
        }
    }

    public bool HasPendingImages => PendingImages.Count > 0;

    public bool CanAttachImages =>
        SelectedProvider?.SupportsImageInput == true
        && State == GovernedAgentState.Ready
        && !_clearInFlight
        && PendingImages.Count < AgentImageAttachment.MaximumPerMessage;

    public string PendingImagesLabel => PendingImages.Count == 1
        ? PendingImages[0].FileName
        : $"{PendingImages.Count.ToString(CultureInfo.InvariantCulture)} images attached";

    public void AddPendingImage(AgentImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (PendingImages.Count >= AgentImageAttachment.MaximumPerMessage)
        {
            throw new InvalidOperationException(
                "At most four images can be attached to one prompt.");
        }

        var totalBytes = PendingImages.Sum(item => (long)item.Content.Length)
            + image.Content.Length;
        if (totalBytes > AgentImageAttachment.MaximumTotalBytesPerMessage)
        {
            throw new InvalidOperationException(
                "The images attached to one prompt exceed the 8 MiB limit.");
        }

        PendingImages.Add(image);
        NotifyPendingImagesChanged();
    }

    public void ClearPendingImages()
    {
        if (PendingImages.Count == 0)
        {
            return;
        }

        PendingImages.Clear();
        NotifyPendingImagesChanged();
    }

    public AiProviderProfileDescriptor? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                _modelSelectionExplicit = false;
                UpdateModels(value, value?.DefaultModel);

                // Provider selection also runs during construction and restore.
                // Keep it side-effect-free: credential-backed network discovery
                // starts only from the explicit Refresh models action.

                OnPropertyChanged(nameof(CanAttachImages));
                NotifyAvailabilityChanged();
            }
        }
    }

    public string Prompt
    {
        get => _prompt;
        set
        {
            if (SetProperty(ref _prompt, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanQueueFollowUp));
                OnPropertyChanged(nameof(CanSubmitPrompt));
                OnPropertyChanged(nameof(ShowPrimaryAction));
                OnPropertyChanged(nameof(ShowStopAction));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(ShowFooterStatus));
            }
        }
    }

    public string ProvisionalAssistantText
    {
        get => _provisionalAssistantText;
        private set
        {
            if (SetProperty(ref _provisionalAssistantText, value))
            {
                OnPropertyChanged(nameof(HasProvisionalAgentContent));
                OnPropertyChanged(nameof(ShowProvisionalReasoningLoader));
                NotifyContentChanged();
            }
        }
    }

    public string ProvisionalReasoningSummary
    {
        get => _provisionalReasoningSummary;
        private set
        {
            if (SetProperty(ref _provisionalReasoningSummary, value))
            {
                OnPropertyChanged(nameof(HasProvisionalReasoningSummary));
                OnPropertyChanged(nameof(ProvisionalReasoningSummaryDisplay));
                OnPropertyChanged(nameof(ProvisionalReasoningStageDisplay));
                OnPropertyChanged(nameof(HasProvisionalAgentContent));
                OnPropertyChanged(nameof(ShowProvisionalReasoningLoader));
                NotifyContentChanged();
            }
        }
    }

    public string ProvisionalReasoningSummaryDisplay =>
        AgentReasoningSummaryPresentation.Format(ProvisionalReasoningSummary);

    public string ProvisionalReasoningStageDisplay =>
        AgentReasoningSummaryPresentation.LatestStage(ProvisionalReasoningSummary);

    public bool HasProvisionalReasoningSummary =>
        ProvisionalReasoningSummary.Length > 0;

    public bool ShowProvisionalReasoningLoader =>
        State == GovernedAgentState.StreamingProvider
        && HasProvisionalReasoningSummary
        && !HasProvisionalAssistantText;

    public bool HasProvisionalAgentContent =>
        HasProvisionalAssistantText || HasProvisionalReasoningSummary;

    public GovernedAgentState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsStreaming));
                OnPropertyChanged(nameof(ShowProvisionalReasoningLoader));
                OnPropertyChanged(nameof(CanCancelActiveAction));
                OnPropertyChanged(nameof(ShowFooterStatus));
                NotifyQuestionAvailabilityChanged();
                NotifyCapabilityRequestAvailabilityChanged();
            }
        }
    }

    public string StateLabel => State switch
    {
        GovernedAgentState.Ready => "Ready",
        GovernedAgentState.StreamingProvider => "Thinking",
        GovernedAgentState.AwaitingUserInput => "Input needed",
        GovernedAgentState.AwaitingCapabilityDecision => "Capability request",
        GovernedAgentState.AwaitingApproval => "Approval",
        GovernedAgentState.RunningTool => "Tool active",
        GovernedAgentState.Cancelling => "Stopping",
        GovernedAgentState.Failed => "Failed",
        GovernedAgentState.Cancelled => "Stopped",
        _ => "Unknown",
    };

    public bool IsBusy => State is
        GovernedAgentState.StreamingProvider
        or GovernedAgentState.AwaitingUserInput
        or GovernedAgentState.AwaitingCapabilityDecision
        or GovernedAgentState.AwaitingApproval
        or GovernedAgentState.RunningTool
        or GovernedAgentState.Cancelling;

    // Preserved for the existing send-button binding name.
    public bool IsStreaming => IsBusy;

    public string TargetTitle
    {
        get => _targetTitle;
        private set
        {
            if (SetProperty(ref _targetTitle, value))
            {
                OnPropertyChanged(nameof(ScopeLabel));
            }
        }
    }

    public string ExactTarget
    {
        get => _exactTarget;
        private set
        {
            if (SetProperty(ref _exactTarget, value))
            {
                OnPropertyChanged(nameof(HasExactTarget));
                OnPropertyChanged(nameof(ScopeLabel));
            }
        }
    }

    public bool HasExactTarget => ExactTarget.Length > 0;

    public string ScopeLabel => HasExactTarget
        ? $"acting in · {TargetTitle}"
        : "Select an active panel";

    public string ConnectionBoundary
    {
        get => _connectionBoundary;
        private set
        {
            if (SetProperty(ref _connectionBoundary, value))
            {
                OnPropertyChanged(nameof(HasTargetContext));
                OnPropertyChanged(nameof(TargetContextLabel));
            }
        }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        private set
        {
            if (SetProperty(ref _workingDirectory, value))
            {
                OnPropertyChanged(nameof(HasTargetContext));
                OnPropertyChanged(nameof(TargetContextLabel));
            }
        }
    }

    public bool HasTargetContext =>
        ConnectionBoundary.Length > 0 || WorkingDirectory.Length > 0;

    public string TargetContextLabel =>
        (ConnectionBoundary.Length > 0, WorkingDirectory.Length > 0) switch
        {
            (true, true) => $"{ConnectionBoundary} · {WorkingDirectory}",
            (true, false) => ConnectionBoundary,
            (false, true) => WorkingDirectory,
            _ => string.Empty,
        };

    public bool HasContextItems => ContextItems.Count > 0;

    public string ContextInspectorSummary
    {
        get
        {
            var terminalCount =
                ContextItems.Count(item => item.Kind == PanelKind.Terminal);
            var browserCount =
                ContextItems.Count(item => item.Kind == PanelKind.Browser);
            var fileCount =
                ContextItems.Count(item => item.Kind == PanelKind.FileViewer);
            var statisticsCount =
                ContextItems.Count(item => item.Kind == PanelKind.Statistics);
            var processMonitorCount =
                ContextItems.Count(item => item.Kind == PanelKind.ProcessMonitor);
            if (terminalCount == ContextItems.Count)
            {
                return terminalCount == 1
                    ? "1 terminal"
                    : $"{terminalCount.ToString(CultureInfo.InvariantCulture)} terminals";
            }

            if (browserCount == ContextItems.Count)
            {
                return browserCount == 1
                    ? "1 browser"
                    : $"{browserCount.ToString(CultureInfo.InvariantCulture)} browsers";
            }

            if (fileCount == ContextItems.Count)
            {
                return fileCount == 1
                    ? "1 File Viewer"
                    : $"{fileCount.ToString(CultureInfo.InvariantCulture)} File Viewers";
            }

            if (statisticsCount == ContextItems.Count)
            {
                return statisticsCount == 1
                    ? "1 Statistics panel"
                    : $"{statisticsCount.ToString(CultureInfo.InvariantCulture)} Statistics panels";
            }

            if (processMonitorCount == ContextItems.Count)
            {
                return processMonitorCount == 1
                    ? "1 Process Monitor"
                    : $"{processMonitorCount.ToString(CultureInfo.InvariantCulture)} Process Monitors";
            }

            return $"{ContextItems.Count.ToString(CultureInfo.InvariantCulture)} panels";
        }
    }

    public string ContextInspectorAccessibleName =>
        $"Inspect agent context · {ContextInspectorSummary}";

    public bool IsContextInspectorExpanded
    {
        get => _isContextInspectorExpanded;
        set => SetProperty(ref _isContextInspectorExpanded, value);
    }

    public bool IsAuditExpanded
    {
        get => _isAuditExpanded;
        set
        {
            if (!SetProperty(ref _isAuditExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRefreshAudit));
            if (value && CanShowAudit && AuditEntries.Count == 0)
            {
                _ = RefreshAuditAsync(_lifetime.Token);
            }
        }
    }

    public bool CanShowAudit =>
        _auditReader is not null
        && _auditRunId is not null;

    internal static bool AuditEvidenceUiEnabled => true;

    public bool HasAuditActivity => CanShowAudit;

    public bool HasAuditEntries => AuditEntries.Count > 0;

    public bool IsAuditLoading
    {
        get => _isAuditLoading;
        private set
        {
            if (SetProperty(ref _isAuditLoading, value))
            {
                OnPropertyChanged(nameof(CanRefreshAudit));
                OnPropertyChanged(nameof(CanLoadOlderAudit));
                OnPropertyChanged(nameof(AuditSummary));
            }
        }
    }

    public string AuditStatus
    {
        get => _auditStatus;
        private set => SetProperty(ref _auditStatus, value);
    }

    public string AuditSummary => IsAuditLoading
        ? "Loading"
        : AuditEntries.Count == 0
            ? "No actions"
            : $"{AuditEntries.Count.ToString(CultureInfo.InvariantCulture)} "
              + (AuditEntries.Count == 1 ? "entry" : "entries");

    public bool CanRefreshAudit =>
        CanShowAudit && IsAuditExpanded && !IsAuditLoading;

    public bool CanLoadOlderAudit =>
        CanRefreshAudit && _nextAuditCursor is not null;

    public string CapabilityNotice
    {
        get => _capabilityNotice;
        private set
        {
            if (SetProperty(ref _capabilityNotice, value))
            {
                OnPropertyChanged(nameof(HasCapabilityNotice));
                OnPropertyChanged(nameof(HasStandingCapabilityNotice));
            }
        }
    }

    /// <summary>
    /// The capability card describes what a run would be allowed to do. With no
    /// provider configured there is no run to describe, and the card only competed
    /// with the empty state that explains how to get one.
    /// </summary>
    public bool HasCapabilityNotice => HasProvider && CapabilityNotice.Length > 0;

    public bool HasStandingCapabilityNotice =>
        HasCapabilityNotice && TerminalMutationAvailable;

    public bool TerminalMutationAvailable
    {
        get => _terminalMutationAvailable;
        private set
        {
            if (SetProperty(ref _terminalMutationAvailable, value))
            {
                OnPropertyChanged(nameof(CapabilityLabel));
                OnPropertyChanged(nameof(HasStandingCapabilityNotice));
            }
        }
    }

    public string CapabilityLabel => TerminalMutationAvailable
        ? "Terminal access"
        : "Capability check";

    public string EffectivePolicyProvider
    {
        get => _effectivePolicyProvider;
        private set
        {
            if (SetProperty(ref _effectivePolicyProvider, value))
            {
                OnPropertyChanged(nameof(EffectivePolicySummary));
            }
        }
    }

    public string EffectivePolicyModel
    {
        get => _effectivePolicyModel;
        private set
        {
            if (SetProperty(ref _effectivePolicyModel, value))
            {
                OnPropertyChanged(nameof(EffectivePolicySummary));
            }
        }
    }

    public string EffectivePolicySummary =>
        $"{EffectivePolicyProvider} · {EffectivePolicyModel}";

    public long PolicyGeneration
    {
        get => _policyGeneration;
        private set
        {
            if (SetProperty(ref _policyGeneration, value))
            {
                OnPropertyChanged(nameof(PolicyComparisonSummary));
            }
        }
    }

    public string PolicyComparisonSummary =>
        $"Baseline → run → effective · generation {PolicyGeneration}";

    public string RendererModeDescription =>
        "AI agent for this workspace";

    public AgentApprovalCardViewModel? PendingApproval
    {
        get => _pendingApproval;
        private set
        {
            if (SetProperty(ref _pendingApproval, value))
            {
                OnPropertyChanged(nameof(HasPendingApproval));
                OnPropertyChanged(nameof(CanDecideApproval));
                NotifyContentChanged();
            }
        }
    }

    public bool HasPendingApproval => PendingApproval is not null;

    public bool CanDecideApproval =>
        PendingApproval is not null
        && State == GovernedAgentState.AwaitingApproval
        && !_decisionInFlight;

    public AgentQuestionCardViewModel? PendingQuestion
    {
        get => _pendingQuestion;
        private set
        {
            if (SetProperty(ref _pendingQuestion, value))
            {
                OnPropertyChanged(nameof(HasPendingQuestion));
                NotifyQuestionAvailabilityChanged();
                NotifyContentChanged();
            }
        }
    }

    public bool HasPendingQuestion => PendingQuestion is not null;

    public string QuestionResponseDraft
    {
        get => _questionResponseDraft;
        set
        {
            if (SetProperty(ref _questionResponseDraft, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSubmitQuestionResponse));
            }
        }
    }

    public bool CanRespondToQuestion =>
        PendingQuestion is not null
        && State == GovernedAgentState.AwaitingUserInput
        && !_questionResponseInFlight;

    public bool CanSubmitQuestionResponse =>
        CanRespondToQuestion
        && !string.IsNullOrWhiteSpace(QuestionResponseDraft);

    public bool CanDeclineQuestion => CanRespondToQuestion;

    public AgentCapabilityRequestCardViewModel? PendingCapabilityRequest
    {
        get => _pendingCapabilityRequest;
        private set
        {
            if (SetProperty(ref _pendingCapabilityRequest, value))
            {
                OnPropertyChanged(nameof(HasPendingCapabilityRequest));
                NotifyCapabilityRequestAvailabilityChanged();
                NotifyContentChanged();
            }
        }
    }

    public bool HasPendingCapabilityRequest =>
        PendingCapabilityRequest is not null;

    public bool CanDecideCapabilityRequest =>
        PendingCapabilityRequest is not null
        && State == GovernedAgentState.AwaitingCapabilityDecision
        && !_capabilityDecisionInFlight;

    public AgentToolActivityViewModel? ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (SetProperty(ref _activeTool, value))
            {
                OnPropertyChanged(nameof(HasActiveTool));
                OnPropertyChanged(nameof(CanCancelActiveAction));
                OnPropertyChanged(nameof(ActiveActionCancellationLabel));
                NotifyContentChanged();
            }
        }
    }

    public bool HasActiveTool => ActiveTool is not null;

    /// <summary>
    /// The last concrete panel used by the active provider turn. Unlike
    /// <see cref="ActiveTool"/>, this remains present while the provider reasons
    /// between panel actions so the panel does not flicker between tool calls.
    /// </summary>
    public AgentToolActivityViewModel? PanelActivity
    {
        get => _panelActivity;
        private set => SetProperty(ref _panelActivity, value);
    }

    public AgentProgressViewModel? CurrentProgress
    {
        get => _currentProgress;
        private set
        {
            if (SetProperty(ref _currentProgress, value))
            {
                OnPropertyChanged(nameof(HasCurrentProgress));
                NotifyContentChanged();
            }
        }
    }

    public bool HasCurrentProgress => CurrentProgress is not null;

    public bool CanCancelActiveAction =>
        State == GovernedAgentState.RunningTool
        && ActiveTool is { CancellationRequested: false }
        && !_actionCancellationInFlight;

    public string ActiveActionCancellationLabel =>
        _actionCancellationInFlight
        || ActiveTool is { CancellationRequested: true }
            ? "Cancelling action…"
            : "Cancel action";

    public AgentYoloAuthorityViewModel? YoloAuthority
    {
        get => _yoloAuthority;
        private set
        {
            if (SetProperty(ref _yoloAuthority, value))
            {
                OnPropertyChanged(nameof(HasYoloAuthority));
                OnPropertyChanged(nameof(CanOfferYolo));
                OnPropertyChanged(nameof(CanEnableYolo));
                OnPropertyChanged(nameof(CanDisableYolo));
                OnPropertyChanged(nameof(PolicyModeLabel));
                NotifyContentChanged();
            }
        }
    }

    public bool HasYoloAuthority => YoloAuthority is not null;

    public bool CanOfferYolo => !_fullAccessSelected;

    public bool CanEnableYolo => !_fullAccessSelected;

    public bool CanDisableYolo => _fullAccessSelected;

    public string PolicyModeLabel => _fullAccessSelected
        ? "Full access"
        : FormatEnum(_terminalMutationPermission);

    public string AccessModeLabel => _fullAccessSelected
        ? "Full access"
        : "Ask approval";

    public bool HasProvider => Providers.Count > 0;

    public bool HasMultipleProviders => Providers.Count > 1;

    public bool HasNoProvider => !HasProvider;

    public bool HasConversation => Messages.Count > 0 || HasProvisionalAgentContent;

    public bool HasAgentContent =>
        HasConversation
        || HasPendingQuestion
        || HasPendingCapabilityRequest
        || HasPendingApproval
        || HasActiveTool
        || HasCurrentProgress
        || HasAuditActivity;

    public bool HasNoConversation => !HasAgentContent;

    public bool HasFailedTurn =>
        State == GovernedAgentState.Failed && !HasConversation;

    public bool ShowFooterStatus => Status.Length > 0 && !HasFailedTurn;

    public string FailureHeading => SelectedModel is null
        ? "The response failed"
        : $"{SelectedModel.DisplayName} couldn't respond";

    public bool CanStartConversation =>
        SelectedProvider is not null
        && State == GovernedAgentState.Ready
        && HasNoConversation
        && !_isRunBound;

    public bool HasProvisionalAssistantText => ProvisionalAssistantText.Length > 0;

    public bool CanSend =>
        SelectedProvider is not null
        && ((_runtimeCanSend && State == GovernedAgentState.Ready)
            || State == GovernedAgentState.Cancelled)
        && !_clearInFlight
        && (!HasPendingImages || SelectedProvider.SupportsImageInput)
        && (!string.IsNullOrWhiteSpace(Prompt) || HasPendingImages);

    public bool CanOfferFollowUpQueue =>
        SelectedProvider is not null
        && _runtimeCanQueueFollowUp
        && !_clearInFlight;

    public bool CanQueueFollowUp =>
        CanOfferFollowUpQueue
        && !string.IsNullOrWhiteSpace(Prompt);

    public int QueuedFollowUpCount => _queuedFollowUpCount;

    public string QueuedFollowUpLabel => QueuedFollowUpCount == 1
        ? "1 follow-up queued"
        : $"{QueuedFollowUpCount.ToString(CultureInfo.InvariantCulture)} follow-ups queued";

    public bool HasQueuedFollowUps => QueuedFollowUpCount > 0;

    public bool CanSubmitPrompt => CanSend || CanQueueFollowUp;

    public bool CanShowPrimaryAction => HasProvider;

    public bool ShowPrimaryAction => CanShowPrimaryAction && !HasFailedTurn;

    public bool ShowStopAction => CanStop;

    public string PrimaryActionLabel => CanOfferFollowUpQueue
        ? "Queue message"
        : "Send";

    public string PrimaryActionAccessibleName =>
        CanOfferFollowUpQueue
            ? "Queue a message for the AI agent"
            : "Send AI agent prompt";

    public string PromptPlaceholder => "Ask Asura…";

    // Preserved as an alias for callers that still use the old chat naming.
    public bool CanCancel => CanStop;

    public bool CanStop => IsBusy;

    public bool CanRequestStop =>
        CanStop && !_stopInFlight && State != GovernedAgentState.Cancelling;

    public bool CanClear =>
        !IsBusy
        && !_clearInFlight
        && (HasConversation
            || _isRunBound
            || State is GovernedAgentState.Failed or GovernedAgentState.Cancelled);

    public bool CanChangeProvider =>
        State == GovernedAgentState.Ready
        && !_isRunBound
        && !HasConversation
        && !_clearInFlight;

    public bool CanBrowseModels =>
        HasProvider
        && !IsBusy
        && !_clearInFlight;

    public bool CanChangeModel => HasMultipleModels && CanBrowseModels;

    public bool CanSelectReasoningEffort =>
        ReasoningEfforts.Count > 1
        && State == GovernedAgentState.Ready
        && !_clearInFlight;

    public bool CanSelectServiceTier =>
        ServiceTiers.Count > 1
        && State == GovernedAgentState.Ready
        && !_clearInFlight;

    public bool CanEnterPrompt =>
        SelectedProvider is not null
        && !_clearInFlight
        && ((_runtimeCanSend && State == GovernedAgentState.Ready)
            || State == GovernedAgentState.Cancelled
            || CanOfferFollowUpQueue);

    public bool NeedsProviderAttention =>
        SelectedProvider is null
        || State is GovernedAgentState.Failed
            or GovernedAgentState.AwaitingUserInput
            or GovernedAgentState.AwaitingCapabilityDecision
            or GovernedAgentState.AwaitingApproval;

    public string ConnectionStatus => SelectedProvider is null
        ? HasProvider && _isRunBound
            ? "Clear required"
            : "Not connected"
        : StateLabel;

    public Task QueueFollowUpAsync(CancellationToken cancellationToken) =>
        QueueFollowUpAsync(
            GovernedAgentFollowUpDelivery.FollowUp,
            cancellationToken);

    public Task QueueSteeringAsync(CancellationToken cancellationToken) =>
        QueueFollowUpAsync(
            GovernedAgentFollowUpDelivery.Steering,
            cancellationToken);

    private async Task QueueFollowUpAsync(
        GovernedAgentFollowUpDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (!CanQueueFollowUp)
        {
            return;
        }

        var followUp = Prompt;
        Prompt = string.Empty;
        try
        {
            var result = await _runtime.QueueFollowUpAsync(
                new GovernedAgentFollowUp(
                    followUp,
                    SelectedReasoningEffort.Value,
                    delivery),
                cancellationToken);
            if (!result.IsAccepted)
            {
                if (string.IsNullOrEmpty(Prompt))
                {
                    Prompt = followUp;
                }

                Status = result.Message;
            }
        }
        catch
        {
            if (string.IsNullOrEmpty(Prompt))
            {
                Prompt = followUp;
            }

            throw;
        }
    }

    private async Task<bool> UpdateQueuedFollowUpAsync(
        AgentQueuedFollowUpViewModel item,
        string message)
    {
        var result = await _runtime.UpdateQueuedFollowUpAsync(
            item.Id,
            new GovernedAgentFollowUp(
                message,
                item.ReasoningEffort,
                item.Delivery),
            _lifetime.Token);
        if (!result.IsAccepted)
        {
            Status = result.Message;
            return false;
        }

        return true;
    }

    private async Task RemoveQueuedFollowUpAsync(
        AgentQueuedFollowUpViewModel item)
    {
        var result = await _runtime.RemoveQueuedFollowUpAsync(
            item.Id,
            _lifetime.Token);
        if (!result.IsAccepted)
        {
            Status = result.Message;
        }
    }

    public async Task MoveQueuedFollowUpAsync(
        AgentQueuedFollowUpViewModel item,
        int destinationIndex)
    {
        var index = QueuedFollowUps.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        var result = await _runtime.MoveQueuedFollowUpAsync(
            item.Id,
            destinationIndex,
            _lifetime.Token);
        if (!result.IsAccepted)
        {
            Status = result.Message;
        }
    }

    private async Task SteerQueuedFollowUpAsync(
        AgentQueuedFollowUpViewModel item)
    {
        var result = await _runtime.SteerQueuedFollowUpAsync(
            item.Id,
            _lifetime.Token);
        if (!result.IsAccepted)
        {
            Status = result.Message;
        }
    }

    public Task SendAsync(
        AgentTarget target,
        AgentPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return SendWithPolicyAsync(target, policy, cancellationToken);
    }

    private Task SendWithPolicyAsync(
        AgentTarget target,
        AgentPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var startsRun = !_isRunBound || State == GovernedAgentState.Cancelled;
        if ((!_runtimeCanSend && State != GovernedAgentState.Cancelled)
            || State is not (GovernedAgentState.Ready or GovernedAgentState.Cancelled)
            || _clearInFlight
            || (string.IsNullOrWhiteSpace(Prompt) && !HasPendingImages))
        {
            return Task.CompletedTask;
        }

        var provider = _modelSelectionExplicit
            ? SelectedProvider
            : Providers.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.Id.Value,
                    policy.Provider,
                    StringComparison.Ordinal));
        if (provider is null)
        {
            ReportTargetUnavailable(
                "The agent policy references an unavailable AI-provider profile.");
            return Task.CompletedTask;
        }

        if (!_modelSelectionExplicit)
        {
            SelectedProvider = provider;
            if (startsRun)
            {
                UpdateModels(provider, policy.Model);
            }
        }

        if (HasPendingImages && !provider.SupportsImageInput)
        {
            ReportTargetUnavailable(
                "The selected AI provider does not support image input.");
            return Task.CompletedTask;
        }

        var prompt = Prompt;
        var images = PendingImages.ToArray();
        var selectedModel = SelectedModel?.Id ?? policy.Model;
        var requestedPolicy = policy.SelectPrimaryModel(
            provider.Id.Value,
            selectedModel);
        var request = new GovernedAgentPrompt(
            provider.Id,
            prompt,
            target,
            images,
            SelectedReasoningEffort.Value,
            SelectedServiceTier.Value,
            requestedPolicy,
            _fullAccessSelected
                ? AgentApprovalMode.FullAccess
                : AgentApprovalMode.Ask);

        Prompt = string.Empty;
        ClearPendingImages();
        lock (_sendGate)
        {
            if (!_activeSend.IsCompleted)
            {
                Prompt = prompt;
                foreach (var image in images)
                {
                    AddPendingImage(image);
                }
                return Task.CompletedTask;
            }

            var embedsApprovalMode = startsRun;
            if (embedsApprovalMode)
            {
                _approvalModeChangePending = false;
            }

            _activeSend = SendCoreAsync(
                request,
                prompt,
                embedsApprovalMode,
                cancellationToken);
            return _activeSend;
        }
    }

    public async Task DecideAsync(bool approved, CancellationToken cancellationToken)
    {
        if (!CanDecideApproval || PendingApproval is not { } approval)
        {
            return;
        }

        _decisionInFlight = true;
        OnPropertyChanged(nameof(CanDecideApproval));
        try
        {
            var result = await _runtime
                .DecideAsync(approval.Id, approved, cancellationToken);
            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _decisionInFlight = false;
            OnPropertyChanged(nameof(CanDecideApproval));
        }
    }

    public Task SubmitQuestionResponseAsync(
        CancellationToken cancellationToken)
    {
        if (!CanSubmitQuestionResponse || PendingQuestion is not { } question)
        {
            return Task.CompletedTask;
        }

        GovernedAgentQuestionResponse.Submitted response;
        try
        {
            response = new GovernedAgentQuestionResponse.Submitted(
                QuestionResponseDraft);
        }
        catch (ArgumentException)
        {
            Status =
                "Enter a printable single-line answer without passwords, tokens, "
                + "private keys, or other credentials.";
            return Task.CompletedTask;
        }

        return RespondToQuestionAsync(question, response, cancellationToken);
    }

    public Task DeclineQuestionAsync(CancellationToken cancellationToken)
    {
        if (!CanDeclineQuestion || PendingQuestion is not { } question)
        {
            return Task.CompletedTask;
        }

        return RespondToQuestionAsync(
            question,
            new GovernedAgentQuestionResponse.Declined(),
            cancellationToken);
    }

    public Task EnableCapabilityAskAsync(CancellationToken cancellationToken)
    {
        if (!CanDecideCapabilityRequest
            || PendingCapabilityRequest is not { } request)
        {
            return Task.CompletedTask;
        }

        return DecideCapabilityRequestAsync(
            request,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            cancellationToken);
    }

    public Task KeepCapabilityOffAsync(CancellationToken cancellationToken)
    {
        if (!CanDecideCapabilityRequest
            || PendingCapabilityRequest is not { } request)
        {
            return Task.CompletedTask;
        }

        return DecideCapabilityRequestAsync(
            request,
            new GovernedAgentCapabilityDecision.KeepOff(),
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!CanStop || _stopInFlight)
        {
            return;
        }

        _stopInFlight = true;
        OnPropertyChanged(nameof(CanRequestStop));
        try
        {
            var result = await _runtime.StopAsync(cancellationToken);
            if (!result.WasRunning)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _stopInFlight = false;
            OnPropertyChanged(nameof(CanRequestStop));
        }
    }

    public async Task CancelActiveActionAsync(
        CancellationToken cancellationToken)
    {
        if (!CanCancelActiveAction)
        {
            return;
        }

        _actionCancellationInFlight = true;
        NotifyActionCancellationChanged();
        try
        {
            var result = await _runtime
                .CancelActiveActionAsync(cancellationToken);
            if (!result.WasRequested)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _actionCancellationInFlight = false;
            NotifyActionCancellationChanged();
        }
    }

    public async Task EnableYoloAsync(
        TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        await SelectApprovalModeAsync(
            fullAccess: true,
            cancellationToken);

    public Task SelectFullAccessAsync(CancellationToken cancellationToken) =>
        SelectApprovalModeAsync(fullAccess: true, cancellationToken);

    public Task SelectAskApprovalAsync(CancellationToken cancellationToken) =>
        SelectApprovalModeAsync(fullAccess: false, cancellationToken);

    public Task DisableYoloAsync(CancellationToken cancellationToken) =>
        SelectAskApprovalAsync(cancellationToken);

    private async Task SelectApprovalModeAsync(
        bool fullAccess,
        CancellationToken cancellationToken)
    {
        _fullAccessSelected = fullAccess;
        _approvalModeChangePending = true;
        NotifyPolicyAvailabilityChanged();
        await ApplySelectedApprovalModeAsync(cancellationToken);
    }

    private async Task ApplySelectedApprovalModeAsync(
        CancellationToken cancellationToken)
    {
        if (!_approvalModeChangePending
            || _policyChangeInFlight
            || !_isRunBound)
        {
            return;
        }

        var fullAccess = _fullAccessSelected;
        _approvalModeChangePending = false;
        _policyChangeInFlight = true;
        NotifyPolicyAvailabilityChanged();
        try
        {
            var result = fullAccess
                ? await _runtime.EnableFullAccessAsync(cancellationToken)
                : await _runtime.DisableYoloAsync(cancellationToken);
            if (!result.IsAccepted)
            {
                if (result.Code is "agent_run_not_bound" or "agent_busy")
                {
                    _approvalModeChangePending = true;
                    Status = result.Message;
                    return;
                }

                _fullAccessSelected = HasYoloAuthority;
                Status = result.Message;
            }
        }
        finally
        {
            _policyChangeInFlight = false;
            NotifyPolicyAvailabilityChanged();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        if (!CanClear || _clearInFlight)
        {
            return;
        }

        _clearInFlight = true;
        NotifyAvailabilityChanged();
        try
        {
            if (!await _runtime.ClearAsync(cancellationToken))
            {
                Status = "The agent run could not be cleared while work is still active.";
            }
        }
        finally
        {
            _clearInFlight = false;
            NotifyAvailabilityChanged();
        }
    }

    public async Task StartNewConversationAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || _clearInFlight)
        {
            return;
        }

        _clearInFlight = true;
        NotifyAvailabilityChanged();
        try
        {
            if (!await _runtime.StartNewConversationAsync(cancellationToken))
            {
                Status = "A new conversation cannot be started while work is active.";
            }
        }
        finally
        {
            _clearInFlight = false;
            NotifyAvailabilityChanged();
        }
    }

    public Task SelectModelAsync(
        AiProviderModelDescriptor model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Models.Contains(model) || !CanBrowseModels)
        {
            return Task.CompletedTask;
        }

        SelectedModel = model;
        _modelSelectionExplicit = true;
        return Task.CompletedTask;
    }

    public async Task ToggleFavoriteModelAsync(
        AgentModelPickerItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (SelectedProvider is not { } provider
            || Models.All(model => !string.Equals(
                model.Id,
                item.Id,
                StringComparison.Ordinal)))
        {
            return;
        }

        var favorite = new AgentModelFavorite(provider.Id, item.Id);
        var shouldFavorite = !item.IsFavorite;
        SetFavorite(favorite, shouldFavorite);
        RefreshFilteredModels();
        if (_favoriteStore is null)
        {
            return;
        }

        var result = await _favoriteStore.SetAsync(
            favorite,
            shouldFavorite,
            cancellationToken);
        if (result.IsSuccess)
        {
            return;
        }

        SetFavorite(favorite, !shouldFavorite);
        RefreshFilteredModels();
        ModelDiscoveryStatus = result.Error?.Message
            ?? "The favorite model could not be saved.";
    }

    public async Task OpenConversationAsync(
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        if (IsBusy || _clearInFlight)
        {
            return;
        }

        _clearInFlight = true;
        NotifyAvailabilityChanged();
        try
        {
            if (!await _runtime.OpenConversationAsync(runId, cancellationToken))
            {
                Status = "The saved conversation could not be opened.";
            }
        }
        finally
        {
            _clearInFlight = false;
            NotifyAvailabilityChanged();
        }
    }

    public async Task ForkConversationAsync(
        AgentConversationForkPoint forkPoint,
        CancellationToken cancellationToken)
    {
        if (IsBusy || _clearInFlight)
        {
            return;
        }

        _clearInFlight = true;
        NotifyAvailabilityChanged();
        try
        {
            if (!await _runtime.ForkConversationAsync(forkPoint, cancellationToken))
            {
                Status = "The conversation could not be forked.";
            }
        }
        finally
        {
            _clearInFlight = false;
            NotifyAvailabilityChanged();
        }
    }

    public async Task DeleteConversationAsync(
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        if (IsBusy || _clearInFlight)
        {
            return;
        }

        _clearInFlight = true;
        NotifyAvailabilityChanged();
        try
        {
            if (!await _runtime.DeleteConversationAsync(runId, cancellationToken))
            {
                Status = "The saved conversation could not be deleted.";
            }
            else
            {
                Status = "Conversation and its retained audit history were deleted.";
            }
        }
        finally
        {
            _clearInFlight = false;
            NotifyAvailabilityChanged();
        }
    }

    public async Task LoadHistoryRetentionAsync(CancellationToken cancellationToken)
    {
        if (IsHistoryRetentionLoading)
        {
            return;
        }

        IsHistoryRetentionLoading = true;
        try
        {
            var result = await _runtime.GetHistoryRetentionAsync(cancellationToken);
            if (!result.IsSuccess || result.Value is not { } retention)
            {
                AgentHistoryStatus = result.Error?.Code
                    == AgentSessionCheckpointStoreErrorCode.CorruptData
                        ? "Agent history controls are unavailable because retained metadata is corrupt."
                        : "Agent history controls are temporarily unavailable.";
                return;
            }

            _historyRetention = retention;
            SelectedHistoryRetentionOption = AllHistoryRetentionOptions
                .FirstOrDefault(option =>
                    option.MaximumRuns == retention.MaximumRuns
                    && option.MaximumAge == retention.MaximumAge)
                ?? new AgentHistoryRetentionOption(
                    "Custom",
                    retention.MaximumRuns,
                    retention.MaximumAge);
            AgentHistoryStatus =
                $"Keeping up to {retention.MaximumRuns} runs for "
                + $"{retention.MaximumAge.TotalDays:N0} days · revision {retention.Revision}.";
        }
        finally
        {
            IsHistoryRetentionLoading = false;
        }
    }

    public async Task ApplyHistoryRetentionAsync(CancellationToken cancellationToken)
    {
        if (!CanApplyHistoryRetention || _historyRetention is not { } current)
        {
            return;
        }

        IsHistoryRetentionSaving = true;
        try
        {
            var option = SelectedHistoryRetentionOption;
            var result = await _runtime.UpdateHistoryRetentionAsync(
                current,
                option.MaximumRuns,
                option.MaximumAge,
                cancellationToken);
            if (!result.IsSuccess || result.Value is not { } updated)
            {
                AgentHistoryStatus = result.Error?.Code switch
                {
                    AgentSessionCheckpointStoreErrorCode.RevisionConflict =>
                        "Agent history retention changed elsewhere. Reload and try again.",
                    AgentSessionCheckpointStoreErrorCode.CorruptData =>
                        "Agent history retention is corrupt and was not changed.",
                    _ => "Agent history retention could not be changed.",
                };
                return;
            }

            _historyRetention = updated;
            AgentHistoryStatus =
                $"Retention applied and older runs pruned · revision {updated.Revision}.";
        }
        finally
        {
            IsHistoryRetentionSaving = false;
        }
    }

    public async ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>>
        ExportHistoryAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (!CanExportHistory)
        {
            return AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.StorageUnavailable,
                    "Another agent history operation is active."));
        }

        IsHistoryExporting = true;
        try
        {
            var result = await _runtime.ExportHistoryAsync(destination, cancellationToken);
            AgentHistoryStatus = result.IsSuccess && result.Value is { } receipt
                ? $"Exported {receipt.RunCount} metadata-only agent runs."
                : result.Error?.Code == AgentSessionCheckpointStoreErrorCode.CorruptData
                    ? "Export stopped: a retained audit trail is incomplete or corrupt."
                    : "Agent history export failed; no destination file was published.";
            return result;
        }
        finally
        {
            IsHistoryExporting = false;
        }
    }

    public void ReportHistoryExportPublicationFailure(bool cleanupUncertain = false)
    {
        AgentHistoryStatus = cleanupUncertain
            ? "Agent history export failed before publication, and its temporary file could not be removed."
            : "Agent history export failed; the existing destination was preserved.";
    }

    public async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        if (SelectedProvider is { } provider)
        {
            await DiscoverModelsAsync(provider.Id, cancellationToken);
        }
    }

    public Task RefreshAuditAsync(CancellationToken cancellationToken) =>
        LoadAuditAsync(replace: true, cancellationToken);

    public Task LoadOlderAuditAsync(CancellationToken cancellationToken) =>
        LoadAuditAsync(replace: false, cancellationToken);

    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        if (CanStop)
        {
            await StopAsync(cancellationToken);
        }

        Task activeSend;
        lock (_sendGate)
        {
            activeSend = _activeSend;
        }

        await activeSend.WaitAsync(cancellationToken);
    }

    public void Cancel()
    {
        if (!_disposed && CanStop)
        {
            _ = StopForDisposalAsync();
        }
    }

    internal void ReportTargetUnavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Status = message;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Changed -= OnRuntimeChanged;
        _profiles.ProfilesChanged -= OnProfilesChanged;
        _favoriteStore?.Changed -= OnFavoriteModelsChanged;
        _auditCancellation?.Cancel();
        _auditCancellation?.Dispose();
        _auditCancellation = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task SendCoreAsync(
        GovernedAgentPrompt request,
        string prompt,
        bool embedsApprovalMode,
        CancellationToken cancellationToken)
    {
        string? failureStatus = null;
        try
        {
            var result = await _runtime.SendAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                var recoverableDrafts = new List<string>();
                if (!result.InitialPromptCommitted && prompt.Length > 0)
                {
                    recoverableDrafts.Add(prompt);
                }

                if (result.RecoverableFollowUps is { Count: > 0 } followUps)
                {
                    recoverableDrafts.AddRange(
                        followUps.Select(followUp => followUp.Message));
                }

                if (recoverableDrafts.Count > 0)
                {
                    var recovered = string.Join(
                        Environment.NewLine + Environment.NewLine,
                        recoverableDrafts);
                    Prompt = string.IsNullOrEmpty(Prompt)
                        ? recovered
                        : Prompt + Environment.NewLine + Environment.NewLine + recovered;
                }

                if (!result.InitialPromptCommitted && PendingImages.Count == 0)
                {
                    foreach (var image in request.Images)
                    {
                        AddPendingImage(image);
                    }
                }

                failureStatus = result.Message;
            }
        }
        finally
        {
            if (embedsApprovalMode)
            {
                QueueRefresh();
            }
        }

        if (failureStatus is not null)
        {
            Status = failureStatus;
        }
    }

    private async Task RespondToQuestionAsync(
        AgentQuestionCardViewModel question,
        GovernedAgentQuestionResponse response,
        CancellationToken cancellationToken)
    {
        _questionResponseInFlight = true;
        NotifyQuestionAvailabilityChanged();
        try
        {
            var result = await _runtime.RespondToQuestionAsync(
                question.Id,
                response,
                cancellationToken);
            if (result.IsAccepted || IsStaleQuestionResponse(result.Code))
            {
                ClearQuestionIfCurrent(question.Id);
                QuestionResponseDraft = string.Empty;
            }

            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _questionResponseInFlight = false;
            NotifyQuestionAvailabilityChanged();
        }
    }

    private async Task DecideCapabilityRequestAsync(
        AgentCapabilityRequestCardViewModel request,
        GovernedAgentCapabilityDecision decision,
        CancellationToken cancellationToken)
    {
        _capabilityDecisionInFlight = true;
        NotifyCapabilityRequestAvailabilityChanged();
        try
        {
            var result = await _runtime.DecideCapabilityRequestAsync(
                request.Id,
                decision,
                cancellationToken);
            if (result.IsAccepted
                || IsStaleCapabilityDecision(result.Code))
            {
                ClearCapabilityRequestIfCurrent(request.Id);
            }

            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _capabilityDecisionInFlight = false;
            NotifyCapabilityRequestAvailabilityChanged();
        }
    }

    private async Task StopForDisposalAsync()
    {
        try
        {
            await _runtime.StopAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueRefresh();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueRefresh();
    }

    private void OnFavoriteModelsChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _ = LoadFavoriteModelsAsync(_lifetime.Token);
    }

    private void QueueRefresh()
    {
        Interlocked.Exchange(ref _refreshPending, 1);
        if (Interlocked.CompareExchange(ref _refreshLoopRunning, 1, 0) == 0)
        {
            _ = DrainRefreshesAsync();
        }
    }

    private async Task DrainRefreshesAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _refreshPending, 0) == 1)
            {
                await _dispatcher.InvokeAsync(Refresh, _lifetime.Token);
                if (_dispatcher.RequiresFramePacing)
                {
                    await Task.Delay(
                        MinimumStreamingRefreshInterval,
                        _lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _refreshLoopRunning, 0);
            if (!_disposed && Volatile.Read(ref _refreshPending) == 1)
            {
                QueueRefresh();
            }
        }
    }

    private void Refresh()
    {
        var snapshot = _runtime.Snapshot;
        var previousState = State;
        var previousQuestionId = PendingQuestion?.Id;
        var selectedId = snapshot.ProviderId ?? SelectedProvider?.Id;
        var selectedModelId = snapshot.ProviderId is not null
            && snapshot.ProviderId == selectedId
                ? snapshot.Model ?? snapshot.EffectivePolicy?.Model
                : selectedId == SelectedProvider?.Id
                    ? SelectedModel?.Id
                    : null;
        var hadProvider = HasProvider;
        Replace(
            Providers,
            _profiles.Profiles
                .Where(profile => profile.IsEnabled)
                .OrderBy(profile => profile.Order)
                .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase));
        if (hadProvider != HasProvider)
        {
            OnPropertyChanged(nameof(HasProvider));
            OnPropertyChanged(nameof(HasNoProvider));
        }
        OnPropertyChanged(nameof(HasMultipleProviders));

        var selectedProvider = Providers.FirstOrDefault(profile => profile.Id == selectedId);
        // A restored transcript has messages and a provider, but no live broker run.
        // Keep approval-mode selection pending until the next prompt creates that run.
        var isRunBound = snapshot.RunId is not null;
        var boundProviderMissing = snapshot.ProviderId is not null
            && isRunBound
            && selectedProvider is null;
        SelectedProvider = selectedProvider
            ?? (boundProviderMissing ? null : Providers.FirstOrDefault());
        UpdateModels(SelectedProvider, selectedModelId);

        _contextTokensUsed = snapshot.ContextTokensUsed;

        Replace(
            Messages,
            snapshot.Messages.Select(message =>
                new AgentChatMessageViewModel(
                    message.Role,
                    message.Content,
                    message.ReasoningSummary,
                    message.Usage,
                    message.Images,
                    message.RequestedReasoningEffort,
                    message.ForkPoint)));
        NotifyContextWindowChanged();
        Replace(
            Conversations,
            (snapshot.Conversations.IsDefault
                    ? []
                    : snapshot.Conversations)
                .Select(item => new AgentConversationItemViewModel(
                    item.RunId,
                    item.Title,
                    item.Model ?? "Model unavailable",
                    AgentPresentationTime.Friendly(item.UpdatedAt),
                    item.RunId == (snapshot.SelectedConversationRunId
                        ?? snapshot.RunId)
                        || item.RunId == _runId && snapshot.HasMessages,
                    item.Availability)));
        RefreshFilteredConversations();
        OnPropertyChanged(nameof(HasConversationHistory));
        Replace(
            ContextItems,
            snapshot.ContextItems.Select(CreateContextItem));
        OnPropertyChanged(nameof(HasContextItems));
        OnPropertyChanged(nameof(ContextInspectorSummary));
        OnPropertyChanged(nameof(ContextInspectorAccessibleName));
        if (!HasContextItems)
        {
            IsContextInspectorExpanded = false;
        }

        ProvisionalAssistantText = snapshot.ProvisionalAssistantText;
        ProvisionalReasoningSummary = snapshot.ProvisionalReasoningSummary;
        CurrentProgress = snapshot.CurrentProgress is { } progress
            ? new AgentProgressViewModel(progress)
            : null;
        State = snapshot.State;
        if (!_questionResponseInFlight
            && previousQuestionId != snapshot.PendingQuestion?.Id)
        {
            QuestionResponseDraft = string.Empty;
        }

        PendingQuestion = snapshot.PendingQuestion is { } question
            ? new AgentQuestionCardViewModel(question)
            : null;
        PendingCapabilityRequest = CreateCapabilityRequest(
            snapshot.PendingCapabilityRequest);
        var selectedConversationRunId = snapshot.SelectedConversationRunId
            ?? snapshot.RunId;
        var auditRunChanged = _auditRunId != selectedConversationRunId;
        if (auditRunChanged)
        {
            _hasActionActivity = false;
            ResetAudit(selectedConversationRunId);
        }

        TargetTitle = snapshot.TargetTitle;
        ExactTarget = FormatTarget(snapshot.Target);
        ConnectionBoundary = snapshot.ConnectionBoundary ?? string.Empty;
        WorkingDirectory = snapshot.WorkingDirectory ?? string.Empty;
        var effectivePolicy = snapshot.EffectivePolicy;
        ArgumentNullException.ThrowIfNull(effectivePolicy);
        _effectivePolicy = effectivePolicy;
        EffectivePolicyProvider = effectivePolicy.Provider;
        EffectivePolicyModel = effectivePolicy.Model;
        Replace(
            EffectivePolicyCapabilities,
            AgentPolicy.Capabilities.Select(capability =>
                new AgentPolicyCapabilityViewModel(
                    AgentPolicyPresentation.CapabilityName(capability),
                    AgentPolicyPresentation.PermissionName(
                        effectivePolicy.GetPermission(capability)))));
        var baselinePolicy = snapshot.BaselinePolicy ?? effectivePolicy;
        var runPolicy = snapshot.RunPolicy ?? baselinePolicy;
        Replace(
            PolicyComparison,
            AgentPolicy.Capabilities.Select(capability =>
                new AgentPolicyComparisonViewModel(
                    AgentPolicyPresentation.CapabilityName(capability),
                    AgentPolicyPresentation.PermissionName(
                        baselinePolicy.GetPermission(capability)),
                    AgentPolicyPresentation.PermissionName(
                        runPolicy.GetPermission(capability)),
                    AgentPolicyPresentation.PermissionName(
                        effectivePolicy.GetPermission(capability)))));
        PolicyGeneration = snapshot.PolicyGeneration;
        TerminalMutationAvailable = snapshot.TerminalMutationAvailable;
        _runHasTerminal = snapshot.ContextItems.Length == 0
            || snapshot.ContextItems.Any(item => item.Kind == PanelKind.Terminal);
        CapabilityNotice = ResolveCapabilityNotice(snapshot);
        PendingApproval = CreateApproval(snapshot.PendingApproval);
        ActiveTool = snapshot.ActiveTool is null
            ? null
            : CreateToolActivity(snapshot.ActiveTool);
        PanelActivity = IsActiveRunState(snapshot.State)
            && snapshot.PanelActivity is not null
                ? CreateToolActivity(snapshot.PanelActivity)
                : null;
        _terminalMutationPermission = snapshot.TerminalMutationPermission;
        if (!_approvalModeInitialized)
        {
            _fullAccessSelected = snapshot.YoloAuthority is not null;
            _approvalModeInitialized = true;
        }

        YoloAuthority = CreateYoloAuthority(snapshot.YoloAuthority);
        if (!_hasActionActivity
            && (snapshot.ActiveTool is not null || snapshot.PendingApproval is not null))
        {
            _hasActionActivity = true;
            OnPropertyChanged(nameof(HasAuditActivity));
        }

        _runId = snapshot.RunId;
        _runtimeCanSend = snapshot.CanSend;
        _runtimeCanQueueFollowUp = snapshot.CanQueueFollowUp;
        if (_queuedFollowUpCount != snapshot.QueuedFollowUpCount)
        {
            _queuedFollowUpCount = snapshot.QueuedFollowUpCount;
            OnPropertyChanged(nameof(QueuedFollowUpCount));
            OnPropertyChanged(nameof(QueuedFollowUpLabel));
            OnPropertyChanged(nameof(HasQueuedFollowUps));
        }
        SynchronizeQueuedFollowUps(
            snapshot.QueuedFollowUps.IsDefault
                ? Array.Empty<GovernedAgentQueuedFollowUp>()
                : snapshot.QueuedFollowUps);
        _runtimeCanStop = snapshot.CanStop;
        _isRunBound = isRunBound;
        Status = boundProviderMissing
            ? "This run's provider is no longer enabled. Clear the run to choose another."
            : snapshot.Status;
        NotifyAvailabilityChanged();
        NotifyContentChanged();
        if (_approvalModeChangePending
            && _isRunBound)
        {
            _ = ApplySelectedApprovalModeAsync(_lifetime.Token);
        }

        if (IsAuditExpanded
            && CanShowAudit
            && (auditRunChanged
                || (previousState != snapshot.State
                    && snapshot.State is
                        GovernedAgentState.Ready
                        or GovernedAgentState.Failed
                        or GovernedAgentState.Cancelled)))
        {
            _ = RefreshAuditAsync(_lifetime.Token);
        }

        if (IsActiveRunState(previousState)
            && snapshot.State is
                GovernedAgentState.Ready
                or GovernedAgentState.Failed
                or GovernedAgentState.Cancelled)
        {
            RunFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsActiveRunState(GovernedAgentState state) => state is
        GovernedAgentState.StreamingProvider
        or GovernedAgentState.AwaitingUserInput
        or GovernedAgentState.AwaitingCapabilityDecision
        or GovernedAgentState.AwaitingApproval
        or GovernedAgentState.RunningTool
        or GovernedAgentState.Cancelling;

    private async Task LoadAuditAsync(
        bool replace,
        CancellationToken cancellationToken)
    {
        if (_auditReader is null
            || _auditRunId is not { } runId
            || IsAuditLoading
            || !IsAuditExpanded
            || (!replace && _nextAuditCursor is null))
        {
            return;
        }

        _auditCancellation?.Dispose();
        _auditCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationCancellation = _auditCancellation;
        var cursor = replace ? null : _nextAuditCursor;
        IsAuditLoading = true;
        AuditStatus = replace
            ? "Loading recorded actions…"
            : "Loading older actions…";

        AuditStoreResult<AgentRunAuditPage>? result = null;
        try
        {
            result = await _auditReader.ReadAsync(
                new AgentRunAuditQuery(runId, cursor),
                operationCancellation.Token);
        }
        catch (OperationCanceledException)
            when (operationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            if (_auditRunId == runId)
            {
                AuditStatus = "Recorded actions are temporarily unavailable.";
            }
        }
        finally
        {
            if (ReferenceEquals(_auditCancellation, operationCancellation))
            {
                _auditCancellation = null;
                operationCancellation.Dispose();
                IsAuditLoading = false;
            }
        }

        if (result is null || _auditRunId != runId)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            AuditStatus = AuditFailureStatus(result.Error!.Code);
            return;
        }

        var page = result.Value!;
        if (replace)
        {
            Replace(
                AuditEntries,
                page.Entries.Select(CreateAuditEntry));
        }
        else
        {
            foreach (var entry in page.Entries)
            {
                AuditEntries.Add(CreateAuditEntry(entry));
            }
        }

        _nextAuditCursor = page.Next;
        NotifyAuditContentChanged();
        AuditStatus = AuditEntries.Count == 0
            ? "No recorded actions in this conversation yet."
            : _nextAuditCursor is null
                ? "Showing all recorded actions."
                : "Showing the newest actions. Older entries are available.";
    }

    private void ResetAudit(AgentRunId? runId)
    {
        _auditCancellation?.Cancel();
        _auditCancellation?.Dispose();
        _auditCancellation = null;
        _auditRunId = runId;
        _nextAuditCursor = null;
        AuditEntries.Clear();
        IsAuditLoading = false;
        if (runId is null)
        {
            IsAuditExpanded = false;
        }

        AuditStatus = runId is null
            ? "Send a message to record agent actions."
            : "Expand to load recorded actions for this conversation.";
        OnPropertyChanged(nameof(CanShowAudit));
        OnPropertyChanged(nameof(HasAuditActivity));
        NotifyAuditContentChanged();
        NotifyContentChanged();
    }

    private static AgentAuditEntryViewModel CreateAuditEntry(
        AgentRunAuditEntry entry) =>
        entry switch
        {
            AgentRunAuditActionEntry action => CreateActionAuditEntry(action),
            AgentRunAuditPolicyEntry policy => CreatePolicyAuditEntry(policy),
            _ => throw new ArgumentOutOfRangeException(
                nameof(entry),
                entry.GetType(),
                "The agent audit entry kind is unsupported."),
        };

    private static AgentAuditEntryViewModel CreateActionAuditEntry(
        AgentRunAuditActionEntry action)
    {
        var title = BuiltInAgentTools.Catalog.TryGet(
                action.ToolName,
                out var descriptor)
            ? descriptor!.Title
            : action.ToolName;
        var outcome = FormatEnum(action.LatestOutcome);
        var authorization = action.AuthorizationSource is { } source
            ? $" · {FormatEnum(source)}"
            : string.Empty;
        var evidence =
            $"{FormatEnum(action.Capability)} · {FormatEnum(action.Permission)}"
            + $" · {FormatEnum(action.Risk)}{authorization}";
        var timeline = string.Join(
            " → ",
            action.Phases.Select(phase => FormatEnum(phase.Outcome)));
        var result = AuditActionResult(action);
        var target = $"Verified target · {ShortDigest(action.TargetIdentity)}";
        var occurred = LocalAuditTime(action.OccurredAtUtc);
        return new AgentAuditEntryViewModel(
            "Action",
            title,
            action.ToolName,
            outcome,
            evidence,
            timeline,
            result,
            target,
            occurred,
            $"{title}; {outcome}; {evidence}; phases {timeline}; "
            + $"{result}; {target}; {occurred}");
    }

    private static AgentAuditEntryViewModel CreatePolicyAuditEntry(
        AgentRunAuditPolicyEntry policy)
    {
        var transition = policy.Transition switch
        {
            AgentRunPolicyTransition.YoloEnabled => "Full access enabled",
            AgentRunPolicyTransition.YoloDisabled => "Full access disabled",
            AgentRunPolicyTransition.YoloExpired => "Full access expired",
            AgentRunPolicyTransition.Updated => "Run policy updated",
            _ => throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.Transition,
                "The policy transition is unsupported."),
        };
        var evidence =
            $"Policy generation {policy.PolicyGeneration.ToString(CultureInfo.InvariantCulture)}";
        var result = policy.YoloExpiresAtUtc is { } expiry
            ? $"Access ends {LocalAuditTime(expiry)}"
            : string.Empty;
        var target = $"Verified target · {ShortDigest(policy.TargetIdentity)}";
        var occurred = LocalAuditTime(policy.OccurredAtUtc);
        return new AgentAuditEntryViewModel(
            "Policy",
            transition,
            string.Empty,
            "Succeeded",
            evidence,
            "Succeeded",
            result,
            target,
            occurred,
            $"{transition}; {evidence}; {result}; {target}; {occurred}");
    }

    private static string AuditActionResult(AgentRunAuditActionEntry action)
    {
        var values = new List<string>(3);
        if (action.ResultCode is { } resultCode)
        {
            values.Add($"Result · {resultCode}");
        }
        else if (action.ErrorCode is { } errorCode)
        {
            values.Add($"Error · {FormatEnum(errorCode)}");
        }

        if (action.ExecutionDurationMilliseconds is { } duration)
        {
            values.Add(
                $"Duration · {duration.ToString(CultureInfo.InvariantCulture)} ms");
        }

        if (action.ResultCount is { } count)
        {
            values.Add(
                $"Count · {count.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(" · ", values);
    }

    private static string ShortDigest(AgentActionDigest digest) =>
        $"{digest.Value[..12]}…";

    private static string LocalAuditTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss zzz",
            CultureInfo.InvariantCulture);

    private static string AuditFailureStatus(AuditStoreErrorCode code) =>
        code switch
        {
            AuditStoreErrorCode.Cancelled =>
                "Loading recorded actions was cancelled.",
            AuditStoreErrorCode.InvalidQuery =>
                "The audit position changed. Refresh the selected run.",
            AuditStoreErrorCode.StorageFailure =>
                "The recorded audit chain is incomplete or corrupt and was hidden.",
            _ => "Recorded actions are temporarily unavailable.",
        };

    private static bool IsStaleQuestionResponse(string code) =>
        code is
            "question_not_found"
            or "question_expired"
            or "question_cancelled"
            or "target_changed";

    private static bool IsStaleCapabilityDecision(string code) =>
        code is
            "capability_request_not_found"
            or "capability_request_expired"
            or "capability_request_cancelled"
            or "capability_request_unavailable"
            or "policy_changed"
            or "target_changed";

    private void ClearQuestionIfCurrent(AgentQuestionId questionId)
    {
        if (PendingQuestion?.Id == questionId)
        {
            PendingQuestion = null;
        }
    }

    private void ClearCapabilityRequestIfCurrent(
        AgentCapabilityRequestId requestId)
    {
        if (PendingCapabilityRequest?.Id == requestId)
        {
            PendingCapabilityRequest = null;
        }
    }

    private void NotifyAuditContentChanged()
    {
        OnPropertyChanged(nameof(HasAuditEntries));
        OnPropertyChanged(nameof(AuditSummary));
        OnPropertyChanged(nameof(CanLoadOlderAudit));
        OnPropertyChanged(nameof(CanRefreshAudit));
    }

    private static AgentContextItemViewModel CreateContextItem(
        GovernedAgentContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var panelKind = item.Kind switch
        {
            PanelKind.Terminal => "terminal",
            PanelKind.Browser => "browser",
            PanelKind.FileViewer => "File Viewer",
            PanelKind.Statistics => "Statistics",
            PanelKind.ProcessMonitor => "Process Monitor",
            _ => "panel",
        };
        var title = item.PanelTitle ?? $"Unnamed {panelKind}";
        var tabTitle = item.TabTitle ?? "Unnamed tab";
        var exactIdentity =
            $"{panelKind} · window/{item.WindowId.Value} · "
            + $"workspace/{item.WorkspaceId.Value} · "
            + $"tab/{item.TabId.Value} · panel/{item.PanelId.Value} · "
            + $"session/{item.SessionId.Value}";
        var context = item.Kind switch
        {
            PanelKind.Browser =>
                "Browser tools are available",
            PanelKind.FileViewer
                when item.FileProviderProfileId is { } providerProfile
                     && item.FileRootDisplay is { } trustedRoot =>
                $"{providerProfile} · trusted root {trustedRoot}",
            PanelKind.FileViewer =>
                "No File Viewer is available",
            PanelKind.Statistics =>
                "Local resource statistics are available",
            PanelKind.ProcessMonitor =>
                "Local processes are available",
            _ => (item.ConnectionBoundary, item.WorkingDirectory) switch
            {
                (not null, not null) =>
                    $"{item.ConnectionBoundary} · {item.WorkingDirectory}",
                (not null, null) => item.ConnectionBoundary,
                (null, not null) => item.WorkingDirectory,
                _ => "Connection and working directory not reported",
            },
        };
        var presence = item.IsFocused
            ? "Focused"
            : item.IsVisible
                ? "Visible"
                : "Background";
        var activeWork = item.HasActiveWork ? " · active work" : string.Empty;
        var state =
            $"{presence} · {FormatEnum(item.Lifecycle)} · "
            + $"{FormatEnum(item.Health)}{activeWork}";
        var operations = item.SupportedOperations.Length == 0
            ? $"No {panelKind} operations"
            : string.Join(" · ", item.SupportedOperations);

        return new AgentContextItemViewModel(
            item.Kind,
            title,
            tabTitle,
            exactIdentity,
            context,
            state,
            operations,
            $"{title}; tab {tabTitle}; {state}; exact identity {exactIdentity}; "
            + $"context {context}; operations {operations}");
    }

    private static string ResolveCapabilityNotice(GovernedAgentSnapshot snapshot)
    {
        var hasTerminal = snapshot.ContextItems.Any(
                item => item.Kind == PanelKind.Terminal)
            || snapshot.ContextItems.Length == 0
            && snapshot.Target is not null;
        var hasBrowser = snapshot.ContextItems.Any(
            item => item.Kind == PanelKind.Browser);
        var hasFiles = snapshot.ContextItems.Any(
            item => item.Kind == PanelKind.FileViewer);
        var hasStatistics = snapshot.ContextItems.Any(
            item => item.Kind == PanelKind.Statistics);
        var hasProcesses = snapshot.ContextItems.Any(
            item => item.Kind == PanelKind.ProcessMonitor);
        var statisticsNotice =
            snapshot.EffectivePolicy.GetPermission(
                AgentCapability.SystemData) == AgentPermission.Off
                ? "Local resource statistics are disabled in this workspace."
                : "Local resource statistics are available.";
        var processNotice =
            snapshot.EffectivePolicy.GetPermission(
                AgentCapability.ProcessData) == AgentPermission.Off
                ? "Process tools are disabled in this workspace."
                : "Local process information is available.";
        var capabilityNotice = !string.IsNullOrWhiteSpace(snapshot.CapabilityNotice)
            ? snapshot.CapabilityNotice
            : hasTerminal && snapshot.TerminalMutationAvailable
                ? "Terminal tools are available."
                : hasTerminal
                    ? PendingCapabilityNotice
                    : hasBrowser
                        ? "Browser tools are available."
                        : hasFiles
                            ? "File tools are limited to the selected File Viewer location."
                            : hasStatistics
                                ? statisticsNotice
                                : hasProcesses
                                    ? processNotice
                                    : PendingCapabilityNotice;
        if (hasBrowser && hasTerminal)
        {
            capabilityNotice += " Browser tools are available.";
        }

        if (hasFiles && (hasTerminal || hasBrowser))
        {
            capabilityNotice +=
                " File tools are limited to the selected File Viewer location.";
        }

        if (hasStatistics && (hasTerminal || hasBrowser || hasFiles))
        {
            capabilityNotice += $" {statisticsNotice}";
        }

        if (hasProcesses
            && (hasTerminal || hasBrowser || hasFiles || hasStatistics))
        {
            capabilityNotice += $" {processNotice}";
        }

        return capabilityNotice;
    }

    private static AgentApprovalCardViewModel? CreateApproval(
        GovernedAgentApproval? approval)
    {
        if (approval is null)
        {
            return null;
        }

        return new AgentApprovalCardViewModel(
            approval.Id,
            approval.ToolName,
            approval.ToolTitle,
            FormatEnum(approval.Risk),
            FormatEnum(approval.Permission),
            approval.Presentation.TargetTitle,
            FormatTarget(approval.Target),
            approval.Presentation.Host ?? "Not reported",
            approval.Presentation.WorkingDirectory ?? "Not reported",
            [.. approval.Presentation.Arguments
                .Select(argument => new AgentApprovalArgumentViewModel(
                    argument.Name,
                    argument.DisplayValue,
                    argument.IsSensitive))],
            AgentPresentationTime.Friendly(approval.ExpiresAtUtc),
            approval.TemporarilyYieldsTerminalInput);
    }

    private static AgentCapabilityRequestCardViewModel? CreateCapabilityRequest(
        GovernedAgentCapabilityRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        return new AgentCapabilityRequestCardViewModel(
            request.Id,
            request.DisplayTitle,
            request.CapabilityToken,
            request.TargetTitle,
            FormatTarget(request.Target),
            [.. request.AffectedToolTitles],
            AgentPresentationTime.Friendly(request.ExpiresAtUtc));
    }

    private static AgentYoloAuthorityViewModel? CreateYoloAuthority(
        GovernedAgentYoloAuthority? authority)
    {
        if (authority is null)
        {
            return null;
        }

        if (authority.ExpiresAtUtc == AgentYoloConfirmation.RunLifetimeExpiry)
        {
            return new AgentYoloAuthorityViewModel(
                FormatTarget(authority.Target),
                "Until changed or the run ends",
                string.Empty);
        }

        var duration = authority.ExpiresAtUtc - authority.ConfirmedAtUtc;
        return new AgentYoloAuthorityViewModel(
            FormatTarget(authority.Target),
            $"{duration.TotalMinutes:0} min window",
            AgentPresentationTime.Friendly(authority.ExpiresAtUtc));
    }

    private static string FormatTarget(AgentTarget? target) =>
        target switch
        {
            null => string.Empty,
            AgentTarget.Panel panel =>
                $"window/{panel.WindowId.Value} · workspace/{panel.WorkspaceId.Value} · "
                + $"tab/{panel.TabId.Value} · panel/{panel.PanelId.Value}",
            AgentTarget.ConnectionSession session =>
                $"session/{session.SessionId.Value}",
            AgentTarget.OpenTab tab =>
                $"window/{tab.WindowId.Value} · workspace/{tab.WorkspaceId.Value} · "
                + $"tab/{tab.TabId.Value}",
            AgentTarget.Workspace workspace =>
                $"window/{workspace.WindowId.Value} · workspace/{workspace.WorkspaceId.Value}",
            AgentTarget.SelectedPanels selected =>
                $"window/{selected.Panels[0].WindowId.Value} · "
                + $"workspace/{selected.Panels[0].WorkspaceId.Value} · panels ["
                + string.Join(
                    ", ",
                    selected.Panels.Select(panel =>
                        $"{panel.TabId.Value}/{panel.PanelId.Value}"))
                + "]",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The agent target kind is not supported."),
        };

    private static string FormatEnum<T>(T value)
        where T : struct, Enum =>
        value.ToString();

    private static string FormatTokenCount(long value)
    {
        if (value < 1_000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var divisor = value >= 1_000_000 ? 1_000_000d : 1_000d;
        var suffix = value >= 1_000_000 ? "m" : "k";
        var scaled = value / divisor;
        var format = scaled >= 100 ? "0" : "0.#";
        return scaled.ToString(format, CultureInfo.InvariantCulture) + suffix;
    }

    private void NotifyContextWindowChanged()
    {
        OnPropertyChanged(nameof(HasContextWindow));
        OnPropertyChanged(nameof(ContextUsedTokens));
        OnPropertyChanged(nameof(ContextEffectiveLimit));
        OnPropertyChanged(nameof(ContextWindowPercent));
        OnPropertyChanged(nameof(ContextWindowUsageLabel));
    }

    private void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanOfferFollowUpQueue));
        OnPropertyChanged(nameof(CanQueueFollowUp));
        OnPropertyChanged(nameof(CanSubmitPrompt));
        OnPropertyChanged(nameof(CanShowPrimaryAction));
        OnPropertyChanged(nameof(ShowPrimaryAction));
        OnPropertyChanged(nameof(ShowStopAction));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(PrimaryActionAccessibleName));
        OnPropertyChanged(nameof(PromptPlaceholder));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRequestStop));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanChangeProvider));
        OnPropertyChanged(nameof(CanBrowseModels));
        OnPropertyChanged(nameof(CanChangeModel));
        OnPropertyChanged(nameof(CanSelectReasoningEffort));
        OnPropertyChanged(nameof(CanSelectServiceTier));
        OnPropertyChanged(nameof(CanAttachImages));
        OnPropertyChanged(nameof(CanEnterPrompt));
        OnPropertyChanged(nameof(CanStartConversation));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(NeedsProviderAttention));
        OnPropertyChanged(nameof(HasCapabilityNotice));
        OnPropertyChanged(nameof(HasStandingCapabilityNotice));
        NotifyPolicyAvailabilityChanged();
        NotifyQuestionAvailabilityChanged();
        NotifyCapabilityRequestAvailabilityChanged();
    }

    private void NotifyHistoryAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanApplyHistoryRetention));
        OnPropertyChanged(nameof(CanExportHistory));
    }

    private static AgentToolActivityViewModel CreateToolActivity(
        GovernedAgentToolActivity activity) =>
        new(
            activity.ToolName,
            activity.ToolTitle,
            FormatEnum(activity.Risk),
            activity.TargetTitle,
            activity.CancellationRequested,
            activity.PanelId);

    private void SynchronizeQueuedFollowUps(
        IReadOnlyList<GovernedAgentQueuedFollowUp> queued)
    {
        GovernedAgentQueuedFollowUp[] desired = queued.Count == 0
            ? []
            : [.. queued];
        var desiredIds = desired.Select(item => item.Id).ToHashSet();
        for (var index = QueuedFollowUps.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(QueuedFollowUps[index].Id))
            {
                QueuedFollowUps.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Length; index++)
        {
            var snapshot = desired[index];
            var existing = QueuedFollowUps.FirstOrDefault(item => item.Id == snapshot.Id);
            if (existing is null)
            {
                existing = new AgentQueuedFollowUpViewModel(
                    snapshot,
                    UpdateQueuedFollowUpAsync,
                    RemoveQueuedFollowUpAsync,
                    SteerQueuedFollowUpAsync);
                QueuedFollowUps.Insert(index, existing);
            }
            else
            {
                existing.Apply(snapshot);
                var currentIndex = QueuedFollowUps.IndexOf(existing);
                if (currentIndex != index)
                {
                    QueuedFollowUps.Move(currentIndex, index);
                }
            }
        }

    }

    private void UpdateModelCapabilities(AiProviderModelDescriptor? model)
    {
        var supported = model?.SupportedReasoningEfforts
            ?? [AgentReasoningEffort.Automatic];
        ReasoningEfforts = Array.AsReadOnly(
            AllReasoningEffortOptions
                .Where(option => supported.Contains(option.Value))
                .ToArray());
        if (ReasoningEfforts.Any(option => option.Value == SelectedReasoningEffort.Value))
        {
            UpdateServiceTiers(model);
            return;
        }

        SelectedReasoningEffort = ReasoningEfforts[0];
        UpdateServiceTiers(model);
    }

    private void UpdateServiceTiers(AiProviderModelDescriptor? model)
    {
        var supported = model?.SupportedServiceTiers ?? [];
        ServiceTiers = Array.AsReadOnly(
            AllServiceTierOptions
                .Where(option => supported.Contains(option.Value))
                .ToArray());
        if (ServiceTiers.Any(option => option.Value == SelectedServiceTier.Value))
        {
            return;
        }

        _selectedServiceTier = ServiceTiers.FirstOrDefault()
            ?? AllServiceTierOptions[0];
        OnPropertyChanged(nameof(SelectedServiceTier));
    }

    private void UpdateModels(
        AiProviderProfileDescriptor? provider,
        string? preferredModelId)
    {
        if (provider is null)
        {
            Models = [];
            SelectedModel = null;
            return;
        }

        var models = provider.Models;
        if (!string.IsNullOrWhiteSpace(preferredModelId)
            && models.All(model => !string.Equals(
                model.Id,
                preferredModelId,
                StringComparison.Ordinal)))
        {
            models = models.Insert(
                0,
                new AiProviderModelDescriptor(preferredModelId, preferredModelId));
        }

        Models = models;
        SelectedModel = models.FirstOrDefault(model => string.Equals(
                model.Id,
                preferredModelId ?? provider.DefaultModel,
                StringComparison.Ordinal))
            ?? models[0];
        RefreshFilteredModels();
    }

    private async Task DiscoverModelsAsync(
        AiProviderProfileId profileId,
        CancellationToken cancellationToken)
    {
        IsDiscoveringModels = true;
        ModelDiscoveryStatus = string.Empty;
        try
        {
            var result = await _profiles.DiscoverModelsAsync(profileId, cancellationToken);
            if (!result.IsSuccess)
            {
                ModelDiscoveryStatus = result.Message;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsDiscoveringModels = false;
        }
    }

    private void RefreshFilteredModels()
    {
        var query = ModelSearch.Trim();
        var provider = SelectedProvider;
        var providerName = provider?.Name ?? "Provider unavailable";
        Replace(
            FilteredModels,
            Models
                .Select(model => new AgentModelPickerItemViewModel(
                    model,
                    providerName,
                    provider is not null && _favoriteModels.Contains(
                        new AgentModelFavorite(provider.Id, model.Id))))
                .Where(item => query.Length == 0
                    || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.ProviderName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.IsFavorite));
        OnPropertyChanged(nameof(HasNoModelMatches));
    }

    private void RefreshFilteredConversations()
    {
        var query = ConversationSearch.Trim();
        Replace(
            FilteredConversations,
            Conversations.Where(item => query.Length == 0
                || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Model.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.UpdatedAt.Contains(query, StringComparison.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(HasNoConversationMatches));
    }

    private async Task LoadFavoriteModelsAsync(CancellationToken cancellationToken)
    {
        if (_favoriteStore is null)
        {
            return;
        }

        try
        {
            var result = await _favoriteStore.ListAsync(cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                return;
            }

            await _dispatcher.InvokeAsync(
                () =>
                {
                    _favoriteModels.Clear();
                    foreach (var favorite in result.Value)
                    {
                        _favoriteModels.Add(favorite);
                    }

                    RefreshFilteredModels();
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void SetFavorite(AgentModelFavorite favorite, bool isFavorite)
    {
        if (isFavorite)
        {
            _favoriteModels.Add(favorite);
        }
        else
        {
            _favoriteModels.Remove(favorite);
        }
    }

    private void NotifyPendingImagesChanged()
    {
        OnPropertyChanged(nameof(HasPendingImages));
        OnPropertyChanged(nameof(PendingImagesLabel));
        OnPropertyChanged(nameof(CanAttachImages));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanSubmitPrompt));
    }

    private void NotifyActionCancellationChanged()
    {
        OnPropertyChanged(nameof(CanCancelActiveAction));
        OnPropertyChanged(nameof(ActiveActionCancellationLabel));
    }

    private void NotifyPolicyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanOfferYolo));
        OnPropertyChanged(nameof(CanEnableYolo));
        OnPropertyChanged(nameof(CanDisableYolo));
        OnPropertyChanged(nameof(PolicyModeLabel));
        OnPropertyChanged(nameof(AccessModeLabel));
    }

    private void NotifyQuestionAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanRespondToQuestion));
        OnPropertyChanged(nameof(CanSubmitQuestionResponse));
        OnPropertyChanged(nameof(CanDeclineQuestion));
    }

    private void NotifyCapabilityRequestAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanDecideCapabilityRequest));
    }

    private void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(HasAgentContent));
        OnPropertyChanged(nameof(HasNoConversation));
        OnPropertyChanged(nameof(CanStartConversation));
        OnPropertyChanged(nameof(HasFailedTurn));
        OnPropertyChanged(nameof(ShowPrimaryAction));
        OnPropertyChanged(nameof(ShowFooterStatus));
        OnPropertyChanged(nameof(FailureHeading));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(HasProvisionalAssistantText));
    }

    private static void Replace<T>(
        ObservableCollection<T> destination,
        IEnumerable<T> source)
    {
        var replacement = source as IReadOnlyList<T> ?? [.. source];
        var sharedPrefixLength = 0;
        var maximumSharedPrefixLength = Math.Min(
            destination.Count,
            replacement.Count);
        while (sharedPrefixLength < maximumSharedPrefixLength
            && EqualityComparer<T>.Default.Equals(
                destination[sharedPrefixLength],
                replacement[sharedPrefixLength]))
        {
            sharedPrefixLength++;
        }

        for (var index = destination.Count - 1;
             index >= sharedPrefixLength;
             index--)
        {
            destination.RemoveAt(index);
        }

        for (var index = sharedPrefixLength;
             index < replacement.Count;
             index++)
        {
            destination.Add(replacement[index]);
        }
    }
}

public sealed class AgentQueuedFollowUpViewModel : ObservableObject
{
    private readonly Func<AgentQueuedFollowUpViewModel, string, Task<bool>> _update;
    private string _message;
    private string _editDraft;
    private AgentReasoningEffort _reasoningEffort;
    private GovernedAgentFollowUpDelivery _delivery;
    private bool _isEditing;

    public AgentQueuedFollowUpViewModel(
        GovernedAgentQueuedFollowUp item,
        Func<AgentQueuedFollowUpViewModel, string, Task<bool>> update,
        Func<AgentQueuedFollowUpViewModel, Task> remove,
        Func<AgentQueuedFollowUpViewModel, Task> steer)
    {
        ArgumentNullException.ThrowIfNull(item);
        Id = item.Id;
        _message = item.Message;
        _editDraft = item.Message;
        _reasoningEffort = item.ReasoningEffort;
        _delivery = item.Delivery;
        _update = update ?? throw new ArgumentNullException(nameof(update));
        DeleteCommand = new AsyncActionCommand(
            () => remove(this),
            () => true);
        SteerCommand = new AsyncActionCommand(
            () => steer(this),
            () => !IsSteering);
        BeginEditCommand = new AsyncActionCommand(
            BeginEditAsync,
            () => !IsEditing);
        SaveEditCommand = new AsyncActionCommand(
            SaveEditAsync,
            () => IsEditing && !string.IsNullOrWhiteSpace(EditDraft));
        CancelEditCommand = new AsyncActionCommand(
            CancelEditAsync,
            () => IsEditing);
    }

    public AgentQueuedFollowUpId Id { get; }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string EditDraft
    {
        get => _editDraft;
        set
        {
            if (SetProperty(ref _editDraft, value ?? string.Empty))
            {
                SaveEditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AgentReasoningEffort ReasoningEffort
    {
        get => _reasoningEffort;
        private set => SetProperty(ref _reasoningEffort, value);
    }

    public GovernedAgentFollowUpDelivery Delivery
    {
        get => _delivery;
        private set
        {
            if (SetProperty(ref _delivery, value))
            {
                OnPropertyChanged(nameof(IsSteering));
                OnPropertyChanged(nameof(SteerLabel));
                SteerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSteering => Delivery == GovernedAgentFollowUpDelivery.Steering;

    public string SteerLabel => IsSteering ? "Next" : "Steer";

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsReadOnly));
                BeginEditCommand.RaiseCanExecuteChanged();
                SaveEditCommand.RaiseCanExecuteChanged();
                CancelEditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsReadOnly => !IsEditing;

    public AsyncActionCommand DeleteCommand { get; }

    public AsyncActionCommand SteerCommand { get; }

    public AsyncActionCommand BeginEditCommand { get; }

    public AsyncActionCommand SaveEditCommand { get; }

    public AsyncActionCommand CancelEditCommand { get; }

    public void Apply(GovernedAgentQueuedFollowUp item)
    {
        if (item.Id != Id)
        {
            throw new ArgumentException(
                "A queued-message presentation cannot change identity.",
                nameof(item));
        }

        Message = item.Message;
        ReasoningEffort = item.ReasoningEffort;
        Delivery = item.Delivery;
        if (!IsEditing)
        {
            EditDraft = item.Message;
        }
    }

    private Task BeginEditAsync()
    {
        EditDraft = Message;
        IsEditing = true;
        return Task.CompletedTask;
    }

    private async Task SaveEditAsync()
    {
        var message = EditDraft.Trim();
        if (message.Length == 0)
        {
            return;
        }

        if (await _update(this, message))
        {
            IsEditing = false;
        }
    }

    private Task CancelEditAsync()
    {
        EditDraft = Message;
        IsEditing = false;
        return Task.CompletedTask;
    }
}

/// <summary>
/// User-facing time formatting for run-local authority: a person deciding a
/// permission reads "2 Jan 2026, 14:34", not a zoned ISO timestamp. Audit
/// evidence keeps its precise form — that surface is a record, not a prompt.
/// </summary>
internal static class AgentPresentationTime
{
    public static string Friendly(DateTimeOffset value) =>
        value.ToLocalTime().ToString(
            "d MMM yyyy, HH:mm",
            CultureInfo.InvariantCulture);
}
