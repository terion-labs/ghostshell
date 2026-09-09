using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class WorkspaceGraphAgentToolResultJson
{
    private const string ContentOrigin = "untrusted_workspace_graph_metadata";

    public static WorkspaceGraphAgentToolJsonProjection Project(
        AgentWorkspaceGraphActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString("content_origin", ContentOrigin);
        writer.WriteString("scope_kind", ScopeKind(result.ScopeKind));
        writer.WriteBoolean("scope_limited", result.ScopeLimited);
        var includeGraphClock = !result.ScopeLimited;
        switch (result)
        {
            case AgentWorkspaceGraphActionResult.WorkspaceInspected inspected:
                WriteWorkspaceInspection(
                    writer,
                    inspected.Workspace,
                    includeGraphClock);
                break;
            case AgentWorkspaceGraphActionResult.TabsListed listed:
                WritePage(
                    writer,
                    listed.Page,
                    (pageWriter, tab) =>
                        WriteTab(pageWriter, tab, includeGraphClock));
                break;
            case AgentWorkspaceGraphActionResult.PanelsListed listed:
                WritePage(
                    writer,
                    listed.Page,
                    (pageWriter, panel) =>
                        WritePanel(pageWriter, panel, includeGraphClock));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.GetType(),
                    "The workspace graph result kind is unsupported.");
        }

        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount
            > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("workspace_graph_limit_exceeded");
        }

        return new WorkspaceGraphAgentToolJsonProjection(
            true,
            SuccessCode(result),
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string Failure(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return AgentToolResultJson.Failure(
            ProviderStableCode(error),
            error.Retryable);
    }

    internal static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal))
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.InvalidRequest => "target_changed",
            HostErrorCode.NotFound => "target_changed",
            HostErrorCode.RevisionConflict => "target_changed",
            HostErrorCode.UnsupportedProtocol => "tool_not_available",
            HostErrorCode.CapabilityNotSupported => "tool_not_available",
            HostErrorCode.DeadlineExceeded => "deadline_exceeded",
            HostErrorCode.Cancelled => error.StableCode is
                "authority_revoked" or "caller_cancelled"
                    ? error.StableCode
                    : "cancelled",
            _ => "workspace_graph_failed",
        };
    }

    private static WorkspaceGraphAgentToolJsonProjection Rejected(
        string stableCode) =>
        new(
            false,
            stableCode,
            AgentToolResultJson.Failure(
                stableCode,
                retryable: false));

    private static void WriteWorkspaceInspection(
        Utf8JsonWriter writer,
        AgentWorkspaceGraphWorkspaceInspection inspection,
        bool includeGraphClock)
    {
        writer.WriteStartObject("workspace");
        WriteWorkspaceFields(
            writer,
            inspection.Workspace,
            includeGraphClock);
        writer.WriteStartArray("tabs");
        foreach (var tab in inspection.Tabs)
        {
            writer.WriteStartObject();
            WriteTabFields(writer, tab.Tab, includeGraphClock);
            writer.WriteStartArray("panels");
            foreach (var panel in tab.Panels)
            {
                WritePanel(writer, panel, includeGraphClock);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePage<T>(
        Utf8JsonWriter writer,
        AgentWorkspaceGraphPage<T> page,
        Action<Utf8JsonWriter, T> writeItem)
        where T : class
    {
        writer.WriteStartObject("page");
        writer.WriteNumber("offset", page.Offset);
        writer.WriteNumber("page_size", page.PageSize);
        writer.WriteNumber("returned", page.Items.Count);
        if (page.NextOffset is { } nextOffset)
        {
            writer.WriteNumber("next_offset", nextOffset);
        }
        else
        {
            writer.WriteNull("next_offset");
        }

        writer.WriteBoolean("complete", page.NextOffset is null);
        writer.WriteStartArray("items");
        foreach (var item in page.Items)
        {
            writeItem(writer, item);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteWorkspaceFields(
        Utf8JsonWriter writer,
        AgentWorkspaceGraphWorkspace workspace,
        bool includeGraphClock)
    {
        writer.WriteString("window_id", workspace.WindowId.Value);
        writer.WriteString("workspace_id", workspace.WorkspaceId.Value);
        if (includeGraphClock)
        {
            writer.WriteNumber(
                "workspace_revision",
                workspace.WorkspaceRevision);
            writer.WriteNumber("graph_sequence", workspace.GraphSequence);
        }

        WriteTitle(writer, workspace.Title);
    }

    private static void WriteTab(
        Utf8JsonWriter writer,
        AgentWorkspaceGraphTab tab,
        bool includeGraphClock)
    {
        writer.WriteStartObject();
        WriteTabFields(writer, tab, includeGraphClock);
        writer.WriteEndObject();
    }

    private static void WriteTabFields(
        Utf8JsonWriter writer,
        AgentWorkspaceGraphTab tab,
        bool includeGraphClock)
    {
        writer.WriteString("window_id", tab.WindowId.Value);
        writer.WriteString("workspace_id", tab.WorkspaceId.Value);
        if (includeGraphClock)
        {
            writer.WriteNumber("workspace_revision", tab.WorkspaceRevision);
            writer.WriteNumber("graph_sequence", tab.GraphSequence);
        }

        writer.WriteString("tab_id", tab.TabId.Value);
        writer.WriteBoolean("active", tab.IsActive);
        WriteTitle(writer, tab.Title);
    }

    private static void WritePanel(
        Utf8JsonWriter writer,
        AgentWorkspaceGraphPanel panel,
        bool includeGraphClock)
    {
        writer.WriteStartObject();
        writer.WriteString("window_id", panel.WindowId.Value);
        writer.WriteString("workspace_id", panel.WorkspaceId.Value);
        if (includeGraphClock)
        {
            writer.WriteNumber(
                "workspace_revision",
                panel.WorkspaceRevision);
            writer.WriteNumber("graph_sequence", panel.GraphSequence);
        }

        writer.WriteString("tab_id", panel.TabId.Value);
        writer.WriteString("panel_id", panel.PanelId.Value);
        writer.WriteString("kind", PanelKindName(panel.Kind));
        writer.WriteBoolean("visible", panel.IsVisible);
        writer.WriteBoolean("focused", panel.IsFocused);
        WriteTitle(writer, panel.Title);
        writer.WriteEndObject();
    }

    private static void WriteTitle(
        Utf8JsonWriter writer,
        AgentWorkspaceGraphTitle? title)
    {
        if (title is null)
        {
            writer.WriteNull("title");
            return;
        }

        writer.WriteStartObject("title");
        writer.WriteString("text", title.Text);
        writer.WriteNumber("redactions", title.Redactions);
        writer.WriteBoolean("truncated", title.Truncated);
        writer.WriteEndObject();
    }

    private static string ScopeKind(
        AgentWorkspaceGraphScopeKind scopeKind) =>
        scopeKind switch
        {
            AgentWorkspaceGraphScopeKind.Panel => "panel",
            AgentWorkspaceGraphScopeKind.ConnectionSession => "connection_session",
            AgentWorkspaceGraphScopeKind.OpenTab => "open_tab",
            AgentWorkspaceGraphScopeKind.Workspace => "workspace",
            AgentWorkspaceGraphScopeKind.SelectedPanels => "selected_panels",
            _ => throw new ArgumentOutOfRangeException(nameof(scopeKind)),
        };

    private static string PanelKindName(PanelKind kind) =>
        kind switch
        {
            PanelKind.Terminal => "terminal",
            PanelKind.Browser => "browser",
            PanelKind.FileViewer => "file_viewer",
            PanelKind.Statistics => "statistics",
            PanelKind.ProcessMonitor => "process_monitor",
            PanelKind.Placeholder => "placeholder",
            PanelKind.DatabaseViewer => "database_viewer",
            PanelKind.Docker => "docker",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string SuccessCode(
        AgentWorkspaceGraphActionResult result) =>
        result switch
        {
            AgentWorkspaceGraphActionResult.WorkspaceInspected =>
                "workspace_inspected",
            AgentWorkspaceGraphActionResult.TabsListed =>
                "tabs_listed",
            AgentWorkspaceGraphActionResult.PanelsListed =>
                "panels_listed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.GetType(),
                "The workspace graph result kind is unsupported."),
        };
}

internal sealed record WorkspaceGraphAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
