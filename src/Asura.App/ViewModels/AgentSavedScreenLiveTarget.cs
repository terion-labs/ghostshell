using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// One reviewed runtime tab created from a durable saved-screen template.
/// The durable identity is retained only as provenance; agent authority is
/// always expressed by <see cref="Target"/>'s live instance identities.
/// </summary>
public sealed record AgentSavedScreenLiveTarget
{
    public AgentSavedScreenLiveTarget(
        ScreenId templateId,
        long templateRevision,
        string templateName,
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        string workspaceName,
        TabInstanceId tabId,
        string tabName,
        int panelCount,
        bool isAuthorized = false)
    {
        if (templateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(templateRevision));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tabName);
        ArgumentOutOfRangeException.ThrowIfNegative(panelCount);

        TemplateId = templateId;
        TemplateRevision = templateRevision;
        TemplateName = templateName.Trim();
        WindowId = windowId;
        WorkspaceId = workspaceId;
        WorkspaceName = workspaceName.Trim();
        TabId = tabId;
        TabName = tabName.Trim();
        PanelCount = panelCount;
        IsAuthorized = isAuthorized;
    }

    public ScreenId TemplateId { get; }

    public long TemplateRevision { get; }

    public string TemplateName { get; }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }

    public string WorkspaceName { get; }

    public TabInstanceId TabId { get; }

    public string TabName { get; }

    public int PanelCount { get; }

    public bool IsAuthorized { get; init; }

    public AgentTarget.OpenTab Target =>
        new(WindowId, WorkspaceId, TabId);

    public string ExactIdentity =>
        $"window {WindowId.Value} / workspace {WorkspaceId.Value} / tab {TabId.Value}";

    public AgentSavedScreenLiveTarget Authorize() => this with { IsAuthorized = true };
}
