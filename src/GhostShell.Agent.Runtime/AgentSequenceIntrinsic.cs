using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;

namespace GhostShell.Agent.Runtime;

/// <summary>Bounded sequential calls, without scripting, nesting, or implicit retries.</summary>
internal static class AgentSequenceIntrinsic
{
    internal const int MaximumSteps = 32;
    internal const int MaximumDelayMilliseconds = 10000;
    internal const int MaximumTotalDelayMilliseconds = 30000;

    public static AgentToolDefinition Build(ImmutableArray<AgentToolDefinition> tools)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartArray("required");
        writer.WriteStringValue("steps");
        writer.WriteEndArray();
        writer.WriteStartObject("properties");
        writer.WriteStartObject("steps");
        writer.WriteString("type", "array");
        writer.WriteNumber("minItems", 1);
        writer.WriteNumber("maxItems", MaximumSteps);
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartArray("required");
        writer.WriteStringValue("tool");
        writer.WriteStringValue("arguments");
        writer.WriteEndArray();
        writer.WriteStartObject("properties");
        writer.WriteStartObject("tool");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (var tool in tools.Where(IsSequenceTool))
        {
            writer.WriteStringValue(tool.Name);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("arguments");
        writer.WriteString("type", "object");
        writer.WriteString("description", "Arguments matching the selected tool's advertised schema.");
        writer.WriteEndObject();
        writer.WriteStartObject("delay_ms");
        writer.WriteString("type", "integer");
        writer.WriteNumber("minimum", 0);
        writer.WriteNumber("maximum", MaximumDelayMilliseconds);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return new AgentToolDefinition(IntrinsicAgentTools.RunSequence,
            "Run 1–32 existing tools in order in one call. Optional delay_ms waits before each step; "
            + "total delays must not exceed 30000 ms. Each step is separately authorized and checked "
            + "against live state. Stops on the first failure or uncertain outcome; completed actions "
            + "are not rolled back. Use known panel IDs and browser references. No result substitution "
            + "or nested sequences. Results identify completed steps. Do not replay completed mutations.",
            buffer.WrittenMemory);
    }

    public static bool IsSequenceTool(AgentToolDefinition tool) =>
        !tool.Name.StartsWith("agent.", StringComparison.Ordinal);

    public static ImmutableArray<Step> Parse(
        JsonElement arguments,
        ImmutableArray<AgentToolDefinition> tools)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Count() != 1
            || !arguments.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array
            || steps.GetArrayLength() is < 1 or > MaximumSteps)
        {
            return [];
        }

        var allowed = tools.Where(IsSequenceTool).Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var parsed = ImmutableArray.CreateBuilder<Step>();
        var totalDelay = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object
                || step.EnumerateObject().Any(property => property.Name is not ("tool" or "arguments" or "delay_ms"))
                || step.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                    != step.EnumerateObject().Count()
                || !step.TryGetProperty("tool", out var name)
                || name.ValueKind != JsonValueKind.String
                || !allowed.Contains(name.GetString()!)
                || !step.TryGetProperty("arguments", out var input)
                || input.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var delay = 0;
            if (step.TryGetProperty("delay_ms", out var delayValue)
                && (delayValue.ValueKind != JsonValueKind.Number
                    || !delayValue.TryGetInt32(out delay)
                    || delay is < 0 or > MaximumDelayMilliseconds))
            {
                return [];
            }

            totalDelay += delay;
            if (totalDelay > MaximumTotalDelayMilliseconds)
            {
                return [];
            }

            parsed.Add(new Step(name.GetString()!, input.Clone(), delay));
        }

        return parsed.ToImmutable();
    }

    internal sealed record Step(string Tool, JsonElement Arguments, int DelayMilliseconds);
}
