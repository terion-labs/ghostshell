using System.Collections.Immutable;
using System.ComponentModel;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Edits one optional durable screen-policy override. The available modes omit
/// YOLO because that authority exists only as a confirmed live-run overlay.
/// </summary>
public sealed class SavedScreenAgentPolicyEditorViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<PermissionOption> DurablePermissionOptions =
        Array.AsReadOnly(
        [
            new PermissionOption(AgentPermission.Off),
            new PermissionOption(AgentPermission.Ask),
            new PermissionOption(AgentPermission.Auto),
        ]);

    private readonly IReadOnlyList<CapabilityEditorViewModel> _capabilities;
    private readonly bool _requiresAvailableProvider;
    private bool _isEnabled;
    private string _provider;
    private string _model;
    private ProviderOption? _selectedProvider;
    private IReadOnlyList<ModelOption> _modelOptions = [];
    private ModelOption? _selectedModel;
    private AgentTaskModelOption _selectedCompactionModel;
    private AgentTaskModelOption _selectedTitleModel;
    private string _systemPrompt;
    private bool _disposed;

    public SavedScreenAgentPolicyEditorViewModel(
        AgentPolicy? policy,
        IReadOnlyList<AiProviderProfileDescriptor>? providerProfiles = null)
    {
        _isEnabled = policy is not null;
        var normalized = policy is null
            ? null
            : AgentPolicyResolver.Resolve(policy);
        _provider = normalized?.Provider ?? string.Empty;
        _model = normalized?.Model ?? string.Empty;
        _requiresAvailableProvider = providerProfiles is not null;

        var providerOptions = (providerProfiles ?? [])
            .OrderBy(profile => profile.Order)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Id.Value, StringComparer.Ordinal)
            .Select(profile => new ProviderOption(
                profile.Id,
                profile.Name,
                profile.DefaultModel,
                profile.IsEnabled,
                IsAvailable: true,
                BuildProviderModels(profile)))
            .ToList();
        _selectedProvider = normalized is null
            ? null
            : providerOptions.SingleOrDefault(option =>
                string.Equals(
                    option.Id.Value,
                    normalized.Provider,
                    StringComparison.Ordinal));
        if (normalized is not null
            && _selectedProvider is null
            && _requiresAvailableProvider)
        {
            _selectedProvider = new ProviderOption(
                new AiProviderProfileId(normalized.Provider),
                normalized.Provider,
                normalized.Model,
                IsEnabled: false,
                IsAvailable: false,
                [new ModelOption(normalized.Model, normalized.Model)]);
            providerOptions.Add(_selectedProvider);
        }
        else if (normalized is null && _requiresAvailableProvider)
        {
            _selectedProvider = providerOptions.FirstOrDefault(option => option.IsSelectable);
            if (_selectedProvider is { } selected)
            {
                _provider = selected.Id.Value;
                _model = selected.DefaultModel;
            }
            else
            {
                _provider = string.Empty;
                _model = string.Empty;
            }
        }

        ProviderOptions = providerOptions.AsReadOnly();
        var primarySelection = new AgentModelSelection(_provider, _model);
        var compactionSelection = normalized?.CompactionModel ?? primarySelection;
        var titleSelection = normalized?.TitleModel ?? primarySelection;
        AgentTaskModelOptions = BuildAgentTaskModelOptions(
            providerOptions,
            compactionSelection,
            titleSelection,
            primarySelection);
        TitleModelOptions = BuildTitleModelOptions(
            titleSelection,
            primarySelection);
        _selectedCompactionModel = ResolveAgentTaskModelOption(
            compactionSelection);
        _selectedTitleModel = ResolveTitleModelOption(
            titleSelection);
        _systemPrompt = normalized?.SystemPrompt ?? string.Empty;
        RefreshModelOptions(_model);
        _capabilities = Array.AsReadOnly(
            AgentPolicy.Capabilities
                .Select(capability => new CapabilityEditorViewModel(
                    capability,
                    normalized?.GetPermission(capability)
                        ?? AgentPolicy.InitialPermissions[capability],
                    DurablePermissionOptions))
                .ToArray());
        foreach (var capability in _capabilities)
        {
            capability.PropertyChanged += OnCapabilityChanged;
        }
    }

    public event EventHandler? Changed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Provider
    {
        get => _provider;
        set
        {
            if (SetProperty(ref _provider, value))
            {
                if (_requiresAvailableProvider)
                {
                    _selectedProvider = ProviderOptions.SingleOrDefault(option =>
                        string.Equals(
                            option.Id.Value,
                            value.Trim(),
                            StringComparison.Ordinal));
                    OnPropertyChanged(nameof(SelectedProvider));
                }

                RefreshModelOptions(_model);
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                RefreshModelOptions(value);
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IReadOnlyList<ProviderOption> ProviderOptions { get; }

    public IReadOnlyList<AgentTaskModelOption> AgentTaskModelOptions { get; }

    public IReadOnlyList<AgentTaskModelOption> TitleModelOptions { get; }

    public bool HasSingleTitleModelOption => TitleModelOptions.Count == 1;

    public bool HasMultipleTitleModelOptions => TitleModelOptions.Count > 1;

    public string SystemPrompt
    {
        get => _systemPrompt;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _systemPrompt, normalized))
            {
                OnPropertyChanged(nameof(SystemPromptUsage));
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string SystemPromptUsage =>
        $"{SystemPrompt.Length} / {AgentPolicy.MaximumSystemPromptLength}";

    public AgentTaskModelOption SelectedCompactionModel
    {
        get => _selectedCompactionModel;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!AgentTaskModelOptions.Contains(value))
            {
                throw new ArgumentException(
                    "Choose a configured model for conversation compaction.",
                    nameof(value));
            }

            if (SetProperty(ref _selectedCompactionModel, value))
            {
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public AgentTaskModelOption SelectedTitleModel
    {
        get => _selectedTitleModel;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!TitleModelOptions.Contains(value))
            {
                throw new ArgumentException(
                    "Choose a configured model for conversation titles.",
                    nameof(value));
            }

            if (SetProperty(ref _selectedTitleModel, value))
            {
                OnPropertyChanged(nameof(IsValid));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasSingleProviderOption => ProviderOptions.Count == 1;

    public bool HasMultipleProviderOptions => ProviderOptions.Count > 1;

    public IReadOnlyList<ModelOption> ModelOptions => _modelOptions;

    public bool HasSingleModelOption => ModelOptions.Count == 1;

    public bool HasMultipleModelOptions => ModelOptions.Count > 1;

    public ModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            // Replacing ItemsSource makes ComboBox briefly write null back.
            // There is no empty choice: clearing an enabled policy's model is
            // not a user action offered by this picker.
            if (value is null && ModelOptions.Count > 0)
            {
                return;
            }
            if (value is not null && !ModelOptions.Contains(value))
            {
                throw new ArgumentException(
                    "Choose a model offered by the selected provider profile.",
                    nameof(value));
            }

            if (!SetProperty(ref _selectedModel, value))
            {
                return;
            }

            _model = value?.Id ?? string.Empty;
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(IsValid));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public ProviderOption? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (value is null && ProviderOptions.Count > 0)
            {
                return;
            }
            if (value is not null && !ProviderOptions.Contains(value))
            {
                throw new ArgumentException(
                    "Choose a provider profile offered by this editor.",
                    nameof(value));
            }

            if (!SetProperty(ref _selectedProvider, value))
            {
                return;
            }

            _provider = value?.Id.Value ?? string.Empty;
            _model = value?.DefaultModel ?? string.Empty;
            RefreshModelOptions(_model);
            OnPropertyChanged(nameof(Provider));
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(IsValid));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<CapabilityEditorViewModel> Capabilities => _capabilities;

    public bool IsValid =>
        !IsEnabled
        || AgentPolicy.IsValidProvider(Provider)
        && AgentPolicy.IsValidModel(Model)
        && AgentTaskModelOptions.Contains(SelectedCompactionModel)
        && TitleModelOptions.Contains(SelectedTitleModel)
        && IsConfiguredRouteAvailable(SelectedCompactionModel.Selection)
        && IsConfiguredRouteAvailable(SelectedTitleModel.Selection)
        && (string.IsNullOrWhiteSpace(SystemPrompt)
            || AgentPolicy.IsValidSystemPrompt(SystemPrompt))
        && (!_requiresAvailableProvider
            || SelectedProvider is { IsSelectable: true } selected
            && string.Equals(
                selected.Id.Value,
                Provider.Trim(),
                StringComparison.Ordinal));

    public AgentPolicy? Build()
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (!IsValid)
        {
            throw new ArgumentException(
                "An enabled saved-screen agent policy requires an available, enabled "
                + "provider profile and a valid model name.");
        }

        var policy = new AgentPolicy(
            Provider.Trim(),
            Model.Trim(),
            Capabilities.ToImmutableDictionary(
                capability => capability.Capability,
                capability => capability.SelectedPermission))
        {
            CompactionModel = SelectedCompactionModel.Selection,
            TitleModel = SelectedTitleModel.Selection,
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt)
                ? null
                : SystemPrompt.Trim(),
        };
        if (!policy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "Saved-screen agent policy permissions must use Off, Ask, or Auto.");
        }

        return policy;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var capability in _capabilities)
        {
            capability.PropertyChanged -= OnCapabilityChanged;
        }

        _disposed = true;
    }

    private void RefreshModelOptions(string model)
    {
        IReadOnlyList<ModelOption> providerModels = _selectedProvider?.Models ?? [];
        List<ModelOption> options = [.. providerModels];
        var selected = options.SingleOrDefault(option =>
            string.Equals(option.Id, model.Trim(), StringComparison.Ordinal));
        if (selected is null && AgentPolicy.IsValidModel(model))
        {
            selected = new ModelOption(model.Trim(), model.Trim());
            options.Add(selected);
        }

        _modelOptions = options.AsReadOnly();
        _selectedModel = selected;
        OnPropertyChanged(nameof(ModelOptions));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(HasSingleModelOption));
        OnPropertyChanged(nameof(HasMultipleModelOptions));
    }

    private static IReadOnlyList<AgentTaskModelOption> BuildAgentTaskModelOptions(
        IReadOnlyList<ProviderOption> providers,
        params AgentModelSelection[] configuredSelections)
    {
        List<AgentTaskModelOption> options =
        [
            ..
            providers
                .Where(provider => provider.IsSelectable)
                .SelectMany(provider => provider.Models.Select(model =>
                    new AgentTaskModelOption(
                        new AgentModelSelection(provider.Id.Value, model.Id),
                        model.DisplayName,
                        provider.Name))),
        ];
        foreach (var selection in configuredSelections)
        {
            if (options.Any(option => option.Selection == selection))
            {
                continue;
            }

            var provider = providers.SingleOrDefault(option =>
                string.Equals(
                    option.Id.Value,
                    selection.Provider,
                    StringComparison.Ordinal));
            options.Add(new AgentTaskModelOption(
                selection,
                selection.Model,
                provider?.Name ?? selection.Provider));
        }

        return options.AsReadOnly();
    }

    private static IReadOnlyList<ModelOption> BuildProviderModels(
        AiProviderProfileDescriptor profile)
    {
        List<ModelOption> models = [.. profile.Models.Select(model => new ModelOption(model.Id, model.DisplayName))];
        if (AgentPolicy.IsValidModel(profile.DefaultModel)
            && models.All(model => !string.Equals(
                model.Id,
                profile.DefaultModel,
                StringComparison.Ordinal)))
        {
            models.Insert(0, new ModelOption(profile.DefaultModel, profile.DefaultModel));
        }

        return models.AsReadOnly();
    }

    private IReadOnlyList<AgentTaskModelOption> BuildTitleModelOptions(
        AgentModelSelection configuredSelection,
        AgentModelSelection currentSelection)
    {
        List<AgentTaskModelOption> options =
        [.. AgentTaskModelOptions.Where(option => option.Selection is not null)];
        AddExactTitleModelOption(options, configuredSelection);
        AddExactTitleModelOption(options, currentSelection);
        return options.AsReadOnly();
    }

    private void AddExactTitleModelOption(
        List<AgentTaskModelOption> options,
        AgentModelSelection? selection)
    {
        if (selection is null
            || !AgentPolicy.IsValidProvider(selection.Provider)
            || !AgentPolicy.IsValidModel(selection.Model)
            || options.Any(option => option.Selection == selection))
        {
            return;
        }

        var provider = ProviderOptions.SingleOrDefault(option =>
            string.Equals(option.Id.Value, selection.Provider, StringComparison.Ordinal));
        if (_requiresAvailableProvider && provider is not { IsSelectable: true })
        {
            return;
        }

        options.Add(new AgentTaskModelOption(
            selection,
            selection.Model,
            provider?.Name ?? selection.Provider));
    }

    private AgentTaskModelOption ResolveAgentTaskModelOption(
        AgentModelSelection selection,
        IReadOnlyList<AgentTaskModelOption>? availableOptions = null)
    {
        var options = availableOptions ?? AgentTaskModelOptions;
        var option = options.FirstOrDefault(candidate =>
            candidate.Selection == selection);
        return option ?? throw new ArgumentException(
            "The configured agent model route is not present in the model catalog.",
            nameof(selection));
    }

    private AgentTaskModelOption ResolveTitleModelOption(
        AgentModelSelection selection)
    {
        var option = TitleModelOptions.FirstOrDefault(candidate =>
            candidate.Selection == selection);
        return option ?? throw new ArgumentException(
            "The configured title model route is not present in the model catalog.",
            nameof(selection));
    }

    private bool IsConfiguredRouteAvailable(AgentModelSelection selection)
    {
        if (!_requiresAvailableProvider)
        {
            return true;
        }

        return ProviderOptions.Any(provider =>
            provider.IsSelectable
            && string.Equals(
                provider.Id.Value,
                selection.Provider,
                StringComparison.Ordinal));
    }

    private void OnCapabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public sealed record PermissionOption(AgentPermission Permission)
    {
        public string DisplayName => AgentPolicyPresentation.PermissionName(Permission);
    }

    public sealed record ProviderOption(
        AiProviderProfileId Id,
        string Name,
        string DefaultModel,
        bool IsEnabled,
        bool IsAvailable,
        IReadOnlyList<ModelOption> Models)
    {
        public bool IsSelectable => IsAvailable && IsEnabled;

        public string DisplayName => this switch
        {
            { IsAvailable: false } => $"Unavailable · {Id.Value}",
            { IsEnabled: false } => $"Disabled · {Name}",
            _ => Name,
        };
    }

    public sealed record ModelOption(string Id, string Name)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }

    public sealed record AgentTaskModelOption(
        AgentModelSelection Selection,
        string ModelName,
        string ProviderName)
    {
        public string DisplayName => ProviderName.Length == 0
            ? ModelName
            : $"{ModelName} · {ProviderName}";
    }

    public sealed class CapabilityEditorViewModel : ObservableObject
    {
        private PermissionOption _selectedOption;

        internal CapabilityEditorViewModel(
            AgentCapability capability,
            AgentPermission permission,
            IReadOnlyList<PermissionOption> options)
        {
            Capability = capability;
            Options = options;
            _selectedOption = options.Single(option => option.Permission == permission);
        }

        public AgentCapability Capability { get; }

        public string DisplayName => AgentPolicyPresentation.CapabilityName(Capability);

        public IReadOnlyList<PermissionOption> Options { get; }

        public PermissionOption SelectedOption
        {
            get => _selectedOption;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (!Options.Contains(value))
                {
                    throw new ArgumentException(
                        "The selected permission cannot be saved.",
                        nameof(value));
                }

                if (SetProperty(ref _selectedOption, value))
                {
                    OnPropertyChanged(nameof(SelectedPermission));
                    OnPropertyChanged(nameof(IsOff));
                    OnPropertyChanged(nameof(IsAsk));
                    OnPropertyChanged(nameof(IsAuto));
                }
            }
        }

        public AgentPermission SelectedPermission
        {
            get => SelectedOption.Permission;
            set => SelectedOption = Options.Single(option => option.Permission == value);
        }

        public string Description =>
            AgentPolicyPresentation.CapabilityDescription(Capability);

        public bool IsOff
        {
            get => SelectedPermission == AgentPermission.Off;
            set
            {
                if (value)
                {
                    SelectedPermission = AgentPermission.Off;
                }
            }
        }

        public bool IsAsk
        {
            get => SelectedPermission == AgentPermission.Ask;
            set
            {
                if (value)
                {
                    SelectedPermission = AgentPermission.Ask;
                }
            }
        }

        public bool IsAuto
        {
            get => SelectedPermission == AgentPermission.Auto;
            set
            {
                if (value)
                {
                    SelectedPermission = AgentPermission.Auto;
                }
            }
        }
    }
}
