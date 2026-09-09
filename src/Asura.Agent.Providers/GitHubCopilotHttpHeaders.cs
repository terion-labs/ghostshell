using System.Net.Http.Headers;

namespace Asura.Agent.Providers;

/// <summary>
/// GitHub's Copilot endpoints identify supported editor integrations through a
/// fixed header set in addition to the bearer token.
/// </summary>
internal static class GitHubCopilotHttpHeaders
{
    public static void Apply(HttpRequestHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers.UserAgent.Clear();
        headers.UserAgent.ParseAdd("GitHubCopilotChat/0.35.0");
        headers.TryAddWithoutValidation("Editor-Version", "vscode/1.107.0");
        headers.TryAddWithoutValidation(
            "Editor-Plugin-Version",
            "copilot-chat/0.35.0");
        headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
    }

    public static void ApplyModelCatalogVersion(HttpRequestHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-06-01");
    }
}
