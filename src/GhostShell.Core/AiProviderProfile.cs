using System.Net;
using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// Durable model-provider configuration. Credential values are represented only by an opaque
/// vault reference and are resolved by the provider adapter for the lifetime of one request.
/// </summary>
public sealed record AiProviderProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumNameLength = 128;
    public const int MaximumModelIdLength = 256;
    public const int MaximumDiscoveredModels = 4 * 1024;
    public const int MaximumEndpointLength = 2_048;
    public const int MaximumOrder = 10_000;

    public AiProviderProfile(
        AiProviderProfileId id,
        int schemaVersion,
        string name,
        AiProviderKind providerKind,
        Uri endpoint,
        AiProviderAuthentication authentication,
        string defaultModel,
        int order,
        bool isEnabled = true,
        IReadOnlyList<string>? discoveredModelIds = null)
        : this(
            id,
            schemaVersion,
            name,
            providerKind,
            endpoint,
            authentication,
            defaultModel,
            order,
            isEnabled,
            AiProviderCatalog.Get(providerKind).Protocol,
            capabilities: null,
            discoveredModelIds)
    {
    }

    [JsonConstructor]
    public AiProviderProfile(
        AiProviderProfileId id,
        int schemaVersion,
        string name,
        AiProviderKind providerKind,
        Uri endpoint,
        AiProviderAuthentication authentication,
        string defaultModel,
        int order,
        bool isEnabled,
        AiProviderProtocol protocol,
        AiProviderCapabilities? capabilities,
        IReadOnlyList<string>? discoveredModelIds = null)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The AI-provider schema version is not supported.");
        }

        if (!Enum.IsDefined(providerKind))
        {
            throw new ArgumentOutOfRangeException(nameof(providerKind), providerKind, null);
        }

        var definition = AiProviderCatalog.Get(providerKind);
        if (!Enum.IsDefined(protocol) || protocol != definition.Protocol)
        {
            throw new ArgumentException(
                "The provider protocol does not match its registered identity.",
                nameof(protocol));
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RequirePrintable(name, nameof(name), MaximumNameLength);
        ProviderKind = providerKind;
        Protocol = protocol;
        Endpoint = NormalizeEndpoint(endpoint);
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        if (Authentication is AiProviderAuthentication.None && !IsLoopback(Endpoint))
        {
            throw new ArgumentException(
                "A provider without authentication must use an exact loopback endpoint.",
                nameof(authentication));
        }

        if (Authentication is not AiProviderAuthentication.None
            && !SupportsAuthentication(definition.AuthenticationMethods, Authentication))
        {
            throw new ArgumentException(
                "The authentication method is not supported by this provider identity.",
                nameof(authentication));
        }

        if (capabilities is { } requestedCapabilities
            && !IsCapabilitySubset(requestedCapabilities, definition.Capabilities))
        {
            throw new ArgumentException(
                "Profile capabilities cannot exceed the provider identity ceiling.",
                nameof(capabilities));
        }

        Capabilities = capabilities ?? definition.Capabilities;

        DefaultModel = RequirePrintable(
            defaultModel,
            nameof(defaultModel),
            MaximumModelIdLength);
        if (discoveredModelIds is { Count: > MaximumDiscoveredModels })
        {
            throw new ArgumentException("The discovered model list is too large.", nameof(discoveredModelIds));
        }

        DiscoveredModelIds = Array.AsReadOnly((discoveredModelIds ?? [])
            .Select(model => RequirePrintable(model, nameof(discoveredModelIds), MaximumModelIdLength))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
        if (order is < 0 or > MaximumOrder)
        {
            throw new ArgumentOutOfRangeException(
                nameof(order),
                order,
                $"Provider order must be between 0 and {MaximumOrder}.");
        }

        Order = order;
        IsEnabled = isEnabled;
    }

    public static DefinitionKind Kind => DefinitionKind.AiProviderProfile;

    public AiProviderProfileId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public AiProviderKind ProviderKind { get; }

    /// <summary>Provider identity used by runtime consumers.</summary>
    [JsonIgnore]
    public AiProviderKind Identity => ProviderKind;

    public AiProviderProtocol Protocol { get; }

    public Uri Endpoint { get; }

    public AiProviderAuthentication Authentication { get; }

    public AiProviderCapabilities Capabilities { get; }

    public string DefaultModel { get; }

    public IReadOnlyList<string> DiscoveredModelIds { get; }

    public int Order { get; }

    public bool IsEnabled { get; }

    public static Uri DefaultEndpoint(AiProviderKind providerKind) =>
        AiProviderCatalog.Get(providerKind).DefaultEndpoint;

    private static bool SupportsAuthentication(
        AiProviderAuthenticationMethod supported,
        AiProviderAuthentication authentication)
    {
        var requested = authentication switch
        {
            AiProviderAuthentication.None =>
                AiProviderAuthenticationMethod.NoAuthentication,
            AiProviderAuthentication.ApiKey => AiProviderAuthenticationMethod.ApiKey,
            AiProviderAuthentication.OAuth { Flow: AiProviderOAuthFlow.Browser } =>
                AiProviderAuthenticationMethod.OAuthBrowser,
            AiProviderAuthentication.OAuth { Flow: AiProviderOAuthFlow.Device } =>
                AiProviderAuthenticationMethod.OAuthDevice,
            AiProviderAuthentication.AwsCredentialChain =>
                AiProviderAuthenticationMethod.AwsCredentialChain,
            _ => AiProviderAuthenticationMethod.None,
        };
        return requested != AiProviderAuthenticationMethod.None
            && supported.HasFlag(requested);
    }

    private static bool IsCapabilitySubset(
        AiProviderCapabilities requested,
        AiProviderCapabilities ceiling) =>
        (!requested.SupportsToolCalling || ceiling.SupportsToolCalling)
        && (!requested.SupportsToolBatches || ceiling.SupportsToolBatches)
        && (!requested.SupportsImageInput || ceiling.SupportsImageInput)
        && (!requested.SupportsReasoning || ceiling.SupportsReasoning)
        && (!requested.SupportsModelDiscovery || ceiling.SupportsModelDiscovery);

    private static Uri NormalizeEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("http" or "https")
            || endpoint.AbsoluteUri.Length > MaximumEndpointLength)
        {
            throw new ArgumentException(
                "The provider endpoint must be a bounded absolute HTTP(S) URI.",
                nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The provider endpoint cannot contain credentials, a query, or a fragment.",
                nameof(endpoint));
        }

        if (endpoint.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
        {
            throw new ArgumentException(
                "The provider endpoint must contain a valid DNS or IP host.",
                nameof(endpoint));
        }

        if (string.Equals(endpoint.Scheme, "http", StringComparison.Ordinal) && !IsLoopback(endpoint))
        {
            throw new ArgumentException(
                "Plain HTTP is allowed only for an exact loopback endpoint.",
                nameof(endpoint));
        }

        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath.EndsWith('/')
                ? endpoint.AbsolutePath
                : $"{endpoint.AbsolutePath}/",
        };
        return builder.Uri;
    }

    private static bool IsLoopback(Uri endpoint)
    {
        if (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(endpoint.Host, out var address)
            && IPAddress.IsLoopback(address);
    }

    private static string RequirePrintable(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The value must be a bounded printable string.",
                parameterName);
        }

        return normalized;
    }
}
