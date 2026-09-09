using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

/// <summary>
/// Defines and parses the one presentation-only tool owned directly by the
/// governed runtime. Its model-supplied text never enters an authorization,
/// capability-bearing host action, audit, or durable transcript boundary.
/// </summary>
internal static class AgentReportProgressIntrinsic
{
    public static AgentToolDefinition Definition { get; } = new(
        IntrinsicAgentTools.ReportProgress,
        "Replace the current visible progress update for this run. "
        + "Use one short single-line status message and, when known, a whole-number percent. "
        + "Never include credentials, tokens, keys, or other secrets.",
        Encoding.UTF8.GetBytes(
            """
            {
              "type": "object",
              "properties": {
                "message": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 512
                },
                "percent": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 100
                }
              },
              "required": ["message"],
              "additionalProperties": false
            }
            """));

    public static AgentReportProgressParseResult Parse(
        AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!string.Equals(
                proposal.ToolName,
                IntrinsicAgentTools.ReportProgress,
                StringComparison.Ordinal))
        {
            return Invalid();
        }

        return Parse(proposal.Arguments);
    }

    internal static AgentReportProgressParseResult Parse(
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid();
        }

        string? message = null;
        int? percent = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name)
                || property.Name is not ("message" or "percent"))
            {
                return Invalid();
            }

            switch (property.Name)
            {
                case "message" when property.Value.ValueKind
                    == JsonValueKind.String:
                    message = property.Value.GetString();
                    break;
                case "percent" when property.Value.ValueKind
                    == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var value):
                    percent = value;
                    break;
                default:
                    return Invalid();
            }
        }

        if (message is null)
        {
            return Invalid();
        }

        try
        {
            return new AgentReportProgressParseResult.Parsed(
                new GovernedAgentProgress(message, percent));
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static AgentReportProgressParseResult.Rejected Invalid() =>
        new("invalid_tool_arguments");
}

internal abstract record AgentReportProgressParseResult
{
    private AgentReportProgressParseResult()
    {
    }

    public sealed record Parsed(GovernedAgentProgress Progress)
        : AgentReportProgressParseResult;

    public sealed record Rejected(string StableCode)
        : AgentReportProgressParseResult;
}
