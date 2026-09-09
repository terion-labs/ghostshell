using System.Text.Json.Serialization;

namespace Asura.Agent.Providers;

internal sealed record AiProviderOAuthSession
{
    [JsonConstructor]
    public AiProviderOAuthSession(
        int schemaVersion,
        string provider,
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAt,
        string? accountId = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, null);
        }

        Provider = RequireToken(provider, nameof(provider), 64, allowWhitespace: false);
        AccessToken = RequireToken(
            accessToken,
            nameof(accessToken),
            MaximumTokenLength,
            allowWhitespace: false);
        RefreshToken = refreshToken is null
            ? null
            : RequireToken(
                refreshToken,
                nameof(refreshToken),
                MaximumTokenLength,
                allowWhitespace: false);
        if (expiresAt == default)
        {
            throw new ArgumentException("The OAuth expiry is required.", nameof(expiresAt));
        }

        ExpiresAt = expiresAt;
        AccountId = string.IsNullOrWhiteSpace(accountId)
            ? null
            : RequireToken(accountId, nameof(accountId), 512, allowWhitespace: false);
        SchemaVersion = schemaVersion;
    }

    public const int CurrentSchemaVersion = 1;
    private const int MaximumTokenLength = 64 * 1024;

    public int SchemaVersion { get; }

    public string Provider { get; }

    public string AccessToken { get; }

    public string? RefreshToken { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string? AccountId { get; }

    private static string RequireToken(
        string value,
        string parameterName,
        int maximumLength,
        bool allowWhitespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength
            || value.Any(character => char.IsControl(character)
                || !allowWhitespace && char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("The OAuth value is invalid.", parameterName);
        }

        return value;
    }
}
