using System.Text.Json;

namespace Asura.Agent.Providers;

internal static class AiProviderOAuthClaims
{
    private const int MaximumJwtPayloadBytes = 64 * 1024;

    public static string? ExtractAccountId(string accessToken, string? idToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return TryExtract(idToken) ?? TryExtract(accessToken);
    }

    private static string? TryExtract(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length > MaximumJwtPayloadBytes * 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length > MaximumJwtPayloadBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes, AiProviderJson.DocumentOptions);
            var root = document.RootElement;
            return BoundedClaim(root, "chatgpt_account_id")
                ?? NestedClaim(root, "https://api.openai.com/auth", "chatgpt_account_id")
                ?? FirstOrganizationId(root);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? NestedClaim(
        JsonElement root,
        string objectName,
        string claimName) =>
        root.TryGetProperty(objectName, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? BoundedClaim(value, claimName)
            : null;

    private static string? FirstOrganizationId(JsonElement root)
    {
        if (!root.TryGetProperty("organizations", out var organizations)
            || organizations.ValueKind != JsonValueKind.Array
            || organizations.GetArrayLength() == 0)
        {
            return null;
        }

        var first = organizations[0];
        return first.ValueKind == JsonValueKind.Object
            ? BoundedClaim(first, "id")
            : null;
    }

    private static string? BoundedClaim(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var claim = value.GetString();
        return !string.IsNullOrWhiteSpace(claim)
            && claim.Length <= 512
            && !claim.Any(character => char.IsControl(character)
                || char.IsWhiteSpace(character))
                ? claim
                : null;
    }
}
