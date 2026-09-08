using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseWorkerRoutingTests
{
    [Fact]
    public async Task ClosedSlotIsRetainedDuringCanceledCleanupAndReclaimedOnlyAfterCompletion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<TestLease> leases = [];
        var authority = new DatabaseWorkerRoute((_, _, _) =>
        {
            var lease = new TestLease(30001 + leases.Count) { CleanupGate = leases.Count == 0 ? gate : null };
            leases.Add(lease);
            return ValueTask.FromResult<IDatabaseTunnelLease>(lease);
        }, CancellationToken.None);
        var routing = new DatabaseWorkerRouting(authority);
        await using var output = new MemoryStream();
        await using (var initial = await RequestsAsync([.. Enumerable.Range(1, 32).Select(id => new DatabaseEndpointRequest(id, $"db{id}.invalid", 1433))]))
        {
            await routing.ReadControlAsync(initial, output, true, CancellationToken.None);
        }
        leases[0].Closed = true;
        using var cancellation = new CancellationTokenSource();
        await using var blocked = await RequestsAsync(new DatabaseEndpointRequest(33, "new.invalid", 1433));
        var waiting = routing.ReadControlAsync(blocked, output, true, cancellation.Token);
        await leases[0].CleanupEntered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(32, leases.Count);
        await using (var healthy = await RequestsAsync(new DatabaseEndpointRequest(34, "db2.invalid", 1433)))
        {
            await routing.ReadControlAsync(healthy, output, true, CancellationToken.None);
        }
        Assert.Equal(32, leases.Count);
        gate.SetResult();
        await leases[0].Disposed.Task;
        await using (var replacement = await RequestsAsync(new DatabaseEndpointRequest(35, "new.invalid", 1433)))
        {
            await routing.ReadControlAsync(replacement, output, true, CancellationToken.None);
        }
        Assert.Equal(33, leases.Count);
        await routing.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedEndpointRequestsRetainOneForwardAndNoPerAcquisitionObjects()
    {
        var opens = 0;
        var lease = new TestLease(30001);
        var authority = new DatabaseWorkerRoute((_, _, _) =>
        {
            ++opens;
            return ValueTask.FromResult<IDatabaseTunnelLease>(lease);
        }, CancellationToken.None);
        await using var input = await RequestsAsync([.. Enumerable.Range(1, 1000).Select(id => new DatabaseEndpointRequest(id, "same.invalid", 1433))]);
        await using var output = new MemoryStream();
        var routing = new DatabaseWorkerRouting(authority);
        Assert.Equal("result"u8.ToArray(), await routing.ReadControlAsync(input, output, true, CancellationToken.None));
        Assert.Equal(1, opens);
        await routing.DisposeAsync();
        Assert.True(lease.Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task UniqueEndpointBudgetRefusesImmediatelyAndSchemaCompletionReleasesForwards()
    {
        List<TestLease> leases = [];
        var authority = new DatabaseWorkerRoute((_, _, _) =>
        {
            var lease = new TestLease(30001 + leases.Count);
            leases.Add(lease);
            return ValueTask.FromResult<IDatabaseTunnelLease>(lease);
        }, CancellationToken.None);
        await using var input = await RequestsAsync([.. Enumerable.Range(1, 33).Select(id => new DatabaseEndpointRequest(id, $"db{id}.invalid", 1433))]);
        await using var output = new MemoryStream();
        var routing = new DatabaseWorkerRouting(authority);
        await Assert.ThrowsAsync<DatabaseRouteEndpointBudgetException>(() => routing.ReadControlAsync(input, output, true, CancellationToken.None));
        Assert.Equal(32, leases.Count);
        await routing.DisposeAsync();
        Assert.All(leases, lease => Assert.True(lease.Disposed.Task.IsCompleted));
        await using var later = await RequestsAsync(new DatabaseEndpointRequest(34, "late.invalid", 1433));
        await Assert.ThrowsAsync<InvalidDataException>(() => routing.ReadControlAsync(later, output, false, CancellationToken.None));
        Assert.Equal(32, leases.Count);
    }

    [Fact]
    public void SerializedConnectionNeverContainsRouteAuthority()
    {
        var connection = new DatabaseWorkerConnection("postgres", "Host=fixture.invalid")
        {
            Route = new DatabaseWorkerRoute((_, _, _) => throw new InvalidOperationException(), CancellationToken.None),
        };
        var json = JsonSerializer.Serialize(new DatabaseOperationRequest(DatabaseWorkerOperation.Query, connection, "fixture"),
            DatabaseOperationJsonContext.Default.DatabaseOperationRequest);
        Assert.DoesNotContain("\"Route\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectEndpointsUseTheSameCapturedAuthorityAndLeasesEndWithWorker()
    {
        var opened = new List<(string Host, int Port)>();
        var leases = new List<TestLease>();
        var authority = new DatabaseWorkerRoute((host, port, _) =>
        {
            opened.Add((host, port));
            var lease = new TestLease(30000 + opened.Count);
            leases.Add(lease);
            return ValueTask.FromResult<IDatabaseTunnelLease>(lease);
        }, CancellationToken.None);
        await using var input = await RequestsAsync(new(1, "first.invalid", 1433), new(2, "redirect.invalid", 1444));
        await using var output = new MemoryStream();
        var routing = new DatabaseWorkerRouting(authority);
        var result = await routing.ReadControlAsync(input, output, true, CancellationToken.None);
        Assert.Equal("result"u8.ToArray(), result);
        Assert.Equal([("first.invalid", 1433), ("redirect.invalid", 1444)], opened);
        Assert.All(leases, lease => Assert.False(lease.Disposed.Task.IsCompleted));
        output.Position = 0;
        Assert.Equal(new(1, 30001), await ReplyAsync(output));
        Assert.Equal(new(2, 30002), await ReplyAsync(output));
        await routing.DisposeAsync();
        Assert.All(leases, lease => Assert.True(lease.Disposed.Task.IsCompleted));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EndpointOpenRequiresBothExecutionPhaseAndParentAuthority(bool allowed, bool hasAuthority)
    {
        var opens = 0;
        var route = new DatabaseWorkerRoute((_, _, _) => { opens++; throw new IOException(); }, CancellationToken.None);
        await using var routing = new DatabaseWorkerRouting(hasAuthority ? route : null);
        await using var input = await RequestsAsync(new DatabaseEndpointRequest(1, "fixture.invalid", 1433));
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => routing.ReadControlAsync(input, output, allowed, CancellationToken.None));
        Assert.Equal(0, opens);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(0, "fixture.invalid", 1433)]
    [InlineData(2, "fixture.invalid", 1433)]
    [InlineData(1, "fixture.invalid/path", 1433)]
    [InlineData(1, "fixture.invalid\n", 1433)]
    [InlineData(1, "fixture.invalid", 0)]
    [InlineData(1, "fixture.invalid", 65536)]
    public async Task MalformedEndpointNeverReachesCapturedRoute(long id, string host, int port)
    {
        var opens = 0;
        var route = new DatabaseWorkerRoute((_, _, _) => { opens++; throw new IOException(); }, CancellationToken.None);
        await using var routing = new DatabaseWorkerRouting(route);
        await using var input = await RequestsAsync(new DatabaseEndpointRequest(id, host, port));
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => routing.ReadControlAsync(input, output, true, CancellationToken.None));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task ParentDenialReturnsNoEndpointAndNoExceptionText()
    {
        var route = new DatabaseWorkerRoute((_, _, _) => throw new IOException("private route secret"), CancellationToken.None);
        await using var routing = new DatabaseWorkerRouting(route);
        await using var input = await RequestsAsync(new DatabaseEndpointRequest(1, "fixture.invalid", 1433));
        await using var output = new MemoryStream();
        await routing.ReadControlAsync(input, output, true, CancellationToken.None);
        output.Position = 0;
        Assert.Equal(new(1, null), await ReplyAsync(output));
        Assert.DoesNotContain("private", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedEndpointMetadataIsRejectedBeforeBodyAllocation()
    {
        var route = new DatabaseWorkerRoute((_, _, _) => throw new InvalidOperationException(), CancellationToken.None);
        await using var routing = new DatabaseWorkerRouting(route);
        await using var input = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(input, "endpoint-open"u8.ToArray(), CancellationToken.None);
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        await input.WriteAsync(header, CancellationToken.None);
        input.Position = 0;
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => routing.ReadControlAsync(input, output, true, CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CanceledOpenDisposesLateLeaseWithoutPublishingIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TestLease(30001);
        var route = new DatabaseWorkerRoute(async (_, _, _) => { entered.SetResult(); await release.Task; return lease; }, CancellationToken.None);
        await using var routing = new DatabaseWorkerRouting(route);
        await using var input = await RequestsAsync(new DatabaseEndpointRequest(1, "fixture.invalid", 1433));
        await using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var read = routing.ReadControlAsync(input, output, true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        release.SetResult();
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(2, 30001)]
    [InlineData(1, null)]
    [InlineData(1, 0)]
    [InlineData(1, 65536)]
    public async Task InvalidOrDeniedReplyPoisonsChildRouteRatherThanFallingBack(long id, int? port)
    {
        await using var input = new MemoryStream();
        await DatabaseOperationProtocol.WriteMetadataAsync(input, new DatabaseEndpointReply(id, port),
            DatabaseRoutingJsonContext.Default.DatabaseEndpointReply, CancellationToken.None);
        input.Position = 0;
        await using var output = new MemoryStream();
        await using var factory = new ParentEndpointTunnelFactory(input, output);
        var lifetime = factory.RouteLifetime;
        await Assert.ThrowsAsync<IOException>(async () => await factory.OpenAsync(BuiltInConnections.Local, "fixture.invalid", 1433, CancellationToken.None));
        var previousLength = output.Length;
        Assert.True(lifetime.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await factory.OpenAsync(BuiltInConnections.Local, "other.invalid", 1433, CancellationToken.None));
        Assert.Equal(previousLength, output.Length);
    }

    [Fact]
    public async Task ChildRequestsHaveSerializedMonotonicReplyOwnership()
    {
        await using var replies = new MemoryStream();
        foreach (var id in new[] { 1, 2 })
        {
            await DatabaseOperationProtocol.WriteMetadataAsync(replies, new DatabaseEndpointReply(id, 30000 + id),
                DatabaseRoutingJsonContext.Default.DatabaseEndpointReply, CancellationToken.None);
        }
        await using var input = new PausedReadStream(replies.ToArray());
        await using var output = new MemoryStream();
        await using var factory = new ParentEndpointTunnelFactory(input, output);
        var first = factory.OpenAsync(BuiltInConnections.Local, "first.invalid", 1433, CancellationToken.None).AsTask();
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstRequestLength = output.Length;
        var second = factory.OpenAsync(BuiltInConnections.Local, "second.invalid", 1444, CancellationToken.None).AsTask();
        Assert.False(second.IsCompleted);
        Assert.Equal(firstRequestLength, output.Length);
        input.Release.SetResult();
        var leases = await Task.WhenAll(first, second);
        Assert.Equal([30001, 30002], leases.Select(lease => lease.LocalPort));
        foreach (var lease in leases) { await lease.DisposeAsync(); }
        output.Position = 0;
        for (var id = 1; id <= 2; id++)
        {
            await DatabaseOperationProtocol.ExpectAsync(output, "endpoint-open"u8.ToArray(), CancellationToken.None);
            Assert.Equal(id, (await DatabaseOperationProtocol.ReadMetadataAsync(output,
                DatabaseRoutingJsonContext.Default.DatabaseEndpointRequest, CancellationToken.None)).Id);
        }
    }

    [Fact]
    public async Task DisposingChildRouteCancelsLifetimeEvenWhenNativeReplyReadDoesNotCancel()
    {
        await using var input = new UninterruptibleReadStream();
        await using var output = new MemoryStream();
        await using var factory = new ParentEndpointTunnelFactory(input, output);
        var lifetime = factory.RouteLifetime;
        var opening = factory.OpenAsync(BuiltInConnections.Local, "fixture.invalid", 1433, CancellationToken.None).AsTask();
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            Assert.True(lifetime.IsCancellationRequested);
        }
        finally { input.Release.TrySetResult(); }
        await input.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<MemoryStream> RequestsAsync(params DatabaseEndpointRequest[] requests)
    {
        var stream = new MemoryStream();
        foreach (var request in requests)
        {
            await DatabaseOperationProtocol.WriteFrameAsync(stream, "endpoint-open"u8.ToArray(), CancellationToken.None);
            await DatabaseOperationProtocol.WriteMetadataAsync(stream, request,
                DatabaseRoutingJsonContext.Default.DatabaseEndpointRequest, CancellationToken.None);
        }
        await DatabaseOperationProtocol.WriteFrameAsync(stream, "result"u8.ToArray(), CancellationToken.None);
        stream.Position = 0;
        return stream;
    }

    private static Task<DatabaseEndpointReply> ReplyAsync(Stream stream) => DatabaseOperationProtocol.ReadMetadataAsync(stream,
        DatabaseRoutingJsonContext.Default.DatabaseEndpointReply, CancellationToken.None);

    private sealed class TestLease(int localPort) : IDatabaseTunnelLease
    {
        public int LocalPort => localPort;
        public bool Closed { get; set; }
        public bool IsClosed => Closed || Disposed.Task.IsCompletedSuccessfully;
        public TaskCompletionSource? CleanupGate { get; init; }
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            CleanupEntered.TrySetResult();
            if (CleanupGate is { } gate) { await gate.Task; }
            Disposed.TrySetResult();
        }
    }

    private sealed class PausedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class UninterruptibleReadStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            Finished.TrySetResult();
            return 0;
        }
    }
}
