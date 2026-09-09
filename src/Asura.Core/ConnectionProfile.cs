using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Durable connection configuration. Runtime health and connection state belong to session projections,
/// and credential material is represented only by opaque <see cref="SecretRef"/> values.
/// </summary>
public sealed record ConnectionProfile : IDurableDefinition, IPanelLaunchCapabilitySource
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public ConnectionProfile(
        ConnectionId id,
        int schemaVersion,
        string name,
        ConnectionEndpoint endpoint,
        ConnectionAuthentication authentication,
        ConnectionStartup startup,
        ConnectionKeepAlive keepAlive,
        SshHostKeyPolicy hostKeyPolicy,
        IReadOnlyList<string>? tags = null,
        PanelKind? preferredPanel = null,
        ConnectionId? hostConnectionId = null)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Schema versions start at one.");
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RuntimeId.Require(name, nameof(name)).Trim();
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        Startup = startup ?? throw new ArgumentNullException(nameof(startup));
        KeepAlive = keepAlive ?? throw new ArgumentNullException(nameof(keepAlive));
        HostKeyPolicy = hostKeyPolicy;
        Tags = NormalizeTags(tags);
        PreferredPanel = preferredPanel;
        HostConnectionId = hostConnectionId;

        if (hostConnectionId == id)
        {
            throw new ArgumentException(
                "A connection cannot reference itself as its host connection.",
                nameof(hostConnectionId));
        }

        if (!Enum.IsDefined(hostKeyPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(hostKeyPolicy), hostKeyPolicy, "The host-key policy is not recognized.");
        }

        if (preferredPanel is { } panel && !Endpoint.PanelLaunchCapabilities.Supports(panel))
        {
            throw new ArgumentException(
                $"This endpoint cannot open {panel} panels.",
                nameof(preferredPanel));
        }

        ValidateEndpointPolicy(nameof(authentication), nameof(hostKeyPolicy));
    }

    public static DefinitionKind Kind => DefinitionKind.Connection;

    public ConnectionId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    [JsonIgnore]
    public ConnectionKind ConnectionKind => Endpoint.Kind;

    public ConnectionEndpoint Endpoint { get; }

    public ConnectionAuthentication Authentication { get; }

    public ConnectionStartup Startup { get; }

    public ConnectionKeepAlive KeepAlive { get; }

    public SshHostKeyPolicy HostKeyPolicy { get; }

    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// The panel this connection opens by default, when the profile prefers
    /// one over the endpoint's own default. A Git connection is a Local or
    /// SSH profile whose preferred panel is <see cref="PanelKind.Git"/> and
    /// whose startup directory is the repository path.
    /// </summary>
    public PanelKind? PreferredPanel { get; }

    /// <summary>
    /// The saved connection whose endpoint and credentials this profile uses.
    /// A referencing profile stores no endpoint of its own — its
    /// <see cref="Endpoint"/> is the <see cref="DelegatedSshEndpoint"/>
    /// stand-in — and is resolved against the current catalog every time it is
    /// used, so later edits to the referenced connection apply here. Null means
    /// the profile is standalone and its own endpoint is authoritative.
    /// </summary>
    public ConnectionId? HostConnectionId { get; }

    /// <summary>
    /// The stored stand-in endpoint for a profile that delegates to
    /// <see cref="HostConnectionId"/>. The schema requires a concrete endpoint,
    /// so a referencing profile stores this SSH endpoint purely for its kind
    /// and launch capabilities. It is never connected to:
    /// <see cref="ResolveHostConnection"/> replaces it with the referenced
    /// connection's endpoint before any use, and the editor never shows it.
    /// </summary>
    public static ConnectionEndpoint DelegatedSshEndpoint { get; } =
        new ConnectionEndpoint.Ssh("delegated-host");

    /// <summary>
    /// Resolves <see cref="HostConnectionId"/> against the current catalog at
    /// the moment of use. Contract: a standalone profile returns itself; a
    /// referencing profile returns a copy that keeps this profile's identity,
    /// name, startup (repository path), tags, and preferred panel while taking
    /// the referenced connection's endpoint, authentication, keep-alive, and
    /// host-key policy. Reference chains are followed with a cycle guard.
    /// Returns null when any referenced connection is missing, cyclic, or its
    /// endpoint cannot open this profile's preferred panel — callers must
    /// surface that as "the referenced connection is unavailable", never crash.
    /// </summary>
    public ConnectionProfile? ResolveHostConnection(
        Func<ConnectionId, ConnectionProfile?> findConnection)
    {
        ArgumentNullException.ThrowIfNull(findConnection);
        if (HostConnectionId is null)
        {
            return this;
        }

        var visited = new HashSet<ConnectionId> { Id };
        var host = this;
        while (host.HostConnectionId is { } hostId)
        {
            if (!visited.Add(hostId) || findConnection(hostId) is not { } next)
            {
                return null;
            }

            host = next;
        }

        if (PreferredPanel is { } panel
            && !host.Endpoint.PanelLaunchCapabilities.Supports(panel))
        {
            return null;
        }

        return new ConnectionProfile(
            Id,
            SchemaVersion,
            Name,
            host.Endpoint,
            host.Authentication,
            Startup,
            host.KeepAlive,
            host.HostKeyPolicy,
            Tags,
            PreferredPanel);
    }

    /// <summary>
    /// The endpoint's launch capabilities with the profile's preferred panel
    /// applied as the default. The supported set is always the endpoint's.
    /// </summary>
    [JsonIgnore]
    public PanelLaunchCapabilities PanelLaunchCapabilities => PreferredPanel is { } panel
        ? new PanelLaunchCapabilities(
            panel,
            [.. Endpoint.PanelLaunchCapabilities.SupportedPanels])
        : Endpoint.PanelLaunchCapabilities;

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        var normalized = (tags ?? [])
            .Select(tag => RuntimeId.Require(tag, nameof(tags)).Trim())
            .ToArray();

        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            throw new ArgumentException("Connection tags must be unique.", nameof(tags));
        }

        return Array.AsReadOnly(normalized);
    }

    private void ValidateEndpointPolicy(
        string authenticationParameterName,
        string hostKeyPolicyParameterName)
    {
        if (Endpoint.Kind == ConnectionKind.Ssh)
        {
            if (HostKeyPolicy == SshHostKeyPolicy.NotApplicable)
            {
                throw new ArgumentException(
                    "SSH connections require an explicit host-key policy.",
                    hostKeyPolicyParameterName);
            }

            return;
        }

        if (Authentication is not ConnectionAuthentication.None)
        {
            throw new ArgumentException(
                "Only SSH endpoints accept connection authentication.",
                authenticationParameterName);
        }

        if (HostKeyPolicy != SshHostKeyPolicy.NotApplicable)
        {
            throw new ArgumentException(
                "Host-key policy only applies to SSH endpoints.",
                hostKeyPolicyParameterName);
        }
    }
}
