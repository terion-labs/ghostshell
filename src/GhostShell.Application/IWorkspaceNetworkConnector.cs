namespace GhostShell.Application;

/// <summary>
/// Opens outbound TCP streams through the network route currently selected for
/// one live workspace. Consumers resolve the route for every new connection so
/// a proxy change or kill switch takes effect without rebuilding panel clients.
/// </summary>
public interface IWorkspaceNetworkConnector
{
    WorkspaceNetworkEgress Egress { get; }

    /// <summary>Cancellation of the captured route, including connections borrowed from a pool.</summary>
    CancellationToken RouteLifetime => CancellationToken.None;

    /// <summary>
    /// Captures immutable dialing authority. Dynamic connectors must override this
    /// atomically; the default is only suitable for a genuinely static connector.
    /// The result exposes no route-changing control.
    /// </summary>
    IWorkspaceNetworkConnector CaptureRoute() => this;

    /// <summary>
    /// Stable loopback SOCKS5 endpoint for libraries that own their socket
    /// creation but support a proxy. It remains stable when the selected route
    /// changes and enforces the current kill-switch state.
    /// </summary>
    Uri LocalProxyEndpoint { get; }

    /// <summary>
    /// Per-workspace credentials required by the loopback SOCKS broker.
    /// Implementations backed by a third-party proxy may return <see langword="null"/>.
    /// </summary>
    WorkspaceNetworkProxyCredentials? LocalProxyCredentials => null;

    /// <summary>
    /// Loopback proxy endpoint suitable for the embedded Chromium renderer.
    /// The built-in workspace broker exposes HTTP CONNECT here because Chromium
    /// does not support authenticated SOCKS5 proxies.
    /// </summary>
    Uri BrowserProxyEndpoint => LocalProxyEndpoint;

    /// <summary>
    /// Durable workspace route identity for browser storage. A loopback port is
    /// only a live transport address and must not identify an encrypted profile.
    /// Null keeps the caller's explicit route identity for external connectors.
    /// </summary>
    string? BrowserProfileRouteIdentity => null;

    /// <summary>Current credential authority, distinct from durable cookie storage identity.</summary>
    string? BrowserAuthenticationRouteIdentity => Egress == WorkspaceNetworkEgress.Direct ? "local" : null;

    /// <summary>Called while traffic is blocked, before a different credential authority is enabled.</summary>
    event Func<CancellationToken, Task>? BrowserAuthenticationRouteChanging
    {
        add { }
        remove { }
    }

    event EventHandler? BrowserAuthenticationRouteFailed
    {
        add { }
        remove { }
    }

    ValueTask<Stream> ConnectTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceNetworkProxyCredentials
{
    public WorkspaceNetworkProxyCredentials(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (username.IndexOf('\0') >= 0 || password.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "Workspace proxy credentials cannot contain null characters.");
        }

        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    public override string ToString() => "Workspace network proxy credentials";
}

public sealed class WorkspaceNetworkBlockedException : IOException
{
    public WorkspaceNetworkBlockedException()
        : base("The workspace network kill switch is blocking traffic.")
    {
    }
}
