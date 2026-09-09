using System.Text.Json;
using System.Text.Json.Nodes;
using Asura.Agent;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Asura.Mcp.Server;

public sealed partial class WorkspaceMcpServer
{
    private const string WorkspacesTool = "asura.workspaces";

    private async ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        _ = request;
        var workspaces = LiveWorkspaces();
        using var workspaceSchema = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
        var tools = new Dictionary<string, Tool>(StringComparer.Ordinal)
        {
            [WorkspacesTool] = new Tool
            {
                Name = WorkspacesTool,
                Description = "List open Asura workspaces available to this operator. Use an ID as workspace_id on subsequent tools.",
                InputSchema = workspaceSchema.RootElement.Clone(),
            },
        };
        foreach (var runtime in workspaces.Values)
        {
            foreach (var definition in await runtime.ListExternalToolsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (tools.ContainsKey(definition.Name))
                {
                    continue;
                }

                var schema = JsonNode.Parse(definition.InputSchema.GetRawText())!.AsObject();
                var ids = new JsonArray();
                foreach (var id in workspaces.Keys)
                {
                    ids.Add((JsonNode?)JsonValue.Create(id));
                }

                schema["properties"]!.AsObject()["workspace_id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = ids,
                    ["description"] = "Workspace ID returned by asura.workspaces. Scope is fixed for the entire call.",
                };
                var required = schema["required"]?.AsArray();
                if (required is null)
                {
                    required = [];
                    schema["required"] = required;
                }

                required.Add((JsonNode?)JsonValue.Create("workspace_id"));
                using var document = JsonDocument.Parse(schema.ToJsonString());
                tools.Add(definition.Name, new Tool
                {
                    Name = definition.Name,
                    Description = definition.Description,
                    InputSchema = document.RootElement.Clone(),
                });
            }
        }

        return new ListToolsResult { Tools = [.. tools.Values] };
    }

    private async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var workspaces = LiveWorkspaces();
        var parameters = request.Params;
        if (string.Equals(parameters.Name, WorkspacesTool, StringComparison.Ordinal))
        {
            var items = new JsonArray();
            foreach (var entry in workspaces)
            {
                items.Add((JsonNode)new JsonObject
                {
                    ["workspace_id"] = entry.Key,
                    ["title"] = entry.Value.Snapshot.TargetTitle,
                });
            }

            return TextResult(items.ToJsonString(), isError: false);
        }

        if (parameters.Arguments is null
            || !parameters.Arguments.TryGetValue("workspace_id", out var workspaceId)
            || workspaceId.ValueKind != JsonValueKind.String
            || !workspaces.TryGetValue(workspaceId.GetString()!, out var runtime))
        {
            return TextResult("Unknown or closed workspace. Call asura.workspaces again.", isError: true);
        }

        var arguments = new JsonObject();
        foreach (var argument in parameters.Arguments)
        {
            if (!string.Equals(argument.Key, "workspace_id", StringComparison.Ordinal))
            {
                arguments[argument.Key] = JsonNode.Parse(argument.Value.GetRawText());
            }
        }

        using var document = JsonDocument.Parse(arguments.ToJsonString());
        var result = await runtime.CallExternalToolAsync(parameters.Name, document.RootElement, cancellationToken)
            .ConfigureAwait(false);
        return TextResult(result.Value.Content, result.Status != AgentToolResultStatus.Succeeded);
    }

    private static CallToolResult TextResult(string text, bool isError) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = text }],
    };
}
