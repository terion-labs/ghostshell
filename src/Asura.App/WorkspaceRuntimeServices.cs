using Asura.Application;
using Asura.Core;
using Asura.Docker;
using Asura.Git;

namespace Asura.App;

/// <summary>
/// The process and panel backends selected for one running workspace. Both
/// direct and isolated workspaces pass through this surface so workspace
/// networking can decorate either route without leaking platform policy into
/// the presentation layer.
/// </summary>
public sealed record WorkspaceRuntimeBackends
{
    public WorkspaceRuntimeBackends(
        IDockerEngineClient? dockerEngineClient,
        IGitRepositoryClient? gitRepositoryClient,
        IFilePanelClient filePanelClient,
        IFileTransferQueueClient? fileTransferQueueClient,
        IDatabasePanelClient? databasePanelClient,
        IRedisPanelSessionFactory? redisPanelSessionFactory,
        IBrowserRendererViewFactory? browserRendererViewFactory,
        IConnectionSecurityRuntime? connectionSecurityRuntime = null)
    {
        DockerEngineClient = dockerEngineClient;
        GitRepositoryClient = gitRepositoryClient;
        FilePanelClient = filePanelClient
            ?? throw new ArgumentNullException(nameof(filePanelClient));
        FileTransferQueueClient = fileTransferQueueClient;
        DatabasePanelClient = databasePanelClient;
        RedisPanelSessionFactory = redisPanelSessionFactory;
        BrowserRendererViewFactory = browserRendererViewFactory;
        ConnectionSecurityRuntime = connectionSecurityRuntime;
    }

    public IDockerEngineClient? DockerEngineClient { get; }

    public IGitRepositoryClient? GitRepositoryClient { get; }

    public IFilePanelClient FilePanelClient { get; }

    public IFileTransferQueueClient? FileTransferQueueClient { get; }

    public IDatabasePanelClient? DatabasePanelClient { get; }

    public IRedisPanelSessionFactory? RedisPanelSessionFactory { get; }

    public IBrowserRendererViewFactory? BrowserRendererViewFactory { get; }

    public IConnectionSecurityRuntime? ConnectionSecurityRuntime { get; }
}

public abstract record WorkspaceNetworkRoute
{
    private WorkspaceNetworkRoute()
    {
    }

    public static WorkspaceNetworkRoute Direct { get; } = new DirectRoute();

    public virtual Uri? ProxyUri => null;

    public static WorkspaceNetworkRoute ViaProxy(Uri proxyUri)
    {
        ArgumentNullException.ThrowIfNull(proxyUri);
        if (!proxyUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "A workspace network proxy must use an absolute URI.",
                nameof(proxyUri));
        }

        return new ProxyRoute(proxyUri);
    }

    /// <summary>
    /// A route attached at the workspace boundary, such as a VPN or tailnet.
    /// Its concrete adapter owns setup and cleanup; clients use the routed
    /// runtime services without needing provider-specific configuration.
    /// </summary>
    public static WorkspaceNetworkRoute Attached { get; } = new AttachedRoute();

    private sealed record DirectRoute : WorkspaceNetworkRoute;

    private sealed record ProxyRoute(Uri Address) : WorkspaceNetworkRoute
    {
        public override Uri ProxyUri => Address;
    }

    private sealed record AttachedRoute : WorkspaceNetworkRoute;
}

public sealed class WorkspaceRuntimeServices(
    WorkspaceRuntimeBackends backends,
    WorkspaceNetworkRoute networkRoute,
    IAsyncDisposable? lifetime = null,
    IWorkspaceNetworkEgressSink? networkEgressSink = null,
    IWorkspaceNetworkConnector? networkConnector = null) : IAsyncDisposable
{
    private readonly object _networkGate = new();
    private WorkspaceNetworkEgress _networkEgress = WorkspaceNetworkEgress.Direct;

    public WorkspaceRuntimeBackends Backends { get; } = backends
        ?? throw new ArgumentNullException(nameof(backends));

    public WorkspaceNetworkRoute NetworkRoute { get; } = networkRoute
        ?? throw new ArgumentNullException(nameof(networkRoute));

    public WorkspaceNetworkEgress NetworkEgress
    {
        get
        {
            lock (_networkGate)
            {
                return _networkEgress;
            }
        }
    }

    public Uri? EffectiveProxyUri
    {
        get
        {
            var egress = NetworkEgress;
            if (egress == WorkspaceNetworkEgress.Blocked)
            {
                return null;
            }

            if (networkConnector is { } connector)
            {
                return connector.LocalProxyCredentials is { } credentials
                    ? new UriBuilder(connector.LocalProxyEndpoint)
                    {
                        UserName = credentials.Username,
                        Password = credentials.Password,
                    }.Uri
                    : connector.LocalProxyEndpoint;
            }

            return egress.ProxyEndpoint ?? NetworkRoute.ProxyUri;
        }
    }

    public IWorkspaceNetworkConnector? NetworkConnector => networkConnector;

    public bool IsNetworkBlocked => NetworkEgress == WorkspaceNetworkEgress.Blocked;

    public void ApplyNetworkEgress(WorkspaceNetworkEgress egress, string? authenticationRouteIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(egress);
        lock (_networkGate)
        {
            _networkEgress = egress;
        }

        networkEgressSink?.Apply(egress, authenticationRouteIdentity);
    }

    public ValueTask DisposeAsync() =>
        lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public interface IWorkspaceNetworkEgressSink
{
    void Apply(WorkspaceNetworkEgress egress);

    void Apply(WorkspaceNetworkEgress egress, string? authenticationRouteIdentity) => Apply(egress);
}

public sealed class WorkspaceNetworkEgressState : IWorkspaceNetworkEgressSink
{
    private readonly object _gate = new();
    private WorkspaceNetworkEgress _current = WorkspaceNetworkEgress.Direct;
    private Uri? _localProxyEndpoint;
    private WorkspaceNetworkProxyCredentials? _localProxyCredentials;
    private Uri? _browserProxyEndpoint;

    public WorkspaceNetworkEgress Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public Uri? LocalProxyEndpoint
    {
        get
        {
            lock (_gate)
            {
                return _localProxyEndpoint;
            }
        }
    }

    public WorkspaceNetworkProxyCredentials? LocalProxyCredentials
    {
        get
        {
            lock (_gate)
            {
                return _localProxyCredentials;
            }
        }
    }

    public Uri? BrowserProxyEndpoint
    {
        get
        {
            lock (_gate)
            {
                return _browserProxyEndpoint;
            }
        }
    }

    public void SetLocalProxyEndpoint(
        Uri endpoint,
        WorkspaceNetworkProxyCredentials? credentials = null,
        Uri? browserProxyEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            _localProxyEndpoint = endpoint;
            _localProxyCredentials = credentials;
            _browserProxyEndpoint = browserProxyEndpoint;
        }
    }

    public void Apply(WorkspaceNetworkEgress egress)
    {
        ArgumentNullException.ThrowIfNull(egress);
        lock (_gate)
        {
            _current = egress;
        }
    }
}

public sealed record WorkspaceRuntimeServicesRequest
{
    public WorkspaceRuntimeServicesRequest(
        WorkspaceInstanceId workspaceId,
        IConnectionRuntime connectionRuntime,
        WorkspaceRuntimeServices hostServices,
        WorkspaceIsolationBinding? isolationBinding,
        WorkspaceNetworkEgressState? networkEgressState = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException(
                "A workspace runtime services request requires a workspace ID.",
                nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        ConnectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        HostServices = hostServices
            ?? throw new ArgumentNullException(nameof(hostServices));
        IsolationBinding = isolationBinding;
        NetworkEgressState = networkEgressState ?? new WorkspaceNetworkEgressState();
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public IConnectionRuntime ConnectionRuntime { get; }

    public WorkspaceRuntimeServices HostServices { get; }

    public WorkspaceIsolationBinding? IsolationBinding { get; }

    public WorkspaceNetworkEgressState NetworkEgressState { get; }
}

public interface IWorkspaceRuntimeServicesFactory
{
    WorkspaceRuntimeServices Create(WorkspaceRuntimeServicesRequest request);
}
