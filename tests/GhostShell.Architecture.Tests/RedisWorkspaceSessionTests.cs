using System.Buffers.Binary;
using System.Diagnostics;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using StackExchange.Redis;

namespace GhostShell.Architecture.Tests;

public sealed class RedisWorkspaceSessionTests
{
    [Fact]
    public async Task ChildPreservesAllOperationsBinaryKeysFactsAndSubscriptions()
    {
        var session = new RecordingSession();
        var factory = new RecordingFactory(session);
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var key = new RedisKeyReference("binary", [0, 255, 17]);
        var entry = new RedisValueEntry("identity", "field", "value", 7.5, [255, 0]);
        var subscription = new RedisSubscription(RedisSubscriptionKind.Pattern, "channel*");
        var operations = Enum.GetValues<RedisWorkspaceOperation>();
        foreach (var operation in operations)
        {
            var request = new RedisWorkspaceRequest((long)operation + 1, operation,
                operation == RedisWorkspaceOperation.Open ? "secret-connection-string" : "text", "other",
                key, 7, 1234567890123, 9.5, TimeSpan.FromSeconds(31), entry, subscription, true);
            await WriteAsync(input, request);
        }
        input.Position = 0;
        await RedisWorkspaceSession.RunChildAsync(input, output, factory, CancellationToken.None);
        Assert.Equal("secret-connection-string", factory.ConnectionString);
        Assert.Null(factory.Tunnel);
        Assert.True(session.Disposed);
        Assert.Equal(operations.Skip(1), session.Operations);
        Assert.Equal(key.Bytes, session.LastKey!.Bytes);
        Assert.Equal("text", session.LastText);
        Assert.Equal("other", session.LastOtherText);
        Assert.Equal(7, session.LastCount);
        Assert.Equal(1234567890123, session.LastIndex);
        Assert.Equal(9.5, session.LastNumber);
        Assert.Equal(TimeSpan.FromSeconds(31), session.LastExpiry);
        Assert.Equal(entry.RawValue, session.LastEntry!.RawValue);
        Assert.Equal(subscription, session.LastSubscription);
        Assert.True(session.LastSharded);

        output.Position = 0;
        var replies = new List<RedisWorkspaceResponse>();
        while (output.Position < output.Length)
        {
            replies.Add(await RedisWorkspaceProtocol.ReadAsync(output, RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, CancellationToken.None));
        }
        var message = Assert.Single(replies, response => response.Message is not null).Message!;
        Assert.Equal("delivered", message.Payload);
        var results = replies.Where(response => response.Id != 0).ToArray();
        Assert.Equal(operations.Length, results.Length);
        Assert.All(results, response => Assert.Equal(RedisWorkspaceFailure.None, response.Failure));
        Assert.Equal(7, results[^1].Facts!.SelectedDatabase);
        Assert.Equal(9, results[(int)RedisWorkspaceOperation.Publish].Count);
        Assert.True(results[(int)RedisWorkspaceOperation.DeleteKey].Deleted);
        Assert.Equal(RedisEntryRemovalOutcome.Stale, results[(int)RedisWorkspaceOperation.RemoveEntry].Removal);
        Assert.Single(results[(int)RedisWorkspaceOperation.ListSearchIndexes].Indexes!);
        Assert.Equal(3, results[(int)RedisWorkspaceOperation.Search].Search!.Total);
    }

    [Theory]
    [InlineData(false, (int)RedisWorkspaceFailure.Rejected)]
    [InlineData(true, (int)RedisWorkspaceFailure.OutcomeUnknown)]
    public async Task ChildDistinguishesServerRejectionFromUncertainTransport(bool transportFailure, int expected)
    {
        var session = new RecordingSession
        {
            MutationFailure = transportFailure ? new IOException("private transport detail") : new RedisServerException("private server detail"),
        };
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await WriteAsync(input, new(1, RedisWorkspaceOperation.Open, Text: "secret"));
        await WriteAsync(input, new(2, RedisWorkspaceOperation.SetString, Text: "value", Key: new("key", [1])));
        input.Position = 0;
        await RedisWorkspaceSession.RunChildAsync(input, output, new RecordingFactory(session), CancellationToken.None);
        output.Position = 0;
        _ = await RedisWorkspaceProtocol.ReadAsync(output, RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, CancellationToken.None);
        var response = await RedisWorkspaceProtocol.ReadAsync(output, RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, CancellationToken.None);
        Assert.Equal((RedisWorkspaceFailure)expected, response.Failure);
        Assert.DoesNotContain("private", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task ProtocolRoundTripsLargeBinaryValuesAndRejectsOversizedOrTruncatedFrames()
    {
        var raw = new byte[128 * 1024];
        Random.Shared.NextBytes(raw);
        var response = new RedisWorkspaceResponse(1, Key: new(new(new("binary", [0, 255]), "string", null, null),
            raw.Length, [new("string", null, "display", RawValue: raw)], false));
        var bytes = await RedisWorkspaceProtocol.SerializeAsync(response, RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, CancellationToken.None);
        using var stream = new MemoryStream();
        await RedisWorkspaceProtocol.WriteAsync(stream, bytes, CancellationToken.None);
        stream.Position = 0;
        var decoded = await RedisWorkspaceProtocol.ReadAsync(stream, RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, CancellationToken.None);
        Assert.Equal(raw, decoded.Key!.Entries[0].RawValue);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, RedisWorkspaceProtocol.MaximumFrameBytes + 1);
        using var oversized = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => RedisWorkspaceProtocol.ReadAsync(oversized, RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, CancellationToken.None));
        BinaryPrimitives.WriteInt32LittleEndian(header, 100);
        using var truncated = new MemoryStream(header);
        await Assert.ThrowsAsync<EndOfStreamException>(() => RedisWorkspaceProtocol.ReadAsync(truncated, RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => RedisWorkspaceProtocol.SerializeAsync(
            new RedisWorkspaceRequest(1, RedisWorkspaceOperation.Open, Text: new string('x', RedisWorkspaceProtocol.MaximumFrameBytes)),
            RedisWorkspaceJsonContext.Default.RedisWorkspaceRequest, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedMutationResponseClosesOwnedProcessWithoutReplay()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var cleanupCount = 0;
        await using var session = new RedisWorkspaceSession(new(new ProcessStartInfo("/bin/cat"), () =>
        {
            cleanupCount++;
            return Task.CompletedTask;
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exception = await Assert.ThrowsAsync<DatabaseMutationOutcomeUnknownException>(() =>
            session.SetStringAsync(new("binary", [0, 255]), "value", null, timeout.Token));
        Assert.Contains("verify before retrying", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, cleanupCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.DeleteKeyAsync(new("key", [1]), timeout.Token));
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public async Task CancelledBeforeDispatchDoesNotSendOrClaimMutationOutcome()
    {
        if (OperatingSystem.IsWindows()) { return; }
        await using var session = new RedisWorkspaceSession(new(new ProcessStartInfo("/bin/cat"), () => Task.CompletedTask));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SetStringAsync(new("key", [1]), "value", null, cancellation.Token));
    }

    [Fact]
    public async Task FailedLaunchStillCleansItsOwnedLease()
    {
        var cleaned = false;
        var factory = new RedisWorkspaceSessionFactory((_, _) => Task.FromResult(new DatabaseWorkspaceOperationLaunch(
            new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing")), () =>
            {
                cleaned = true;
                return Task.CompletedTask;
            })));
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => factory.OpenAsync("secret", null, CancellationToken.None));
        Assert.True(cleaned);
    }

    [Fact]
    public async Task RouteRevocationClosesAnIdleSessionAndReleasesItsLease()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var route = new CancellationTokenSource();
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new RedisWorkspaceSession(new(new ProcessStartInfo("/bin/cat"), () =>
        {
            cleaned.TrySetResult();
            return Task.CompletedTask;
        }, route.Token));
        await route.CancelAsync();
        await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ScanKeysAsync("*", null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterDispatchReportsUnknownMutationAndPreservesCleanupFailure()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var cancellation = new CancellationTokenSource();
        var start = new ProcessStartInfo("/bin/sleep");
        start.ArgumentList.Add("30");
        var cleanupCount = 0;
        var session = new RedisWorkspaceSession(new(start, () =>
        {
            cleanupCount++;
            return Task.FromException(new IOException("test cleanup failure"));
        }));
        var mutation = session.SetStringAsync(new("key", [1]), "value", null, cancellation.Token);
        await Task.Delay(50, CancellationToken.None);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<DatabaseMutationOutcomeUnknownException>(() => mutation);
        await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
        Assert.Equal(1, cleanupCount);
    }

    private static async Task WriteAsync(Stream stream, RedisWorkspaceRequest request)
    {
        var bytes = await RedisWorkspaceProtocol.SerializeAsync(request, RedisWorkspaceJsonContext.Default.RedisWorkspaceRequest, CancellationToken.None);
        await RedisWorkspaceProtocol.WriteAsync(stream, bytes, CancellationToken.None);
    }

    private sealed class RecordingFactory(RecordingSession session) : IRedisPanelSessionFactory
    {
        public string? ConnectionString { get; private set; }
        public ConnectionProfile? Tunnel { get; private set; }
        public Task<IRedisPanelSession> OpenAsync(string connectionString, ConnectionProfile? tunnel, CancellationToken cancellationToken)
        {
            ConnectionString = connectionString;
            Tunnel = tunnel;
            return Task.FromResult<IRedisPanelSession>(session);
        }
    }

    private sealed class RecordingSession : IRedisPanelSession
    {
        public RedisServerFacts Facts { get; private set; } = new("7", "RESP3", RedisTopologyKind.Standalone, RedisLogicalDatabaseMode.Selectable,
            0, 16, true, true, true, true);
        public event EventHandler<RedisPubSubMessage>? MessageReceived;
        public List<RedisWorkspaceOperation> Operations { get; } = [];
        public bool Disposed { get; private set; }
        public Exception? MutationFailure { get; init; }
        public RedisKeyReference? LastKey { get; private set; }
        public RedisValueEntry? LastEntry { get; private set; }
        public RedisSubscription? LastSubscription { get; private set; }
        public string? LastText { get; private set; }
        public string? LastOtherText { get; private set; }
        public int LastCount { get; private set; }
        public long LastIndex { get; private set; }
        public double LastNumber { get; private set; }
        public TimeSpan? LastExpiry { get; private set; }
        public bool LastSharded { get; private set; }

        public Task SelectDatabaseAsync(int database, CancellationToken cancellationToken)
        {
            Operations.Add(RedisWorkspaceOperation.SelectDatabase);
            Facts = Facts with { SelectedDatabase = database };
            return Task.CompletedTask;
        }
        public Task<RedisScanPage> ScanKeysAsync(string pattern, string? cursor, int count, CancellationToken cancellationToken)
        {
            Operations.Add(RedisWorkspaceOperation.ScanKeys);
            LastText = pattern; LastOtherText = cursor; LastCount = count;
            return Task.FromResult(new RedisScanPage([], "cursor", false));
        }
        public Task<RedisKeySnapshot> ReadKeyAsync(RedisKeyReference key, int maximumEntries, CancellationToken cancellationToken)
        {
            Operations.Add(RedisWorkspaceOperation.ReadKey); LastKey = key; LastCount = maximumEntries;
            return Task.FromResult(new RedisKeySnapshot(new(key, "string", null, null), 0, [], false));
        }
        public Task SetStringAsync(RedisKeyReference key, string value, TimeSpan? expiry, CancellationToken cancellationToken)
        {
            Record(RedisWorkspaceOperation.SetString, key, value); LastExpiry = expiry;
            return MutationFailure is null ? Task.CompletedTask : Task.FromException(MutationFailure);
        }
        public Task SetHashFieldAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.SetHashField, key, field); LastOtherText = value; return Task.CompletedTask; }
        public Task AppendListValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.AppendListValue, key, value); return Task.CompletedTask; }
        public Task SetListValueAsync(RedisKeyReference key, long index, string value, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.SetListValue, key, value); LastIndex = index; return Task.CompletedTask; }
        public Task AddSetValueAsync(RedisKeyReference key, string value, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.AddSetValue, key, value); return Task.CompletedTask; }
        public Task AddSortedSetValueAsync(RedisKeyReference key, string value, double score, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.AddSortedSetValue, key, value); LastNumber = score; return Task.CompletedTask; }
        public Task AddStreamEntryAsync(RedisKeyReference key, string field, string value, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.AddStreamEntry, key, field); LastOtherText = value; return Task.CompletedTask; }
        public Task SetJsonAsync(RedisKeyReference key, string json, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.SetJson, key, json); return Task.CompletedTask; }
        public Task AddTimeSeriesSampleAsync(RedisKeyReference key, double value, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.AddTimeSeriesSample, key, null); LastNumber = value; return Task.CompletedTask; }
        public Task<bool> DeleteKeyAsync(RedisKeyReference key, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.DeleteKey, key, null); return Task.FromResult(true); }
        public Task<RedisEntryRemovalOutcome> RemoveEntryAsync(RedisKeyReference key, string type, RedisValueEntry entry, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.RemoveEntry, key, type); LastEntry = entry; return Task.FromResult(RedisEntryRemovalOutcome.Stale); }
        public Task SetExpiryAsync(RedisKeyReference key, TimeSpan? expiry, CancellationToken cancellationToken)
        { Record(RedisWorkspaceOperation.SetExpiry, key, null); LastExpiry = expiry; return Task.CompletedTask; }
        public Task SubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken)
        { Operations.Add(RedisWorkspaceOperation.Subscribe); LastSubscription = subscription; return Task.CompletedTask; }
        public Task UnsubscribeAsync(RedisSubscription subscription, CancellationToken cancellationToken)
        { Operations.Add(RedisWorkspaceOperation.Unsubscribe); LastSubscription = subscription; return Task.CompletedTask; }
        public Task<long> PublishAsync(string channel, string payload, bool sharded, CancellationToken cancellationToken)
        {
            Operations.Add(RedisWorkspaceOperation.Publish); LastText = channel; LastOtherText = payload; LastSharded = sharded;
            MessageReceived?.Invoke(this, new(LastSubscription!, channel, "delivered", DateTimeOffset.UnixEpoch));
            return Task.FromResult(9L);
        }
        public Task<IReadOnlyList<RedisSearchIndex>> ListSearchIndexesAsync(CancellationToken cancellationToken)
        { Operations.Add(RedisWorkspaceOperation.ListSearchIndexes); return Task.FromResult<IReadOnlyList<RedisSearchIndex>>([new("index", null, null, 3)]); }
        public Task<RedisSearchResult> SearchAsync(string index, string query, int limit, CancellationToken cancellationToken)
        { Operations.Add(RedisWorkspaceOperation.Search); LastText = index; LastOtherText = query; LastCount = limit; return Task.FromResult(new RedisSearchResult(3, [], true)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private void Record(RedisWorkspaceOperation operation, RedisKeyReference key, string? text)
        { Operations.Add(operation); LastKey = key; LastText = text; }
    }
}
