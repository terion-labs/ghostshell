using Asura.Application;
using Asura.Core;
using Asura.Desktop;
using Asura.Infrastructure;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceConnectionBackendFactoryTests
{
    [Fact]
    public async Task Cleanup_attempts_every_owned_release_and_preserves_failures()
    {
        List<int> called = [];
        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            WorkspaceConnectionBackendFactory.StopOwnedResourcesAsync(
                () => { called.Add(1); throw new IOException("VM stop failed."); },
                () => { called.Add(2); throw new InvalidOperationException("Tunnel release failed."); },
                () => { called.Add(3); return ValueTask.CompletedTask; }));
        Assert.Equal([1, 2, 3], called);
        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Fact]
    public async Task Failed_vm_stop_remains_retryable_and_scope_is_not_pruned()
    {
        var provider = new Provider { FailStop = true };
        var service = await ServiceAsync(provider);
        await Assert.ThrowsAsync<AggregateException>(async () => await service.DisposeAsync());
        Assert.False(service.IsRetired);
        Assert.True(service.RetirementFailed);
        Assert.Equal(1, provider.Stops);
        provider.FailStop = false;
        await service.DisposeAsync();
        Assert.True(service.IsRetired);
        Assert.False(service.RetirementFailed);
        Assert.Equal(2, provider.Stops);
        await service.DisposeAsync();
        Assert.Equal(2, provider.Stops);
    }

    [Fact]
    public async Task Cancellation_callback_failure_does_not_skip_vm_stop_or_revoke_retry()
    {
        var provider = new Provider();
        var authority = new CancellationTokenSource();
        var service = await ServiceAsync(provider, authority);
        using var callback = authority.Token.Register(static () => throw new InvalidOperationException("Fixture cancellation failure."));
        await Assert.ThrowsAsync<AggregateException>(async () => await service.DisposeAsync());
        Assert.Equal(1, provider.Stops);
        Assert.False(service.IsRetired);
        await service.DisposeAsync();
        Assert.True(service.IsRetired);
        Assert.Equal(1, provider.Stops);
    }

    [Fact]
    public async Task Route_cancellation_retires_backend_without_waiting_for_workspace_close()
    {
        var provider = new Provider();
        var authority = new CancellationTokenSource();
        var service = await ServiceAsync(provider, authority);
        service.ObserveRoute();
        await authority.CancelAsync();
        await provider.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await service.DisposeAsync();
        Assert.True(service.IsRetired);
        Assert.Equal(1, provider.Stops);
    }

    private static async Task<WorkspaceConnectionBackendFactory.Session.Service> ServiceAsync(
        Provider provider, CancellationTokenSource? authority = null)
    {
        authority ??= new CancellationTokenSource();
        var isolate = await WorkspaceConnectionServiceIsolate.OpenAsync(provider, new GatewayRuntime(),
            new WorkspaceInstanceId("test-owner"), new WorkspacePacketGatewayServiceProxy(
                new Uri("socks5://127.0.0.1:54321"), new WorkspaceNetworkProxyCredentials("test", "secret")),
            authority.Token, CancellationToken.None);
        return new(null, authority.Token, isolate, null, authority,
            static () => throw new InvalidOperationException("The lifecycle fixture must not create database result storage."));
    }

    private sealed class Provider : IWorkspaceIsolationProvider
    {
        public WorkspaceIsolationProviderDescriptor Descriptor => WorkspaceSdkIsolationProvider.ProviderDescriptor;
        public bool FailStop { get; set; }
        public int Stops { get; private set; }
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
            WorkspaceIsolationPrepareRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(
                new WorkspaceIsolationBinding(request.WorkspaceId, Descriptor.Id, Descriptor.Capabilities,
                    request.WorkspaceId.Value, [], Guid.NewGuid(), network: new WorkspaceIsolationNetworkBinding(
                        "fixture-service", "100.64.0.1", "100.64.0.0/30", hostAttachment:
                        new WorkspaceHostNetworkAttachment("/tmp/fixture-service.control", "/tmp/fixture-service.packet")))));

        public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
            WorkspaceIsolationBinding binding, WorkspaceIsolationProcessRequest request) =>
            throw new InvalidOperationException("The lifecycle fixture must not launch processes.");

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
            WorkspaceIsolationBinding binding, CancellationToken cancellationToken)
        {
            Stops++;
            Stopped.TrySetResult();
            return ValueTask.FromResult(FailStop
                ? WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(WorkspaceIsolationErrorCode.StopFailed, binding)
                : WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding));
        }
    }

    private sealed class GatewayRuntime : IWorkspacePacketGatewayRuntime
    {
        public ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
            WorkspacePacketGatewayOpenRequest request, IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(new Gateway()));
    }

    private sealed class Gateway : IWorkspacePacketGatewaySession
    {
        public WorkspacePacketGatewaySnapshot Snapshot { get; } = new(WorkspacePacketGatewayState.Ready,
            new WorkspacePacketRouteCapabilities(WorkspaceIpAddressFamilies.Ipv4, WorkspaceIpProtocolCapabilities.Tcp, 1280));
        public event EventHandler<WorkspacePacketGatewaySnapshot>? Changed { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
