using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Projects durable AI-provider definitions into bounded connectivity and model-discovery results.
/// HTTP clients, provider payloads, headers, and credential material remain behind this seam.
/// </summary>
public interface IAiProviderProfileRuntime : IDisposable
{
    event EventHandler? ProfilesChanged;

    IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; }

    IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics { get; }

    ValueTask<AiProviderTestResult> TestAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken);

    ValueTask ReloadAsync(CancellationToken cancellationToken);

    ValueTask<AiProviderModelDiscoveryResult> DiscoverModelsAsync(
        AiProviderProfileId profileId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AiProviderModelDiscoveryResult(
            false,
            "ai_provider_model_discovery_unavailable",
            "Model discovery is unavailable for this provider runtime.",
            []));
}

public sealed record AiProviderModelDiscoveryResult(
    bool IsSuccess,
    string Code,
    string Message,
    IReadOnlyList<AiProviderModelDescriptor> Models);

public enum AiProviderRuntimeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum AiProviderRuntimeErrorCode
{
    InvalidConfiguration,
    CredentialUnavailable,
    AuthenticationFailed,
    AccessDenied,
    ModelUnavailable,
    RateLimited,
    QuotaExceeded,
    ProviderUnavailable,
    ProtocolError,
    ResponseTooLarge,
    Timeout,
    Cancelled,
}

public sealed record AiProviderProfileDescriptor
{
    public AiProviderProfileDescriptor(
        AiProviderProfileId Id,
        string Name,
        AiProviderKind ProviderKind,
        Uri Endpoint,
        string DefaultModel,
        int Order,
        bool IsEnabled,
        bool RequiresCredential,
        bool SupportsImageInput = false,
        IReadOnlyList<AgentReasoningEffort>? SupportedReasoningEfforts = null,
        IReadOnlyList<AiProviderModelDescriptor>? Models = null)
    {
        var efforts = SupportedReasoningEfforts is null
            ? ImmutableArray.Create(AgentReasoningEffort.Automatic)
            : [.. SupportedReasoningEfforts];
        if (efforts.IsEmpty
            || efforts[0] != AgentReasoningEffort.Automatic
            || efforts.Any(effort => !Enum.IsDefined(effort))
            || efforts.Distinct().Count() != efforts.Length)
        {
            throw new ArgumentException(
                "Supported reasoning efforts must be unique, valid, and begin with Automatic.",
                nameof(SupportedReasoningEfforts));
        }

        this.Id = Id;
        this.Name = Name;
        this.ProviderKind = ProviderKind;
        this.Endpoint = Endpoint;
        this.DefaultModel = DefaultModel;
        this.Order = Order;
        this.IsEnabled = IsEnabled;
        this.RequiresCredential = RequiresCredential;
        this.SupportsImageInput = SupportsImageInput;
        this.SupportedReasoningEfforts = efforts;
        SupportsReasoning = efforts.Length > 1;

        var models = Models is null
            ? ImmutableArray.Create(new AiProviderModelDescriptor(DefaultModel, DefaultModel))
            : [.. Models];
        if (models.IsEmpty
            || models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count()
                != models.Length
            || models.All(model => !string.Equals(
                model.Id,
                DefaultModel,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Available models must be unique and include the configured default model.",
                nameof(Models));
        }

        this.Models = models;
    }

    public AiProviderProfileId Id { get; }

    public string Name { get; }

    public AiProviderKind ProviderKind { get; }

    public Uri Endpoint { get; }

    public string DefaultModel { get; }

    public int Order { get; }

    public bool IsEnabled { get; }

    public bool RequiresCredential { get; }

    public bool SupportsImageInput { get; }

    public bool SupportsReasoning { get; }

    public ImmutableArray<AgentReasoningEffort> SupportedReasoningEfforts { get; }

    public ImmutableArray<AiProviderModelDescriptor> Models { get; }
}

public sealed record AiProviderModelDescriptor
{
    public AiProviderModelDescriptor(
        string id,
        string displayName,
        IReadOnlyList<AgentReasoningEffort>? supportedReasoningEfforts = null,
        IReadOnlyList<AgentServiceTier>? supportedServiceTiers = null,
        int? contextWindowTokens = null)
    {
        Id = RequireBounded(id, nameof(id));
        DisplayName = RequireBounded(displayName, nameof(displayName));
        SupportedReasoningEfforts = NormalizeReasoningEfforts(
            supportedReasoningEfforts ?? [AgentReasoningEffort.Automatic],
            nameof(supportedReasoningEfforts));
        SupportedServiceTiers = NormalizeServiceTiers(
            supportedServiceTiers ?? [],
            nameof(supportedServiceTiers));
        if (contextWindowTokens is <= 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextWindowTokens),
                "A context window must be a positive bounded token count.");
        }

        ContextWindowTokens = contextWindowTokens;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ImmutableArray<AgentReasoningEffort> SupportedReasoningEfforts { get; }

    public ImmutableArray<AgentServiceTier> SupportedServiceTiers { get; }

    public int? ContextWindowTokens { get; }

    private static ImmutableArray<AgentReasoningEffort> NormalizeReasoningEfforts(
        IEnumerable<AgentReasoningEffort> values,
        string parameterName)
    {
        var normalized = values.ToImmutableArray();
        if (normalized.IsEmpty
            || normalized.Any(value => !Enum.IsDefined(value))
            || normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException(
                "Model capabilities must be non-empty, unique, and valid.",
                parameterName);
        }

        return normalized;
    }

    private static ImmutableArray<AgentServiceTier> NormalizeServiceTiers(
        IEnumerable<AgentServiceTier> values,
        string parameterName)
    {
        var normalized = values.ToImmutableArray();
        if (normalized.Any(value => !Enum.IsDefined(value))
            || normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException(
                "Model service tiers must be unique and valid.",
                parameterName);
        }

        return normalized;
    }

    private static string RequireBounded(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > AiProviderProfile.MaximumModelIdLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The model identifier must be a bounded printable string.",
                parameterName);
        }

        return normalized;
    }
}

public sealed record AiProviderRuntimeDiagnostic(
    AiProviderProfileId? ProfileId,
    AiProviderRuntimeDiagnosticSeverity Severity,
    string Code,
    string Message);

public sealed record AiProviderTestResult(
    bool IsSuccess,
    string Code,
    string Message,
    IReadOnlyList<AiProviderModelDescriptor> Models,
    AiProviderRuntimeErrorCode? ErrorCode = null,
    TimeSpan? RetryAfter = null);
