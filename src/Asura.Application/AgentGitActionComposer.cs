using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

public sealed class AgentGitActionComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentGitAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentGitRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var resolved = Resolve(context, request, execution: false);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            request.ToolName,
            resolved.Context,
            CreateArgumentDigest(envelope.ActionId, request),
            CreatePresentation(resolved.Panel, request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentGitAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentGitAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);
        ValidatePreparedAction(action);
        var resolved = Resolve(freshContext, action.Request, execution: true);
        if (action.Proposal.TargetIdentity
            != AgentTargetIdentity.Create(resolved.Context.Target))
        {
            throw new ArgumentException(
                "The fresh Git target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            action.Proposal.Id,
            action.Proposal.RunId,
            action.Proposal.Actor.Id,
            action.Request.ToolName,
            resolved.Context.Target,
            action.Proposal.TargetIdentity,
            resolved.Context.BindingFingerprint,
            action.Proposal.ArgumentDigest,
            action.Proposal.PolicyGeneration);
    }

    internal static void ValidatePreparedAction(AgentGitAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.Equals(
                action.Proposal.ToolName,
                action.Request.ToolName,
                StringComparison.Ordinal)
            || action.Proposal.ArgumentDigest
                != CreateArgumentDigest(action.Proposal.Id, action.Request))
        {
            throw new ArgumentException(
                "The prepared Git action does not match its typed request.",
                nameof(action));
        }
    }

    private static ResolvedGitContext Resolve(
        AgentContextSnapshot context,
        AgentGitRequest request,
        bool execution)
    {
        var matches = context.Panels
            .Where(panel => panel.PanelId == request.PanelId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain one matching Git panel.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.Git
            || !panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession
            || panel.SessionId is null
            || panel.Lifecycle != SessionLifecycle.Active
            || panel.GitMetadata is not
            {
                ConnectionKind: ConnectionKind.Local,
            } gitMetadata
            || (request.IsMutation && gitMetadata.MutationsQuarantined)
            || !panel.Capabilities.Contains(
                request.RequiredSessionCapability,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A Git action requires one live capable local hosted Git session.",
                nameof(context));
        }

        AgentTarget exactTarget = context.Target switch
        {
            AgentTarget.Panel panelTarget => ValidatePanelTarget(panelTarget, panel),
            AgentTarget.ConnectionSession sessionTarget =>
                ValidateSessionTarget(sessionTarget, panel),
            AgentTarget.OpenTab or AgentTarget.Workspace when !execution =>
                ExactPanelTarget(panel),
            _ => throw new ArgumentException(
                "Git execution requires a freshly resolved exact panel or session target.",
                nameof(context)),
        };
        if (!AgentTargetScope.Contains(context.Target, exactTarget))
        {
            throw new ArgumentException(
                "The Git panel is outside the resolved run target.",
                nameof(context));
        }

        var exactContext = execution
            ? context
            : new AgentContextSnapshot(exactTarget, [panel], context.CapturedAtUtc);
        return new ResolvedGitContext(exactContext, panel);
    }

    private static AgentTarget ValidatePanelTarget(
        AgentTarget.Panel target,
        AgentContextPanel panel)
    {
        if (target != ExactPanelTarget(panel))
        {
            throw new ArgumentException("The Git panel target is stale.", nameof(target));
        }

        return target;
    }

    private static AgentTarget ValidateSessionTarget(
        AgentTarget.ConnectionSession target,
        AgentContextPanel panel)
    {
        if (panel.SessionId is not { } sessionId || target.SessionId != sessionId)
        {
            throw new ArgumentException("The Git session target is stale.", nameof(target));
        }

        return target;
    }

    private static AgentTarget.Panel ExactPanelTarget(AgentContextPanel panel) =>
        new(panel.WindowId, panel.WorkspaceId, panel.TabId, panel.PanelId);

    private static AgentApprovalPresentation CreatePresentation(
        AgentContextPanel panel,
        AgentGitRequest request)
    {
        var arguments = new List<AgentApprovalArgument>
        {
            new("panel_id", panel.PanelId.Value),
            new("operation", request.ToolName),
            new("repository", panel.PanelTitle ?? "Git repository"),
            new("repository_identity", panel.GitMetadata!.RepositoryIdentity.Value),
        };
        AppendRequestMaterial(arguments, request);
        return new AgentApprovalPresentation(
            request.IsMutation ? "Git repository change" : "Git repository observation",
            panel.GitMetadata.ConnectionDisplayName,
            workingDirectory: null,
            arguments);
    }

    private static void AppendRequestMaterial(
        List<AgentApprovalArgument> arguments,
        AgentGitRequest request)
    {
        switch (request)
        {
            case AgentGitRequest.ReadState:
                return;
            case AgentGitRequest.ReadDiff diff:
                AddState(arguments, diff.State);
                arguments.Add(new("change_ref", diff.Change.Value));
                arguments.Add(new("area", diff.Area.ToString()));
                return;
            case AgentGitRequest.ReadRemoteRef remote:
                AddState(arguments, remote.State);
                arguments.Add(new("remote_ref", remote.Remote.Value));
                arguments.Add(new("branch_ref", remote.Branch.Value));
                return;
            case AgentGitRequest.Stage stage:
                AddState(arguments, stage.State);
                arguments.Add(new("change_ref", stage.Change.Value));
                return;
            case AgentGitRequest.Unstage unstage:
                AddState(arguments, unstage.State);
                arguments.Add(new("change_ref", unstage.Change.Value));
                return;
            case AgentGitRequest.BranchCreate create:
                AddState(arguments, create.State);
                arguments.Add(new("new_branch", create.Name));
                return;
            case AgentGitRequest.BranchCheckout checkout:
                AddState(arguments, checkout.State);
                arguments.Add(new("branch_ref", checkout.Branch.Value));
                return;
            case AgentGitRequest.Commit commit:
                AddState(arguments, commit.State);
                arguments.Add(new("subject", commit.Subject));
                arguments.Add(new(
                    "body_length",
                    (commit.Body?.Length ?? 0).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
                return;
            case AgentGitRequest.Push push:
                AddState(arguments, push.State);
                arguments.Add(new("remote_state_ref", push.RemoteState.Value));
                arguments.Add(new("remote_ref", push.Remote.Value));
                arguments.Add(new("branch_ref", push.Branch.Value));
                arguments.Add(new("effect", "publishes one branch remotely"));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static void AddState(
        List<AgentApprovalArgument> arguments,
        GitStateReferenceId state) =>
        arguments.Add(new AgentApprovalArgument("state_ref", state.Value));

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentGitRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, actionId.Value);
        Append(hash, request.ToolName);
        Append(hash, request.PanelId.Value);
        foreach (var argument in RequestDigestMaterial(request))
        {
            Append(hash, argument);
        }

        return new AgentActionDigest(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static IEnumerable<string?> RequestDigestMaterial(AgentGitRequest request) =>
        request switch
        {
            AgentGitRequest.ReadState => [],
            AgentGitRequest.ReadDiff value =>
                [value.State.Value, value.Change.Value, value.Area.ToString()],
            AgentGitRequest.ReadRemoteRef value =>
                [value.State.Value, value.Remote.Value, value.Branch.Value],
            AgentGitRequest.Stage value => [value.State.Value, value.Change.Value],
            AgentGitRequest.Unstage value => [value.State.Value, value.Change.Value],
            AgentGitRequest.BranchCreate value => [value.State.Value, value.Name],
            AgentGitRequest.BranchCheckout value =>
                [value.State.Value, value.Branch.Value],
            AgentGitRequest.Commit value =>
                [value.State.Value, value.Subject, value.Body],
            AgentGitRequest.Push value =>
                [
                    value.State.Value,
                    value.RemoteState.Value,
                    value.Remote.Value,
                    value.Branch.Value,
                ],
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData([0xff, 0xff, 0xff, 0xff]);
            return;
        }

        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private sealed record ResolvedGitContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}
