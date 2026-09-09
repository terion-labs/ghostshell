using System.Text;
using System.Text.Json;

namespace Asura.Core;

/// <summary>
/// A versioned, provider-neutral durable document for one idle native-agent
/// session. The payload is intentionally opaque to storage adapters; the
/// native-agent kernel owns its schema and semantic validation.
/// </summary>
public sealed class AgentSessionCheckpoint
{
    public const int CurrentSchemaVersion = 3;
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;
    public const int MaximumRunIdBytes = 256;

    private static readonly HashSet<string> SecretPropertyNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "accessToken",
        "apiKey",
        "authorization",
        "credential",
        "credentialValue",
        "password",
        "passphrase",
        "privateKey",
        "refreshToken",
        "secret",
        "secretRef",
        "secretReference",
        "secretValue",
        "token",
    };

    public AgentSessionCheckpoint(
        AgentRunId runId,
        int schemaVersion,
        long generation,
        long revision,
        string payloadJson,
        DateTimeOffset updatedAt)
    {
        if (runId == default
            || !IsBoundedIdentifier(runId.Value, MaximumRunIdBytes))
        {
            throw new ArgumentException(
                "A bounded agent run ID is required.",
                nameof(runId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentNullException.ThrowIfNull(payloadJson);
        if (updatedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Checkpoint timestamps must be in UTC.",
                nameof(updatedAt));
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes is 0 or > MaximumPayloadBytes)
        {
            throw new ArgumentException(
                "The checkpoint payload exceeds its byte limit.",
                nameof(payloadJson));
        }

        try
        {
            using var document = JsonDocument.Parse(
                payloadJson,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 160,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "The checkpoint payload must be a JSON object.",
                    nameof(payloadJson));
            }

            if (LiteralSecretValidator.ContainsLikelyLiteralSecret(payloadJson)
                || ContainsSecretProperty(document.RootElement))
            {
                throw new ArgumentException(
                    "The checkpoint payload contains credential material.",
                    nameof(payloadJson));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The checkpoint payload is not valid bounded JSON.",
                nameof(payloadJson),
                exception);
        }

        RunId = runId;
        SchemaVersion = schemaVersion;
        Generation = generation;
        Revision = revision;
        PayloadJson = payloadJson;
        UpdatedAt = updatedAt;
    }

    public AgentRunId RunId { get; }

    public int SchemaVersion { get; }

    public long Generation { get; }

    public long Revision { get; }

    public string PayloadJson { get; }

    public DateTimeOffset UpdatedAt { get; }

    private static bool IsBoundedIdentifier(string? value, int maximumBytes) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= maximumBytes
        && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static bool ContainsSecretProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (SecretPropertyNames.Contains(property.Name)
                    || ContainsSecretProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsSecretProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
