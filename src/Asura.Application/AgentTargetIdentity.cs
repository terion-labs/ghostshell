using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Produces an unambiguous identity for the requested scope itself. This is
/// separate from the target fingerprint, which also binds live revisions.
/// </summary>
public static class AgentTargetIdentity
{
    public static AgentActionDigest Create(AgentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var material = target switch
        {
            AgentTarget.Panel panel => Join(
                "panel",
                panel.WindowId.Value,
                panel.WorkspaceId.Value,
                panel.TabId.Value,
                panel.PanelId.Value),
            AgentTarget.ConnectionSession session => Join(
                "session",
                session.SessionId.Value),
            AgentTarget.OpenTab tab => Join(
                "tab",
                tab.WindowId.Value,
                tab.WorkspaceId.Value,
                tab.TabId.Value),
            AgentTarget.Workspace workspace => Join(
                "workspace",
                workspace.WindowId.Value,
                workspace.WorkspaceId.Value),
            AgentTarget.SelectedPanels selected => Join(
                "selected-panels",
                [.. selected.Panels.SelectMany(panel => new[]
                {
                    panel.WindowId.Value,
                    panel.WorkspaceId.Value,
                    panel.TabId.Value,
                    panel.PanelId.Value,
                })]),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The agent target kind is not supported."),
        };
        return AgentActionDigest.FromUtf8(material);
    }

    private static string Join(string kind, params string[] values)
    {
        var builder = new StringBuilder(kind);
        foreach (var value in values)
        {
            builder
                .Append('|')
                .Append(Encoding.UTF8.GetByteCount(value))
                .Append(':')
                .Append(value);
        }

        return builder.ToString();
    }
}
