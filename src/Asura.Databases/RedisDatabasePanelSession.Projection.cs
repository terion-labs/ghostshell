using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Asura.Application;

namespace Asura.Databases;

internal sealed partial class RedisDatabasePanelSession
{
    private const int MaximumKeyBytes = 4 * 1_024;
    private const int MaximumEntryFieldBytes = 4 * 1_024;
    private const int MaximumEntryValueBytes = 16 * 1_024;
    private const int MaximumProjectedPayloadBytes = 48 * 1_024;
    private const int MaximumSerializedResultBytes = 64 * 1_024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static RedisServerFacts ProjectFacts(RedisServerFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!Enum.IsDefined(facts.Topology)
            || !Enum.IsDefined(facts.LogicalDatabases)
            || facts.SelectedDatabase < 0
            || facts.ConfiguredDatabaseCount < 0)
        {
            throw new InvalidDataException("The Redis provider returned invalid server facts.");
        }

        var result = new RedisServerFacts(
            CopyOptionalMetadata(facts.Version, 256),
            CopyOptionalMetadata(facts.Protocol, 128),
            facts.Topology,
            facts.LogicalDatabases,
            facts.SelectedDatabase,
            facts.ConfiguredDatabaseCount,
            facts.SearchAvailable,
            facts.JsonAvailable,
            facts.TimeSeriesAvailable,
            facts.ShardedPubSubAvailable,
            CopyOptionalMetadata(facts.Limitation, 2_048));
        EnsureSerializedBound(result);
        return result;
    }

    private RedisKeyPage ProjectScanPage(RedisScanPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Keys);
        var nextCursor = CopyOptionalMetadata(page.NextCursor, 256);
        var remainingBytes = MaximumProjectedPayloadBytes
            - 512
            - (nextCursor is null ? 0 : Utf8Length(nextCursor));
        var keys = new List<RedisKeyItem>(page.Keys.Count);
        foreach (var source in page.Keys)
        {
            ValidateKeySummary(source);
            var estimatedCost = 384
                + Utf8Length(source.Key.DisplayName)
                + Utf8Length(source.Type);
            if (estimatedCost > remainingBytes)
            {
                break;
            }

            var key = ProjectKey(source);
            var actualCost = KeyCost(key) + 128;
            if (actualCost > remainingBytes)
            {
                break;
            }

            remainingBytes -= actualCost;
            keys.Add(key);
        }

        var result = new RedisKeyPage(
            Array.AsReadOnly(keys.ToArray()),
            nextCursor,
            page.IsComplete && keys.Count == page.Keys.Count);
        EnsureSerializedBound(result);
        return result;
    }

    private RedisKeyValueSnapshot ProjectKeySnapshot(
        RedisKeySnapshot snapshot,
        RedisKeyReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Entries);
        if (snapshot.Length < 0 || snapshot.Entries.Count > request.MaximumEntries)
        {
            throw new InvalidDataException(
                "The Redis provider returned a key result outside its fixed bounds.");
        }

        var key = ProjectKey(snapshot.Summary);
        var limitation = CopyOptionalMetadata(snapshot.Limitation, 2_048);
        var remainingBytes = MaximumProjectedPayloadBytes
            - KeyCost(key)
            - (limitation is null ? 0 : Utf8Length(limitation))
            - 512;
        if (remainingBytes < 0)
        {
            throw new InvalidDataException(
                "The Redis key metadata exceeds the projected payload bound.");
        }

        var truncated = snapshot.Truncated;
        var entries = CopyEntries(snapshot.Entries, ref remainingBytes, ref truncated);
        var result = new RedisKeyValueSnapshot(
            key,
            snapshot.Length,
            entries,
            truncated,
            limitation);
        EnsureSerializedBound(result);
        return result;
    }

    private static RedisSearchResult ProjectSearchResult(
        RedisSearchResult result,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Values);
        if (result.Total < 0 || result.Values.Count > limit)
        {
            throw new InvalidDataException(
                "The Redis provider returned a search result outside its fixed bounds.");
        }

        var remainingBytes = MaximumProjectedPayloadBytes;
        var truncated = result.Truncated;
        var entries = CopyEntries(result.Values, ref remainingBytes, ref truncated);
        var projected = new RedisSearchResult(result.Total, entries, truncated);
        EnsureSerializedBound(projected);
        return projected;
    }

    private static RedisSearchIndexPage ProjectSearchIndexes(
        IReadOnlyList<RedisSearchIndex> source,
        int maximumIndexes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > 10_000)
        {
            throw new InvalidDataException(
                "The Redis provider returned too many search indexes.");
        }

        var remainingBytes = MaximumProjectedPayloadBytes - 512;
        var indexes = new List<RedisSearchIndex>(
            Math.Min(source.Count, maximumIndexes));
        foreach (var item in source.Take(maximumIndexes))
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.DocumentCount < 0)
            {
                throw new InvalidDataException(
                    "The Redis provider returned invalid search index metadata.");
            }

            var name = CopyMetadata(item.Name, 256);
            var definition = CopyOptionalMetadata(item.Definition, 2_048);
            var attributes = CopyOptionalMetadata(item.Attributes, 4_096);
            var cost = 192
                + Utf8Length(name)
                + (definition is null ? 0 : Utf8Length(definition))
                + (attributes is null ? 0 : Utf8Length(attributes));
            if (cost > remainingBytes)
            {
                break;
            }

            remainingBytes -= cost;
            indexes.Add(new RedisSearchIndex(
                name,
                definition,
                attributes,
                item.DocumentCount));
        }

        var result = new RedisSearchIndexPage(
            Array.AsReadOnly(indexes.ToArray()),
            indexes.Count < source.Count);
        EnsureSerializedBound(result);
        return result;
    }

    private static IReadOnlyList<RedisValueEntry> CopyEntries(
        IReadOnlyList<RedisValueEntry> source,
        ref int remainingBytes,
        ref bool truncated)
    {
        var entries = new List<RedisValueEntry>(source.Count);
        foreach (var entry in source)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Score is { } score && !double.IsFinite(score))
            {
                throw new InvalidDataException("The Redis provider returned an invalid score.");
            }

            const int structuralCost = 128;
            if (remainingBytes <= structuralCost)
            {
                truncated = true;
                break;
            }

            remainingBytes -= structuralCost;
            var identity = CopyBudgetedText(
                entry.Identity,
                MaximumEntryFieldBytes,
                ref remainingBytes,
                ref truncated);
            var field = entry.Field is null
                ? null
                : CopyBudgetedText(
                    entry.Field,
                    MaximumEntryFieldBytes,
                    ref remainingBytes,
                    ref truncated);
            var value = CopyBudgetedText(
                entry.Value,
                MaximumEntryValueBytes,
                ref remainingBytes,
                ref truncated);
            entries.Add(new RedisValueEntry(identity, field, value, entry.Score));
            if (remainingBytes == 0)
            {
                truncated |= entries.Count < source.Count;
                break;
            }
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    private static void ValidateKeySummary(RedisKeySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(summary.Key);
        ArgumentNullException.ThrowIfNull(summary.Key.Bytes);
        if (summary.Key.Bytes.Length is < 1 or > MaximumKeyBytes
            || summary.TimeToLive < TimeSpan.Zero
            || summary.MemoryBytes < 0)
        {
            throw new InvalidDataException("The Redis provider returned invalid key metadata.");
        }

        _ = CopyMetadata(summary.Key.DisplayName, MaximumKeyBytes);
        _ = CopyMetadata(summary.Type, 128);
    }

    private static int KeyCost(RedisKeyItem key) =>
        256
        + Utf8Length(key.Reference.Value)
        + Utf8Length(key.DisplayName)
        + Utf8Length(key.Type);

    private static string CopyBudgetedText(
        string value,
        int perFieldMaximumBytes,
        ref int remainingBytes,
        ref bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        var maximumBytes = Math.Min(perFieldMaximumBytes, Math.Max(remainingBytes, 0));
        var byteCount = Utf8Length(value);
        if (byteCount <= maximumBytes)
        {
            remainingBytes -= byteCount;
            return string.Concat(value);
        }

        truncated = true;
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            used += rune.Utf8SequenceLength;
        }

        remainingBytes -= used;
        return builder.ToString();
    }

    private static string CopyMetadata(string value, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) || Utf8Length(value) > maximumBytes)
        {
            throw new InvalidDataException(
                "The Redis provider returned invalid bounded metadata.");
        }

        return string.Concat(value);
    }

    private static string? CopyOptionalMetadata(string? value, int maximumBytes) =>
        value is null ? null : CopyMetadata(value, maximumBytes);

    private static int Utf8Length(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The Redis provider returned invalid Unicode text.",
                exception);
        }
    }

    private static void EnsureSerializedBound<T>(T value)
    {
        try
        {
            var typeInfo = (JsonTypeInfo<T>)DatabaseProjectionJsonContext.Default
                .Options.GetTypeInfo(typeof(T));
            if (JsonSerializer.SerializeToUtf8Bytes(value, typeInfo).Length
                > MaximumSerializedResultBytes)
            {
                throw new InvalidDataException(
                    "The Redis provider result exceeds the serialized byte bound.");
            }
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The Redis provider result cannot be serialized safely.",
                exception);
        }
    }
}
