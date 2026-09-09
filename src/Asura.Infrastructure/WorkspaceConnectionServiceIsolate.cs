using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Owns one mount-free VM and packet gateway for a captured connection route.
/// Route failure cancels Lifetime immediately. The owner must drain its backend workers
/// before disposal: SDK commands remain available for their private scratch cleanup.
/// Neither the VM nor its synthetic DNS map is reattached to a replacement route.
/// </summary>
public sealed class WorkspaceConnectionServiceIsolate : IAsyncDisposable, IConnectionCommandRuntime
{
    private readonly IWorkspaceIsolationProvider _provider;
    private IWorkspacePacketGatewaySession? _gateway;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _disposal = new(1, 1);
    private CancellationTokenRegistration _routeRegistration;
    private int _disposeStarted;
    private bool _disposed;

    private WorkspaceConnectionServiceIsolate(IWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding)
    {
        _provider = provider;
        Binding = binding;
        Lifetime = _lifetime.Token;
    }

    public WorkspaceIsolationBinding Binding { get; }

    public IConnectionCommandRuntime Commands => this;

    public CancellationToken Lifetime { get; }

    public static async ValueTask<WorkspaceConnectionServiceIsolate> OpenAsync(
        IWorkspaceIsolationProvider provider,
        IWorkspacePacketGatewayRuntime gateways,
        WorkspaceInstanceId owner,
        WorkspacePacketGatewayServiceProxy upstream,
        CancellationToken routeLifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(gateways);
        ArgumentNullException.ThrowIfNull(upstream);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(routeLifetime, cancellationToken);
        startup.Token.ThrowIfCancellationRequested();
        // No host paths or user configuration become service-VM mounts. A fresh ID
        // prevents a retired DNS map, disk, or SDK lease from authorizing a new scope.
        var request = new WorkspaceIsolationPrepareRequest(new WorkspaceId($"service-{Guid.NewGuid():N}"));
        var prepared = await provider.PrepareAsync(request, startup.Token).ConfigureAwait(false);
        if (prepared is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure)
        {
            var startupFailure = new IOException(failure.Error.Message);
            if (failure.CleanupValue is { } cleanup)
            {
                var pending = new WorkspaceConnectionServiceIsolate(provider, cleanup);
                try { await pending.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupFailure)
                {
                    throw new WorkspaceConnectionServiceStartException(startupFailure, cleanupFailure, pending);
                }
            }
            throw startupFailure;
        }
        var binding = ((WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)prepared).Value;
        var service = new WorkspaceConnectionServiceIsolate(provider, binding);
        try
        {
            if (binding.WorkspaceId != request.WorkspaceId || binding.Mounts.Count != 0
                || (binding.Network?.HostAttachment is null && binding.Network?.RelayAttachment is null))
            {
                throw new IOException("The connection service did not receive a private host-attached VM.");
            }
            var opened = await gateways.OpenAsync(
                new WorkspacePacketGatewayOpenRequest(owner, binding, serviceProxy: upstream),
                progress: null, startup.Token).ConfigureAwait(false);
            if (opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure routeFailure)
            {
                throw new IOException(routeFailure.Error.Message);
            }
            var gateway = ((NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success)opened).Value;
            service._gateway = gateway;
            gateway.Changed += service.OnGatewayChanged;
            service._routeRegistration = routeLifetime.Register(service.Invalidate);
            if (gateway.Snapshot.State != WorkspacePacketGatewayState.Ready)
            {
                service.Invalidate();
            }
            if (startup.IsCancellationRequested || service.Lifetime.IsCancellationRequested)
            {
                throw new OperationCanceledException("The connection service route changed during startup.", startup.Token);
            }
            return service;
        }
        catch (Exception startupFailure)
        {
            try { await service.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                throw new WorkspaceConnectionServiceStartException(startupFailure, cleanupFailure, service);
            }
            throw;
        }
    }

    private void OnGatewayChanged(object? sender, WorkspacePacketGatewaySnapshot snapshot)
    {
        if (snapshot.State != WorkspacePacketGatewayState.Ready) { Invalidate(); }
    }

    private void Invalidate()
    {
        try { _lifetime.Cancel(); }
        catch (AggregateException exception)
        {
            // One consumer's cancellation callback must not prevent other workers
            // from observing route loss or escape the native gateway event boundary.
            SecretSafeDiagnosticProjection.WriteTrace("workspace.service.cancel.failed", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposal.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { return; }
            Interlocked.Exchange(ref _disposeStarted, 1);
            await _routeRegistration.DisposeAsync().ConfigureAwait(false);
            if (_gateway is { } gateway) { gateway.Changed -= OnGatewayChanged; }
            Invalidate();
            try
            {
                if (_gateway is not null) { await _gateway.DisposeAsync().ConfigureAwait(false); }
            }
            finally { await StopBindingAsync(_provider, Binding).ConfigureAwait(false); }
            _disposed = true;
        }
        finally { _disposal.Release(); }
        // A failed stop retains the provider lease; a later disposal can retry it.
        // Do not dispose the CTS: callers may still register on the published
        // canceled Lifetime while draining already-captured route work.
    }

    private static async Task StopBindingAsync(IWorkspaceIsolationProvider provider, WorkspaceIsolationBinding binding)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (await provider.StopAsync(binding, timeout.Token).ConfigureAwait(false)
            is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure)
        {
            throw new IOException(failure.Error.Message);
        }
    }

    public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
        ConnectionProfile connection, string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) => Plan(connection, executable, arguments, WorkspaceProcessMode.None, cancellationToken);

    public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
        ConnectionProfile connection, string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) => Plan(connection, executable, arguments, WorkspaceProcessMode.Interactive, cancellationToken);

    private ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> Plan(
        ConnectionProfile connection, string executable, IReadOnlyList<string> arguments,
        WorkspaceProcessMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (connection.Id != BuiltInConnections.Local.Id)
        {
            throw new ArgumentException("Service commands run locally in the owned VM, never on an SSH server.", nameof(connection));
        }
        var planned = _provider.CreateExecLaunch(Binding,
            new WorkspaceIsolationProcessRequest(ConnectionKind.Local, executable, arguments, mode: mode));
        if (planned is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure failure)
        {
            throw new IOException(failure.Error.Message);
        }
        var launch = ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)planned).Value;
        return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Succeed(
            new TerminalLaunchRequest(launch.HostWorkingDirectory, launch.Executable,
                launch.Arguments, launch.Environment, connectionId: BuiltInConnections.Local.Id)));
    }
}
