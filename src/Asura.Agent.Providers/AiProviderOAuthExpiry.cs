using System.Globalization;
using System.Text.Json;
using Asura.Application;

namespace Asura.Agent.Providers;

/// <summary>
/// Normalizes the duration and absolute timestamp forms emitted by OAuth providers.
/// Token expiry is provider metadata used to schedule refresh, so Asura
/// validates representability but does not replace the provider's lifetime policy.
/// </summary>
internal static class AiProviderOAuthExpiry
{
    public static DateTimeOffset Read(
        JsonElement root,
        DateTimeOffset now,
        DateTimeOffset? ceiling = null)
    {
        if (root.TryGetProperty("expires_in", out var duration))
        {
            var seconds = ReadSeconds(duration);
            try
            {
                return ApplyCeiling(now.AddSeconds(seconds), ceiling);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw InvalidExpiry();
            }
        }

        if (root.TryGetProperty("expires_at", out var absolute))
        {
            var timestamp = ReadTimestamp(absolute);
            if (timestamp <= now)
            {
                throw InvalidExpiry();
            }

            return ApplyCeiling(timestamp, ceiling);
        }

        return ceiling ?? throw InvalidExpiry();
    }

    private static double ReadSeconds(JsonElement value)
    {
        var seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) => number,
            _ => 0d,
        };
        if (!double.IsFinite(seconds)
            || seconds <= 0)
        {
            throw InvalidExpiry();
        }

        return seconds;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return timestamp;
        }

        var unixSeconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                value.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number) => number,
            _ => 0,
        };
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidExpiry();
        }
    }

    private static DateTimeOffset ApplyCeiling(
        DateTimeOffset timestamp,
        DateTimeOffset? ceiling) => ceiling is { } maximum && timestamp > maximum
            ? maximum
            : timestamp;

    private static AiProviderClientException InvalidExpiry() =>
        AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
}
