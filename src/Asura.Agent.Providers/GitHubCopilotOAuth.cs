using System.Text.Json;
using Asura.Application;

namespace Asura.Agent.Providers;

/// <summary>
/// Exchanges a long-lived GitHub device token for the bounded Copilot API token
/// that model requests require. The GitHub token remains vault-only refresh
/// material and is never sent to a Copilot model endpoint.
/// </summary>
internal static class GitHubCopilotOAuth
{
    private static readonly Uri TokenEndpoint =
        new("https://api.github.com/copilot_internal/v2/token");
    private static readonly TimeSpan MaximumTokenLifetime = TimeSpan.FromHours(24);

    public static async ValueTask<AiProviderOAuthSession> ExchangeAsync(
        string gitHubAccessToken,
        AiProviderOAuthHttp http,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RequireToken(gitHubAccessToken);
        var body = await http.GetGitHubCopilotTokenAsync(
            TokenEndpoint,
            gitHubAccessToken,
            cancellationToken).ConfigureAwait(false);
        using var document = AiProviderJson.Parse(body);
        var root = document.RootElement;
        var token = AiProviderJson.RequiredBoundedString(root, "token", 64 * 1024);
        RequireToken(token);
        var expiresAt = ReadExpiry(root);
        var now = timeProvider.GetUtcNow();
        if (expiresAt <= now || expiresAt > now + MaximumTokenLifetime)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }

        return new AiProviderOAuthSession(
            AiProviderOAuthSession.CurrentSchemaVersion,
            "github-copilot",
            token,
            gitHubAccessToken,
            expiresAt);
    }

    private static DateTimeOffset ReadExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("expires_at", out var property))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }

        var seconds = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) => number,
            _ => 0,
        };
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError,
                innerException: exception);
        }
    }

    private static void RequireToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64 * 1024
            || value.Any(character => char.IsControl(character)
                || char.IsWhiteSpace(character)))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }
    }
}
