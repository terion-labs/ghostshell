using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Asura.Application;

namespace Asura.ConnectionBackend;

internal enum RedisWorkspaceOperation
{
    Open, SelectDatabase, ScanKeys, ReadKey, SetString, SetHashField, AppendListValue,
    SetListValue, AddSetValue, AddSortedSetValue, AddStreamEntry, SetJson,
    AddTimeSeriesSample, DeleteKey, RemoveEntry, SetExpiry, Subscribe, Unsubscribe,
    Publish, ListSearchIndexes, Search,
}

internal enum RedisWorkspaceFailure { None, InvalidRequest, Unsupported, Rejected, Unavailable, OutcomeUnknown }

internal sealed record RedisWorkspaceRequest(
    long Id, RedisWorkspaceOperation Operation, string? Text = null, string? OtherText = null,
    RedisKeyReference? Key = null, int Count = 0, long Index = 0, double Number = 0,
    TimeSpan? Expiry = null, RedisValueEntry? Entry = null,
    RedisSubscription? Subscription = null, bool Sharded = false);

internal sealed record RedisWorkspaceResponse(
    long Id, RedisWorkspaceFailure Failure = RedisWorkspaceFailure.None,
    RedisServerFacts? Facts = null, RedisScanPage? Scan = null, RedisKeySnapshot? Key = null,
    bool Deleted = false, RedisEntryRemovalOutcome Removal = RedisEntryRemovalOutcome.Removed,
    long Count = 0, IReadOnlyList<RedisSearchIndex>? Indexes = null, RedisSearchResult? Search = null,
    RedisPubSubMessage? Message = null);

[JsonSerializable(typeof(RedisWorkspaceRequest))]
[JsonSerializable(typeof(RedisWorkspaceResponse))]
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class RedisWorkspaceJsonContext : JsonSerializerContext;

/// <summary>
/// One Redis session uses ordered request/response frames plus unsolicited pub/sub
/// frames. Credentials and binary keys stay in the private pipe, never process arguments.
/// </summary>
internal static class RedisWorkspaceProtocol
{
    internal const int MaximumFrameBytes = BackendJsonFrames.MaximumBytes;
    internal const int MaximumEventBytes = 1024 * 1024;
    internal const int MaximumQueuedEvents = 16;

    internal static bool Mutates(RedisWorkspaceOperation operation) => operation is
        RedisWorkspaceOperation.SetString or RedisWorkspaceOperation.SetHashField or
        RedisWorkspaceOperation.AppendListValue or RedisWorkspaceOperation.SetListValue or
        RedisWorkspaceOperation.AddSetValue or RedisWorkspaceOperation.AddSortedSetValue or
        RedisWorkspaceOperation.AddStreamEntry or RedisWorkspaceOperation.SetJson or
        RedisWorkspaceOperation.AddTimeSeriesSample or RedisWorkspaceOperation.DeleteKey or
        RedisWorkspaceOperation.RemoveEntry or RedisWorkspaceOperation.SetExpiry or RedisWorkspaceOperation.Publish;

    internal static Task<byte[]> SerializeAsync<T>(T value, JsonTypeInfo<T> type, CancellationToken token) =>
        BackendJsonFrames.SerializeAsync(value, type, token);

    internal static Task<T> ReadAsync<T>(Stream input, JsonTypeInfo<T> type, CancellationToken token) =>
        BackendJsonFrames.ReadAsync(input, type, token);

    internal static async Task WriteAsync(Stream output, byte[] frame, CancellationToken token)
    {
        try { await DatabaseOperationProtocol.WriteFrameAsync(output, frame, token).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(frame); }
    }

}
