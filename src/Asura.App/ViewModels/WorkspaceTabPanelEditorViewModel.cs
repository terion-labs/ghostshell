using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Edits one durable panel inside a workspace-only tab. Layout slots and durable
/// dependencies are represented as options so missing references remain visible
/// until the user explicitly repairs them.
/// </summary>
public sealed class WorkspaceTabPanelEditorViewModel : ObservableObject
{
    private readonly ScreenPanelDefinition _original;
    private IReadOnlyList<WorkspaceLayoutSlotOption> _slotOptions = [];
    private WorkspaceLayoutSlotOption? _selectedSlot;
    private ScreenConnectionOption? _selectedConnection;
    private ScreenFileProviderOption? _selectedFileProvider;
    private string _title;
    private string _startupLocation;
    private string _startupCommands;
    private StartupCommandDeliveryFailurePolicyOption _selectedDeliveryFailurePolicy;

    public WorkspaceTabPanelEditorViewModel(
        ScreenPanelDefinition panel,
        WorkspaceLayoutOption layout,
        IReadOnlyList<ScreenConnectionOption> connectionOptions,
        IReadOnlyList<ScreenFileProviderOption> fileProviderOptions)
    {
        _original = panel ?? throw new ArgumentNullException(nameof(panel));
        ConnectionOptions = connectionOptions ?? throw new ArgumentNullException(nameof(connectionOptions));
        FileProviderOptions = fileProviderOptions
            ?? throw new ArgumentNullException(nameof(fileProviderOptions));
        _title = panel.Title ?? string.Empty;
        _selectedConnection = panel.ConnectionId is { } connectionId
            ? ConnectionOptions.SingleOrDefault(option => option.Id == connectionId)
            : panel.Kind == ScreenPanelKind.Browser
                ? ConnectionOptions.FirstOrDefault(option =>
                    option.IsAvailable && option.ConnectionKind == ConnectionKind.Local)
                : null;
        FileProviderProfileId? fileProviderId = panel.Kind == ScreenPanelKind.FileViewer
            ? panel.FileProviderProfileId ?? new FileProviderProfileId("builtin.files.home")
            : null;
        _selectedFileProvider = fileProviderId is { } selectedFileProviderId
            ? FileProviderOptions.SingleOrDefault(option => option.Id == selectedFileProviderId)
            : null;
        var startup = panel.Startup ?? PanelStartupBehavior.None;
        _startupLocation = startup.Location ?? string.Empty;
        _startupCommands = IsTerminal
            ? string.Join(Environment.NewLine, startup.Commands)
            : string.Empty;
        _selectedDeliveryFailurePolicy = StartupCommandDeliveryFailurePolicyOption.FromPolicy(
            IsTerminal
                ? startup.DeliveryFailurePolicy
                : StartupCommandDeliveryFailurePolicy.RetryWhileLive);
        UpdateLayout(layout, panel.SlotId);
    }

    public ScreenPanelId Id => _original.Id;

    public ScreenPanelKind Kind => _original.Kind;

    public string KindLabel => KindBadges.Panel(Kind);

    public bool IsTerminal => Kind == ScreenPanelKind.Terminal;

    public bool IsFileViewer => Kind == ScreenPanelKind.FileViewer;

    public bool SupportsLocation => Kind is ScreenPanelKind.Terminal
        or ScreenPanelKind.Browser
        or ScreenPanelKind.FileViewer
        or ScreenPanelKind.DatabaseViewer
        or ScreenPanelKind.Git;

    /// <summary>Browsers use local or SSH routes; databases keep an optional SSH tunnel.</summary>
    public bool SupportsConnection => Kind is ScreenPanelKind.Terminal
        or ScreenPanelKind.Browser
        or ScreenPanelKind.DatabaseViewer
        or ScreenPanelKind.Docker
        or ScreenPanelKind.Git;

    public IReadOnlyList<WorkspaceLayoutSlotOption> SlotOptions => _slotOptions;

    public IReadOnlyList<ScreenConnectionOption> ConnectionOptions { get; }

    public IReadOnlyList<ScreenConnectionOption> ApplicableConnectionOptions =>
        Kind is ScreenPanelKind.Browser or ScreenPanelKind.Docker or ScreenPanelKind.Git
            ? [.. ConnectionOptions
                .Where(option => option.ConnectionKind is ConnectionKind.Local or ConnectionKind.Ssh
                    || !option.IsAvailable && option.Id == _original.ConnectionId)]
            : ConnectionOptions;

    public IReadOnlyList<ScreenFileProviderOption> FileProviderOptions { get; }

    public WorkspaceLayoutSlotOption? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (SetProperty(ref _selectedSlot, value))
            {
                PublishReferenceState();
            }
        }
    }

    public ScreenConnectionOption? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
            {
                PublishReferenceState();
            }
        }
    }

    public ScreenFileProviderOption? SelectedFileProvider
    {
        get => _selectedFileProvider;
        set
        {
            if (SetProperty(ref _selectedFileProvider, value))
            {
                PublishReferenceState();
            }
        }
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string StartupLocation
    {
        get => _startupLocation;
        set => SetProperty(ref _startupLocation, value);
    }

    public string StartupCommands
    {
        get => _startupCommands;
        set => SetProperty(ref _startupCommands, IsTerminal ? value : string.Empty);
    }

    public IReadOnlyList<StartupCommandDeliveryFailurePolicyOption>
        DeliveryFailurePolicyOptions => StartupCommandDeliveryFailurePolicyOption.All;

    public StartupCommandDeliveryFailurePolicyOption SelectedDeliveryFailurePolicy
    {
        get => _selectedDeliveryFailurePolicy;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetProperty(
                ref _selectedDeliveryFailurePolicy,
                IsTerminal
                    ? value
                    : StartupCommandDeliveryFailurePolicyOption.RetryWhileLive);
        }
    }

    public bool HasMissingSlot => SelectedSlot?.IsAvailable != true;

    public bool HasMissingConnection =>
        (Kind is ScreenPanelKind.Terminal
            or ScreenPanelKind.Browser
            or ScreenPanelKind.Docker
            or ScreenPanelKind.Git)
        && (SelectedConnection?.IsAvailable != true
            || Kind is ScreenPanelKind.Browser or ScreenPanelKind.Docker or ScreenPanelKind.Git
                && SelectedConnection.ConnectionKind is not (
                    ConnectionKind.Local or ConnectionKind.Ssh));

    public bool HasMissingFileProvider => IsFileViewer && SelectedFileProvider?.IsAvailable != true;

    public bool HasMissingDefinition =>
        HasMissingSlot || HasMissingConnection || HasMissingFileProvider;

    internal void UpdateLayout(
        WorkspaceLayoutOption layout,
        LayoutSlotId? preferredSlotId = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var selectedId = preferredSlotId ?? SelectedSlot?.Id ?? _original.SlotId;
        var options = layout.Definition?.Slots
            .Select((slot, index) => new WorkspaceLayoutSlotOption(
                slot.Id,
                $"Panel {index + 1} · {slot.Id.Value}",
                true))
            .ToList()
            ?? [];
        if (options.All(option => option.Id != selectedId))
        {
            options.Add(new WorkspaceLayoutSlotOption(selectedId, selectedId.Value, false));
        }

        _slotOptions = options.AsReadOnly();
        _selectedSlot = _slotOptions.Single(option => option.Id == selectedId);
        OnPropertyChanged(nameof(SlotOptions));
        OnPropertyChanged(nameof(SelectedSlot));
        PublishReferenceState();
    }

    internal ScreenPanelDefinition Build()
    {
        var commands = IsTerminal
            ? StartupCommands
                .Split('\n', StringSplitOptions.None)
                .Select(command => command.TrimEnd('\r'))
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .ToArray()
            : [];
        return new ScreenPanelDefinition(
            Id,
            SelectedSlot?.Id ?? _original.SlotId,
            Kind,
            string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
            SupportsConnection ? SelectedConnection?.Id : _original.ConnectionId,
            new PanelStartupBehavior(
                StartupLocation,
                commands,
                IsTerminal
                    ? SelectedDeliveryFailurePolicy.Policy
                    : StartupCommandDeliveryFailurePolicy.RetryWhileLive),
            IsFileViewer ? SelectedFileProvider?.Id : _original.FileProviderProfileId);
    }

    private void PublishReferenceState()
    {
        OnPropertyChanged(nameof(HasMissingSlot));
        OnPropertyChanged(nameof(HasMissingConnection));
        OnPropertyChanged(nameof(HasMissingFileProvider));
        OnPropertyChanged(nameof(HasMissingDefinition));
    }
}
