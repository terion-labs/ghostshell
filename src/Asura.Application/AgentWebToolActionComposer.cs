using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Binds one closed web request to the run target and one-action authorization.
/// </summary>
public sealed class AgentWebToolActionComposer
{
    public AgentWebToolAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentWebToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            request.ToolName,
            context,
            CreateArgumentDigest(envelope.ActionId, request),
            Presentation(request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentWebToolAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentWebToolAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);
        var proposal = action.Proposal;
        if (!string.Equals(proposal.ToolName, action.Request.ToolName, StringComparison.Ordinal)
            || proposal.ArgumentDigest != CreateArgumentDigest(proposal.Id, action.Request))
        {
            throw new InvalidOperationException(
                "The prepared web action no longer matches its typed request.");
        }

        var targetIdentity = AgentTargetIdentity.Create(freshContext.Target);
        if (proposal.Target != freshContext.Target
            || proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh web target does not match the original run target.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            action.Request.ToolName,
            freshContext.Target,
            targetIdentity,
            freshContext.BindingFingerprint,
            proposal.ArgumentDigest,
            proposal.PolicyGeneration);
    }

    private static AgentApprovalPresentation Presentation(AgentWebToolRequest request) =>
        request switch
        {
            AgentHttpFetchRequest fetch => new AgentApprovalPresentation(
                "Fetch HTTP resource",
                fetch.Address.IdnHost,
                workingDirectory: null,
                [
                    new AgentApprovalArgument("method", fetch.Method.ToString().ToUpperInvariant()),
                    new AgentApprovalArgument("url", fetch.Address.AbsoluteUri),
                ]),
            AgentWebReadRequest read => new AgentApprovalPresentation(
                "Read web page",
                read.Address.IdnHost,
                workingDirectory: null,
                [
                    new AgentApprovalArgument("url", read.Address.AbsoluteUri),
                    new AgentApprovalArgument("format", Format(read.Format)),
                ]),
            AgentWebSearchRequest search => new AgentApprovalPresentation(
                "Google search",
                "www.google.com",
                workingDirectory: null,
                [
                    new AgentApprovalArgument("query", search.Query),
                    new AgentApprovalArgument(
                        "result_count",
                        search.ResultCount.ToString(CultureInfo.InvariantCulture)),
                ]),
            _ => throw new ArgumentException("The web request type is unsupported.", nameof(request)),
        };

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentWebToolRequest request)
    {
        var arguments = request switch
        {
            AgentHttpFetchRequest fetch =>
                $"{fetch.Method}|{Hex(fetch.Address.AbsoluteUri)}",
            AgentWebReadRequest read =>
                $"{read.Format}|{Hex(read.Address.AbsoluteUri)}",
            AgentWebSearchRequest search =>
                $"{search.ResultCount.ToString(CultureInfo.InvariantCulture)}|{Hex(search.Query)}",
            _ => throw new ArgumentException("The web request type is unsupported.", nameof(request)),
        };
        return AgentActionDigest.FromUtf8(string.Join(
            '|',
            "asura.agent-web-action",
            "1",
            actionId.Value,
            request.ToolName,
            arguments));
    }

    private static string Hex(string value) =>
        Convert.ToHexStringLower(Encoding.UTF8.GetBytes(value));

    private static string Format(AgentWebReadFormat format) => format switch
    {
        AgentWebReadFormat.Markdown => "markdown",
        AgentWebReadFormat.RenderedHtml => "rendered_html",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
