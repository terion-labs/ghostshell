using Asura.Application;
using Asura.Core;

namespace Asura.Monitoring;

public sealed class SystemMonitorPanelSessionFactory : ISystemMonitorPanelSessionFactory, IDisposable
{
    private readonly IConnectionCommandExecutor? _executor;
    private readonly INetworkSnapshotSource? _networkSource;
    private readonly IProcessSnapshotSource? _source;
    private readonly object _samplerGate = new();
    private readonly Dictionary<ConnectionId, SamplerRegistration> _samplers = [];
    private readonly List<ProcessResourceSampler> _retiredSamplers = [];
    private readonly TimeProvider _timeProvider;

    public SystemMonitorPanelSessionFactory(TimeProvider timeProvider)
        : this(
            new PosixProcessSnapshotSource(
                new LocalPosixCommandTransport(),
                timeProvider,
                Environment.ProcessId),
            timeProvider,
            new SystemNetworkSnapshotSource())
    {
    }

    public SystemMonitorPanelSessionFactory(
        IConnectionCommandExecutor executor,
        TimeProvider timeProvider)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public SystemMonitorPanelSessionFactory(
        IPosixCommandTransport transport,
        TimeProvider timeProvider)
        : this(
            new PosixProcessSnapshotSource(
                transport,
                timeProvider,
                localProcessId: null),
            timeProvider,
            new PosixNetworkSnapshotSource(transport))
    {
    }

    internal SystemMonitorPanelSessionFactory(
        IProcessSnapshotSource source,
        TimeProvider timeProvider,
        INetworkSnapshotSource? networkSource = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _networkSource = networkSource;
    }

    public CapabilitySet StatisticsCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.StatisticsRead,
    ]);

    public CapabilitySet ProcessMonitorCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.ProcessesList,
    ]);

    ValueTask<IStatisticsPanelSession>
        ISystemMonitorPanelSessionFactory.CreateStatisticsAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            ConnectionProfile connection,
            CancellationToken cancellationToken) =>
        CreateStatisticsAsync(sessionId, connection, cancellationToken);

    public ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IStatisticsPanelSession>(new StatisticsPanelSession(
            sessionId,
            NewSampler(connection),
            StatisticsCapabilities,
            _timeProvider));
    }

    public ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken) =>
        CreateStatisticsAsync(sessionId, BuiltInConnections.Local, cancellationToken);

    ValueTask<IProcessMonitorPanelSession>
        ISystemMonitorPanelSessionFactory.CreateProcessMonitorAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            ConnectionProfile connection,
            CancellationToken cancellationToken) =>
        CreateProcessMonitorAsync(sessionId, connection, cancellationToken);

    public ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IProcessMonitorPanelSession>(new ProcessMonitorPanelSession(
            sessionId,
            NewSampler(connection),
            ProcessMonitorCapabilities,
            _timeProvider));
    }

    public ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        SessionId sessionId,
        CancellationToken cancellationToken) =>
        CreateProcessMonitorAsync(sessionId, BuiltInConnections.Local, cancellationToken);

    private ProcessResourceSampler NewSampler(ConnectionProfile connection)
    {
        lock (_samplerGate)
        {
            if (_samplers.TryGetValue(connection.Id, out var registered)
                && HasSameExecutionConfiguration(registered.Connection, connection))
            {
                return registered.Sampler;
            }

            var sampler = CreateSampler(connection);
            if (_samplers.Remove(connection.Id, out var replaced))
            {
                _retiredSamplers.Add(replaced.Sampler);
            }

            _samplers[connection.Id] = new SamplerRegistration(connection, sampler);
            return sampler;
        }
    }

    private ProcessResourceSampler CreateSampler(ConnectionProfile connection)
    {
        if (_source is not null)
        {
            return new ProcessResourceSampler(_source, _timeProvider, _networkSource);
        }

        var transport = new ConnectionPosixCommandTransport(_executor!, connection);
        int? localProcessId = connection.ConnectionKind == ConnectionKind.Local
            ? Environment.ProcessId
            : null;
        var networkSource = connection.ConnectionKind == ConnectionKind.Local
            ? (INetworkSnapshotSource)new SystemNetworkSnapshotSource()
            : new PosixNetworkSnapshotSource(transport);
        return new ProcessResourceSampler(
            new PosixProcessSnapshotSource(transport, _timeProvider, localProcessId),
            _timeProvider,
            networkSource);
    }

    private static bool HasSameExecutionConfiguration(
        ConnectionProfile left,
        ConnectionProfile right) =>
        Equals(left.Endpoint, right.Endpoint)
        && Equals(left.Authentication, right.Authentication)
        && Equals(left.Startup, right.Startup)
        && Equals(left.KeepAlive, right.KeepAlive)
        && left.HostKeyPolicy == right.HostKeyPolicy;

    public void Dispose()
    {
        lock (_samplerGate)
        {
            foreach (var registration in _samplers.Values)
            {
                registration.Sampler.Dispose();
            }

            foreach (var sampler in _retiredSamplers)
            {
                sampler.Dispose();
            }

            _samplers.Clear();
            _retiredSamplers.Clear();
        }
    }

    private sealed record SamplerRegistration(
        ConnectionProfile Connection,
        ProcessResourceSampler Sampler);
}
