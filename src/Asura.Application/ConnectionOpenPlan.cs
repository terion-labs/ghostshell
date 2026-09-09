using Asura.Core;

namespace Asura.Application;

/// <summary>
/// A non-secret launch plan. Secret requirements are opaque and must be fulfilled by the adapter at execution time.
/// </summary>
public sealed record ConnectionOpenPlan
{
    public ConnectionOpenPlan(
        ConnectionId connectionId,
        ConnectionKind kind,
        TerminalLaunchRequest launch,
        ConnectionAuthenticationMode authentication,
        SshHostKeyPolicy hostKeyPolicy,
        ConnectionReconnectMode reconnectMode,
        IReadOnlyList<ConnectionSecretRequirement>? secretRequirements = null,
        IReadOnlyList<ConnectionPlanWarning>? warnings = null,
        bool isSecretBrokerPrepared = false)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ConnectionId = connectionId;
        Kind = kind;
        Launch = launch;
        Authentication = authentication;
        HostKeyPolicy = hostKeyPolicy;
        ReconnectMode = reconnectMode;
        SecretRequirements = Array.AsReadOnly(secretRequirements?.ToArray() ?? []);
        Warnings = Array.AsReadOnly(warnings?.Distinct().ToArray() ?? []);
        if (isSecretBrokerPrepared && SecretRequirements.Count == 0)
        {
            throw new ArgumentException(
                "A prepared secret broker requires at least one secret requirement.",
                nameof(isSecretBrokerPrepared));
        }

        IsSecretBrokerPrepared = isSecretBrokerPrepared;
    }

    public ConnectionId ConnectionId { get; }

    public ConnectionKind Kind { get; }

    public TerminalLaunchRequest Launch { get; }

    public ConnectionAuthenticationMode Authentication { get; }

    public SshHostKeyPolicy HostKeyPolicy { get; }

    public ConnectionReconnectMode ReconnectMode { get; }

    public IReadOnlyList<ConnectionSecretRequirement> SecretRequirements { get; }

    public IReadOnlyList<ConnectionPlanWarning> Warnings { get; }

    public bool IsSecretBrokerPrepared { get; }

    public bool RequiresSecretBroker => SecretRequirements.Count > 0 && !IsSecretBrokerPrepared;

    public ConnectionOpenPlan WithPreparedSecretBroker(TerminalLaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (SecretRequirements.Count == 0)
        {
            throw new InvalidOperationException("This connection plan has no secret requirements.");
        }

        return new ConnectionOpenPlan(
            ConnectionId,
            Kind,
            launch,
            Authentication,
            HostKeyPolicy,
            ReconnectMode,
            SecretRequirements,
            [.. Warnings.Where(warning => warning != ConnectionPlanWarning.SecretBrokerRequired)],
            isSecretBrokerPrepared: true);
    }
}
