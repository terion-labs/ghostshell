using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Binds one frozen MCP manifest entry and exact JSON arguments to the
/// governed run target and generic <c>mcp.call</c> catalog action.
/// </summary>
public sealed class AgentMcpToolCallActionComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentMcpToolCallAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentMcpToolCallRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            BuiltInAgentTools.McpCall,
            context,
            CreateArgumentDigest(envelope.ActionId, request),
            CreatePresentation(request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentMcpToolCallAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentMcpToolCallAction action,
        AgentContextSnapshot freshContext,
        AgentMcpToolManifest currentManifest)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);
        ArgumentNullException.ThrowIfNull(currentManifest);
        var request = action.Request;
        var proposal = action.Proposal;
        if (request.Manifest.ManifestDigest
                != currentManifest.ManifestDigest
            || !string.Equals(
                request.Manifest.ProviderAlias,
                currentManifest.ProviderAlias,
                StringComparison.Ordinal)
            || request.Manifest.ProfileId
                != currentManifest.ProfileId
            || request.Manifest.ProfileRevision
                != currentManifest.ProfileRevision
            || !string.Equals(
                proposal.ToolName,
                BuiltInAgentTools.McpCall,
                StringComparison.Ordinal)
            || proposal.ArgumentDigest
                != CreateArgumentDigest(proposal.Id, request))
        {
            throw new InvalidOperationException(
                "The prepared MCP call no longer matches its frozen manifest.");
        }

        var targetIdentity = AgentTargetIdentity.Create(freshContext.Target);
        if (proposal.Target != freshContext.Target
            || proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh MCP target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            BuiltInAgentTools.McpCall,
            freshContext.Target,
            targetIdentity,
            freshContext.BindingFingerprint,
            proposal.ArgumentDigest,
            proposal.PolicyGeneration);
    }

    private static AgentApprovalPresentation CreatePresentation(
        AgentMcpToolCallRequest request)
    {
        var manifest = request.Manifest;
        var isStdio = manifest.TransportKind
            == McpServerTransportKind.Stdio;
        return new AgentApprovalPresentation(
            $"MCP server: {manifest.ProfileName}",
            isStdio
                ? "Local MCP stdio process"
                : "Remote MCP Streamable HTTP server",
            manifest.WorkingDirectory,
            [
                new AgentApprovalArgument(
                    isStdio ? "executable" : "endpoint",
                    EscapeForApproval(manifest.TransportTarget),
                    AgentApprovalPresentation.MaximumWorkingDirectoryBytes),
                new AgentApprovalArgument(
                    "tool",
                    EscapeForApproval(
                        manifest.ToolNameRedacted
                            ? manifest.ToolName
                                + " (sensitive identifier redacted)"
                            : manifest.ToolName)),
                new AgentApprovalArgument(
                    "arguments",
                    EscapeForApproval(request.Arguments.GetRawText()),
                    AgentApprovalArgument.MaximumEscapedValueBytes),
                new AgentApprovalArgument(
                    "profile_revision",
                    manifest.ProfileRevision.ToString(
                        CultureInfo.InvariantCulture)),
                new AgentApprovalArgument(
                    "manifest",
                    manifest.ManifestDigest.Value),
            ]);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentMcpToolCallRequest request)
    {
        var manifest = request.Manifest;
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-mcp-tool-call");
        AppendCanonical(hash, "1");
        AppendCanonical(hash, actionId.Value);
        AppendCanonical(hash, BuiltInAgentTools.McpCall);
        AppendCanonical(hash, manifest.ProfileId.Value);
        AppendCanonical(
            hash,
            manifest.ProfileRevision.ToString(
                CultureInfo.InvariantCulture));
        AppendCanonical(hash, manifest.ProviderAlias);
        AppendCanonical(hash, manifest.ToolName);
        AppendCanonical(hash, manifest.ManifestDigest.Value);
        AppendCanonical(hash, request.Arguments.GetRawText());
        return new AgentActionDigest(
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendCanonical(
        IncrementalHash hash,
        string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string EscapeForApproval(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune)
                || Rune.GetUnicodeCategory(rune)
                    is System.Globalization.UnicodeCategory.Format
                        or System.Globalization.UnicodeCategory.LineSeparator
                        or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                builder.Append("\\u");
                builder.Append(
                    rune.Value.ToString(
                        rune.Value <= 0xffff ? "X4" : "X8",
                        CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }
}
