using System.Collections.ObjectModel;
using System.Net;
using GhostShell.Core;

namespace GhostShell.Application;

public abstract record WorkspaceNetworkPlacement
{
    private WorkspaceNetworkPlacement()
    {
    }

    public static WorkspaceNetworkPlacement Host { get; } = new HostPlacement();

    public static WorkspaceNetworkPlacement Isolated(WorkspaceIsolationBinding binding) =>
        new IsolatedPlacement(binding ?? throw new ArgumentNullException(nameof(binding)));

    public sealed record HostPlacement : WorkspaceNetworkPlacement;

    public sealed record IsolatedPlacement(WorkspaceIsolationBinding Binding) :
        WorkspaceNetworkPlacement;
}

/// <summary>
/// The network path available to workspace consumers. Proxy endpoints are local adapter
/// endpoints and never carry durable credentials. Attached means the execution environment
/// itself owns the route. Blocked is an intentional kill-switch outcome.
/// </summary>
public abstract record WorkspaceNetworkEgress
{
    private WorkspaceNetworkEgress()
    {
    }

    public static WorkspaceNetworkEgress Direct { get; } = new DirectEgress();

    public static WorkspaceNetworkEgress Attached { get; } = new AttachedEgress();

    public static WorkspaceNetworkEgress Blocked { get; } = new BlockedEgress();

    public static WorkspaceNetworkEgress ViaProxy(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || endpoint.Port is < 1 or > 65_535
            || endpoint.HostNameType == UriHostNameType.Unknown
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.Scheme is not ("socks5" or "http" or "https"))
        {
            throw new ArgumentException(
                "A workspace proxy route requires an absolute SOCKS5, HTTP, or HTTPS endpoint without credentials.",
                nameof(endpoint));
        }

        return new ProxyEgress(endpoint);
    }

    public virtual Uri? ProxyEndpoint => null;

    private sealed record DirectEgress : WorkspaceNetworkEgress;

    private sealed record AttachedEgress : WorkspaceNetworkEgress;

    private sealed record BlockedEgress : WorkspaceNetworkEgress;

    private sealed record ProxyEgress(Uri Endpoint) : WorkspaceNetworkEgress
    {
        public override Uri ProxyEndpoint => Endpoint;
    }
}

public enum NetworkConnectionState
{
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Failed,
}

public sealed record NetworkConnectionSnapshot
{
    public NetworkConnectionSnapshot(
        NetworkConnectionId connectionId,
        NetworkConnectionState state,
        string? status = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId.Value))
        {
            throw new ArgumentException("A network connection ID is required.", nameof(connectionId));
        }
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        ConnectionId = connectionId;
        State = state;
        Status = NormalizeStatus(status);
    }

    public NetworkConnectionId ConnectionId { get; }

    public NetworkConnectionState State { get; }

    public string? Status { get; }

    private static string? NormalizeStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A network connection status cannot contain NUL characters.",
                nameof(value));
        }

        return normalized;
    }
}

public sealed record NetworkConnectionProgress
{
    public NetworkConnectionProgress(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (status.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Network connection progress cannot contain NUL characters.",
                nameof(status));
        }

        Status = status.Trim();
    }

    public string Status { get; }
}

public enum NetworkConnectionErrorCode
{
    RuntimeMissing,
    AuthenticationRequired,
    InvalidConfiguration,
    ConnectionFailed,
    RouteUnavailable,
    Cancelled,
    AuthenticationRejected,
}

public sealed record NetworkConnectionError
{
    public NetworkConnectionError(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, null);
        }

        Code = code;
        StableCode = RequireText(stableCode, nameof(stableCode));
        Message = RequireText(message, nameof(message));
        Retryable = retryable;
    }

    public NetworkConnectionErrorCode Code { get; }

    public string StableCode { get; }

    public string Message { get; }

    public bool Retryable { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A network error cannot contain NUL characters.", parameterName);
        }

        return value.Trim();
    }
}

public abstract record NetworkConnectionResult<T>
{
    private NetworkConnectionResult()
    {
    }

    public sealed record Success(T Value) : NetworkConnectionResult<T>;

    public sealed record Failure(NetworkConnectionError Error) : NetworkConnectionResult<T>;

    public static NetworkConnectionResult<T> Succeed(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Success(value);
    }

    public static NetworkConnectionResult<T> Fail(NetworkConnectionError error) =>
        new Failure(error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed record NetworkConnectionStartRequest
{
    public NetworkConnectionStartRequest(
        WorkspaceInstanceId workspaceId,
        NetworkConnectionProfile connection,
        WorkspaceNetworkPlacement placement,
        bool killSwitchEnabled,
        SecretMaterial? transientPassword = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace instance ID is required.", nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        KillSwitchEnabled = killSwitchEnabled;
        TransientPassword = transientPassword;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public NetworkConnectionProfile Connection { get; }

    public WorkspaceNetworkPlacement Placement { get; }

    public bool KillSwitchEnabled { get; }

    /// <summary>
    /// Session-only password owned by the caller. Providers may read it only while
    /// <see cref="INetworkConnectionProvider.ConnectAsync"/> is running and must copy
    /// anything they need before that call returns.
    /// </summary>
    public SecretMaterial? TransientPassword { get; }
}

public sealed record NetworkPasswordPromptRequest
{
    public NetworkPasswordPromptRequest(
        NetworkConnectionId connectionId,
        string connectionName,
        bool storedPasswordRejected = false)
    {
        if (string.IsNullOrWhiteSpace(connectionId.Value))
        {
            throw new ArgumentException(
                "A network connection ID is required.",
                nameof(connectionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ConnectionId = connectionId;
        ConnectionName = connectionName.Trim();
        StoredPasswordRejected = storedPasswordRejected;
    }

    public NetworkConnectionId ConnectionId { get; }

    public string ConnectionName { get; }

    public bool StoredPasswordRejected { get; }
}

/// <summary>
/// Presentation boundary for missing or rejected passwords. Successful material is
/// owned by the caller. Persistence of a replacement needs separate, post-authentication consent.
/// </summary>
public interface INetworkPasswordPrompt
{
    ValueTask<NetworkConnectionResult<SecretMaterial>> RequestPasswordAsync(
        NetworkPasswordPromptRequest request,
        CancellationToken cancellationToken);

    /// <summary>Asked only after the replacement authenticated successfully.</summary>
    ValueTask<bool> ConfirmPasswordUpdateAsync(NetworkPasswordPromptRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    ValueTask NotifyPasswordUpdateFailedAsync(NetworkPasswordPromptRequest request, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public interface INetworkConnectionSession : IAsyncDisposable
{
    NetworkConnectionSnapshot Snapshot { get; }

    WorkspaceNetworkEgress Egress { get; }

    /// <summary>
    /// Resolver addresses negotiated by this connection. An empty list means the provider
    /// cannot yet supply DNS for an authoritative isolated route.
    /// </summary>
    IReadOnlyList<IPAddress> DnsServers => [];

    /// <summary>
    /// The owned provider supports SOCKS5 UDP association. The packet gateway must still
    /// verify the loopback association before publishing UDP route capabilities.
    /// </summary>
    bool SupportsUdpAssociate => false;

    event EventHandler<NetworkConnectionSnapshot>? Changed;
}

/// <summary>Provider seam implemented by each real proxy or VPN backend.</summary>
public interface INetworkConnectionProvider
{
    NetworkConnectionKind Kind { get; }

    ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}

public enum WorkspaceNetworkState
{
    Direct,
    Connecting,
    Connected,
    Blocked,
    Failed,
}

public sealed record WorkspaceNetworkSnapshot
{
    public WorkspaceNetworkSnapshot(
        WorkspaceNetworkState state,
        WorkspaceNetworkEgress egress,
        NetworkConnectionId? selectedConnectionId,
        NetworkConnectionError? error = null,
        WorkspacePacketRouteCapabilities? routeCapabilities = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        if (selectedConnectionId is { } selected)
        {
            if (string.IsNullOrWhiteSpace(selected.Value))
            {
                throw new ArgumentException(
                    "A selected network connection ID is required.",
                    nameof(selectedConnectionId));
            }
        }

        ValidateState(state, egress, selectedConnectionId, error);
        State = state;
        Egress = egress;
        SelectedConnectionId = selectedConnectionId;
        Error = error;
        RouteCapabilities = routeCapabilities;
    }

    public WorkspaceNetworkState State { get; }

    public WorkspaceNetworkEgress Egress { get; }

    public NetworkConnectionId? SelectedConnectionId { get; }

    public NetworkConnectionError? Error { get; }

    public WorkspacePacketRouteCapabilities? RouteCapabilities { get; }

    public static WorkspaceNetworkSnapshot Direct { get; } = new(
        WorkspaceNetworkState.Direct,
        WorkspaceNetworkEgress.Direct,
        null);

    private static void ValidateState(
        WorkspaceNetworkState state,
        WorkspaceNetworkEgress egress,
        NetworkConnectionId? selectedConnectionId,
        NetworkConnectionError? error)
    {
        ArgumentNullException.ThrowIfNull(egress);
        if (state == WorkspaceNetworkState.Direct
            && (egress is not null
                && egress != WorkspaceNetworkEgress.Direct
                && egress != WorkspaceNetworkEgress.Attached
                || selectedConnectionId is not null
                || error is not null))
        {
            throw new ArgumentException("A direct workspace network snapshot cannot carry a connection or error.");
        }

        if (state == WorkspaceNetworkState.Blocked && egress != WorkspaceNetworkEgress.Blocked)
        {
            throw new ArgumentException("A blocked workspace network snapshot requires blocked egress.");
        }

        if (state == WorkspaceNetworkState.Connected
            && selectedConnectionId is null)
        {
            throw new ArgumentException("An active workspace network snapshot requires a selected connection.");
        }

        if (state == WorkspaceNetworkState.Failed && error is null)
        {
            throw new ArgumentException("A failed workspace network snapshot requires an error.");
        }
    }
}

public sealed record WorkspaceNetworkPolicyUpdate
{
    public WorkspaceNetworkPolicyUpdate(
        NetworkPolicy policy,
        IReadOnlyList<NetworkConnectionProfile> connections)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentNullException.ThrowIfNull(connections);
        Connections = new ReadOnlyCollection<NetworkConnectionProfile>([.. connections]);
        var byId = Connections.ToDictionary(connection => connection.Id);
        if (Policy.Connections.Any(id => !byId.ContainsKey(id)))
        {
            throw new ArgumentException(
                "Every policy connection must have a matching profile.",
                nameof(connections));
        }
    }

    public NetworkPolicy Policy { get; }

    public IReadOnlyList<NetworkConnectionProfile> Connections { get; }
}

public interface IWorkspaceNetworkSession : IAsyncDisposable
{
    WorkspaceNetworkSnapshot Snapshot { get; }

    event EventHandler<WorkspaceNetworkSnapshot>? Changed;

    ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>> ApplyAsync(
        WorkspaceNetworkPolicyUpdate update,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record WorkspaceNetworkOpenRequest
{
    public WorkspaceNetworkOpenRequest(
        WorkspaceInstanceId workspaceId,
        WorkspaceNetworkPolicyUpdate initialPolicy,
        WorkspaceNetworkPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace instance ID is required.", nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        InitialPolicy = initialPolicy ?? throw new ArgumentNullException(nameof(initialPolicy));
        Placement = placement ?? throw new ArgumentNullException(nameof(placement));
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public WorkspaceNetworkPolicyUpdate InitialPolicy { get; }

    public WorkspaceNetworkPlacement Placement { get; }
}

/// <summary>
/// Owns the selected provider session and applies kill-switch fallback for one running
/// workspace. Presentation code observes this seam and never controls providers directly.
/// </summary>
public interface IWorkspaceNetworkRuntime
{
    ValueTask<IWorkspaceNetworkSession> OpenAsync(
        WorkspaceNetworkOpenRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}
