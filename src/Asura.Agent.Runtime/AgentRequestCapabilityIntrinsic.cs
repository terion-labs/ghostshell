using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

/// <summary>
/// Defines the run-local Off-to-Ask request intrinsic. The schema exposes only
/// trusted capability tokens derived from production tools in the current
/// provider request; model prose never enters the human decision surface.
/// </summary>
internal static class AgentRequestCapabilityIntrinsic
{
    public static AgentToolDefinition CreateDefinition(
        ImmutableArray<AgentCapability> candidates)
    {
        if (candidates.IsDefaultOrEmpty
            || candidates.Any(capability => !Enum.IsDefined(capability))
            || candidates.Distinct().Count() != candidates.Length)
        {
            throw new ArgumentException(
                "A capability-request definition requires distinct candidates.",
                nameof(candidates));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WritePropertyName("capability");
            writer.WriteStartObject();
            writer.WriteString("type", "string");
            writer.WritePropertyName("enum");
            writer.WriteStartArray();
            foreach (var capability in candidates)
            {
                writer.WriteStringValue(
                    AgentCapabilityProtocol.GetToken(capability));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue("capability");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return new AgentToolDefinition(
            IntrinsicAgentTools.RequestCapability,
            "Ask the local user to change one currently disabled capability "
            + "to Ask for this run. Use only a capability token listed in the "
            + "schema. This never approves an action: every later action still "
            + "requires its ordinary one-action approval.",
            buffer.WrittenSpan.ToArray());
    }

    public static AgentRequestCapabilityParseResult Parse(
        AgentToolProposal proposal,
        IReadOnlySet<AgentCapability> candidates)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!string.Equals(
                proposal.ToolName,
                IntrinsicAgentTools.RequestCapability,
                StringComparison.Ordinal))
        {
            return Invalid();
        }

        return Parse(proposal.Arguments, candidates);
    }

    internal static AgentRequestCapabilityParseResult Parse(
        JsonElement arguments,
        IReadOnlySet<AgentCapability> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid();
        }

        string? token = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name)
                || !string.Equals(property.Name, "capability"
, StringComparison.Ordinal) || property.Value.ValueKind != JsonValueKind.String)
            {
                return Invalid();
            }

            token = property.Value.GetString();
        }

        if (!AgentCapabilityProtocol.TryParseToken(token, out var capability))
        {
            return Invalid();
        }

        return candidates.Contains(capability)
            ? new AgentRequestCapabilityParseResult.Parsed(capability)
            : new AgentRequestCapabilityParseResult.Unavailable();
    }

    private static AgentRequestCapabilityParseResult.Rejected Invalid() =>
        new("invalid_tool_arguments");
}

internal abstract record AgentRequestCapabilityParseResult
{
    private AgentRequestCapabilityParseResult()
    {
    }

    public sealed record Parsed(AgentCapability Capability)
        : AgentRequestCapabilityParseResult;

    public sealed record Unavailable : AgentRequestCapabilityParseResult;

    public sealed record Rejected(string StableCode)
        : AgentRequestCapabilityParseResult;
}
