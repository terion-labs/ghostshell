namespace Asura.Agent.Providers;

public sealed record AiProviderOAuthOptions
{
    public const string OpenAiDefaultClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    // Public, non-secret client identity used by GitHub's first-party Copilot
    // integrations. Keep an override for distributions that register their own app.
    public const string GitHubCopilotDefaultClientId = "Iv1.b507a08c87ecfe98";

    public AiProviderOAuthOptions(
        string openAiClientId = OpenAiDefaultClientId,
        string? gitHubClientId = null,
        TimeSpan? browserTimeout = null,
        TimeSpan? deviceTimeout = null)
    {
        OpenAiClientId = RequireClientId(openAiClientId, nameof(openAiClientId));
        GitHubClientId = RequireClientId(
            string.IsNullOrWhiteSpace(gitHubClientId)
                ? GitHubCopilotDefaultClientId
                : gitHubClientId,
            nameof(gitHubClientId));
        BrowserTimeout = RequireTimeout(
            browserTimeout ?? TimeSpan.FromMinutes(5),
            nameof(browserTimeout));
        DeviceTimeout = RequireTimeout(
            deviceTimeout ?? TimeSpan.FromMinutes(15),
            nameof(deviceTimeout));
    }

    public string OpenAiClientId { get; }

    /// <summary>
    /// Public GitHub OAuth client used for Copilot device authorization. The
    /// default is GitHub's first-party Copilot client; deployments may replace
    /// it with their registered client through configuration.
    /// </summary>
    public string GitHubClientId { get; }

    public TimeSpan BrowserTimeout { get; }

    public TimeSpan DeviceTimeout { get; }

    private static string RequireClientId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 256
            || normalized.Any(character => char.IsControl(character)
                || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("The OAuth client ID is invalid.", parameterName);
        }

        return normalized;
    }

    private static TimeSpan RequireTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromMilliseconds(100) || value > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, null);
        }

        return value;
    }
}
