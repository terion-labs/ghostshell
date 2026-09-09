using Asura.Core;

namespace Asura.Application;

/// <summary>A non-secret request for a connection-owned, one-use credential launch.</summary>
public sealed record ConnectionCredentialBrokerRequest
{
    public ConnectionCredentialBrokerRequest(
        ConnectionId connectionId,
        ConnectionKind kind,
        ConnectionAuthenticationMode authentication,
        TerminalLaunchRequest launch,
        IReadOnlyList<ConnectionSecretRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(requirements);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The connection kind is invalid.");
        }

        if (!Enum.IsDefined(authentication))
        {
            throw new ArgumentOutOfRangeException(
                nameof(authentication),
                authentication,
                "The connection authentication mode is invalid.");
        }

        if (launch.Executable is null)
        {
            throw new ArgumentException(
                "A credential-broker launch requires an explicit executable.",
                nameof(launch));
        }

        if (requirements.Count == 0)
        {
            throw new ArgumentException(
                "A credential-broker launch requires at least one secret.",
                nameof(requirements));
        }

        ConnectionId = connectionId;
        Kind = kind;
        Authentication = authentication;
        Launch = launch;
        Requirements = Array.AsReadOnly(requirements.ToArray());
    }

    public ConnectionId ConnectionId { get; }

    public ConnectionKind Kind { get; }

    public ConnectionAuthenticationMode Authentication { get; }

    public TerminalLaunchRequest Launch { get; }

    public IReadOnlyList<ConnectionSecretRequirement> Requirements { get; }

    public override string ToString() =>
        $"Connection credential request ({Kind}, {Authentication}, {Requirements.Count} secret handles)";
}
