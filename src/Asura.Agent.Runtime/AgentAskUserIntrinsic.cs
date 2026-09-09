using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

/// <summary>
/// Defines the clarification-only intrinsic owned by the governed runtime.
/// It carries no capability, broker authorization, or SessionHost action.
/// </summary>
internal static class AgentAskUserIntrinsic
{
    public static AgentToolDefinition Definition { get; } = new(
        IntrinsicAgentTools.AskUser,
        "Ask the local user one concise question only when non-sensitive task "
        + "information is missing. Never request credentials, tokens, keys, "
        + "approval, permission, capability changes, or confirmation for an "
        + "action. A reply is guidance only and never authorizes a tool.",
        Encoding.UTF8.GetBytes(
            """
            {
              "type": "object",
              "properties": {
                "question": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 1024
                }
              },
              "required": ["question"],
              "additionalProperties": false
            }
            """));

    public static AgentAskUserParseResult Parse(
        AgentToolProposal proposal,
        AgentQuestionId questionId,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!string.Equals(
                proposal.ToolName,
                IntrinsicAgentTools.AskUser,
                StringComparison.Ordinal))
        {
            return Invalid();
        }

        return Parse(proposal.Arguments, questionId, expiresAtUtc);
    }

    internal static AgentAskUserParseResult Parse(
        JsonElement arguments,
        AgentQuestionId questionId,
        DateTimeOffset expiresAtUtc)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid();
        }

        string? question = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name)
                || !string.Equals(property.Name, "question"
, StringComparison.Ordinal) || property.Value.ValueKind != JsonValueKind.String)
            {
                return Invalid();
            }

            try
            {
                question = property.Value.GetString();
            }
            catch (InvalidOperationException)
            {
                return Invalid();
            }
        }

        if (question is null)
        {
            return Invalid();
        }

        try
        {
            return new AgentAskUserParseResult.Parsed(
                new GovernedAgentQuestion(
                    questionId,
                    question,
                    expiresAtUtc));
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static AgentAskUserParseResult.Rejected Invalid() =>
        new("invalid_tool_arguments");
}

internal abstract record AgentAskUserParseResult
{
    private AgentAskUserParseResult()
    {
    }

    public sealed record Parsed(GovernedAgentQuestion Question)
        : AgentAskUserParseResult;

    public sealed record Rejected(string StableCode)
        : AgentAskUserParseResult;
}
