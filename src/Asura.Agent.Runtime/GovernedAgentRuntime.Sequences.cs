using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Asura.Agent;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteSequenceAsync(
        AgentToolProposal proposal,
        ImmutableArray<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var steps = AgentSequenceIntrinsic.Parse(proposal.Arguments, tools);
        if (steps.IsEmpty)
        {
            return CreateRejectedResult(proposal, "invalid_sequence");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteStartArray("results");
        var completed = 0;
        AgentToolResult? last = null;
        for (var index = 0; index < steps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[index];
            if (step.DelayMilliseconds > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(step.DelayMilliseconds),
                    _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            var child = new AgentToolProposal(
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                proposal.Generation, proposal.ProviderCallId, step.Tool, step.Arguments);
            last = await ExecuteProposalAsync(child, tools, cancellationToken).ConfigureAwait(false);
            writer.WriteStartObject();
            writer.WriteNumber("index", index);
            writer.WriteString("tool", step.Tool);
            writer.WriteBoolean("ok", last.Status == AgentToolResultStatus.Succeeded);
            writer.WriteString("code", last.StableCode);
            // A single panel read can fill the entire ordinary tool-result budget.
            // Omit that payload rather than losing the receipt of executed mutations.
            if (Encoding.UTF8.GetByteCount(last.Value.Content) <= 4096)
            {
                writer.WriteString("content", last.Value.Content);
            }
            else
            {
                writer.WriteBoolean("content_omitted", true);
            }

            writer.WriteEndObject();
            completed++;
            if (last.Status != AgentToolResultStatus.Succeeded
                || AgentToolOutcomePolicy.Classify(last) != AgentToolOutcomeDisposition.Continue)
            {
                break;
            }
        }

        writer.WriteEndArray();
        writer.WriteNumber("executed_steps", completed);
        writer.WriteNumber("remaining_steps", steps.Length - completed);
        writer.WriteBoolean("ok", last!.Status == AgentToolResultStatus.Succeeded);
        writer.WriteEndObject();
        writer.Flush();
        // Preserve uncertain-outcome codes so the outer run still reconciles or quarantines.
        return new AgentToolResult(proposal, last.Status, last.StableCode,
            AgentToolResultValue.FromJson(buffer.WrittenMemory));
    }
}
