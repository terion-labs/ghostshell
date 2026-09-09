using System.Text;
using Asura.Core;
using Asura.Git;

namespace Asura.Application;

public static class GitAgentToolNames
{
    public const string ReadState = "git.read_state";
    public const string ReadDiff = "git.read_diff";
    public const string ReadRemoteRef = "git.read_remote_ref";
    public const string Stage = "git.stage";
    public const string Unstage = "git.unstage";
    public const string BranchCreate = "git.branch_create";
    public const string BranchCheckout = "git.branch_checkout";
    public const string Commit = "git.commit";
    public const string Push = "git.push";
}

public abstract record AgentGitRequest
{
    private const int MaximumCommitBytes = 32 * 1024;

    private AgentGitRequest(
        PanelInstanceId panelId,
        string toolName,
        string requiredSessionCapability,
        bool isMutation)
    {
        if (string.IsNullOrWhiteSpace(panelId.Value)
            || panelId.Value.Length > 256
            || panelId.Value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Git action requires a bounded panel identifier.",
                nameof(panelId));
        }

        PanelId = panelId;
        ToolName = toolName;
        RequiredSessionCapability = requiredSessionCapability;
        IsMutation = isMutation;
    }

    public PanelInstanceId PanelId { get; }

    public string ToolName { get; }

    public string RequiredSessionCapability { get; }

    public bool IsMutation { get; }

    public sealed record ReadState : AgentGitRequest
    {
        public ReadState(PanelInstanceId panelId)
            : base(panelId, GitAgentToolNames.ReadState, SessionCapabilities.GitReadState, false)
        {
        }
    }

    public sealed record ReadDiff : AgentGitRequest
    {
        public ReadDiff(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            GitChangeReferenceId change,
            GitChangeArea area)
            : base(panelId, GitAgentToolNames.ReadDiff, SessionCapabilities.GitReadDiff, false)
        {
            if (!Enum.IsDefined(area))
            {
                throw new ArgumentOutOfRangeException(nameof(area));
            }

            State = state;
            Change = change;
            Area = area;
        }

        public GitStateReferenceId State { get; }

        public GitChangeReferenceId Change { get; }

        public GitChangeArea Area { get; }
    }

    public sealed record ReadRemoteRef : AgentGitRequest
    {
        public ReadRemoteRef(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            GitRemoteReferenceId remote,
            GitBranchReferenceId branch)
            : base(
                panelId,
                GitAgentToolNames.ReadRemoteRef,
                SessionCapabilities.GitReadRemoteRef,
                false)
        {
            State = state;
            Remote = remote;
            Branch = branch;
        }

        public GitStateReferenceId State { get; }

        public GitRemoteReferenceId Remote { get; }

        public GitBranchReferenceId Branch { get; }
    }

    public sealed record Stage : AgentGitRequest
    {
        public Stage(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            GitChangeReferenceId change)
            : base(panelId, GitAgentToolNames.Stage, SessionCapabilities.GitStage, true)
        {
            State = state;
            Change = change;
        }

        public GitStateReferenceId State { get; }

        public GitChangeReferenceId Change { get; }
    }

    public sealed record Unstage : AgentGitRequest
    {
        public Unstage(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            GitChangeReferenceId change)
            : base(panelId, GitAgentToolNames.Unstage, SessionCapabilities.GitUnstage, true)
        {
            State = state;
            Change = change;
        }

        public GitStateReferenceId State { get; }

        public GitChangeReferenceId Change { get; }
    }

    public sealed record BranchCreate : AgentGitRequest
    {
        public BranchCreate(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            string name)
            : base(
                panelId,
                GitAgentToolNames.BranchCreate,
                SessionCapabilities.GitBranchCreate,
                true)
        {
            State = state;
            Name = RequireText(name, 256, nameof(name));
        }

        public GitStateReferenceId State { get; }

        public string Name { get; }
    }

    public sealed record BranchCheckout : AgentGitRequest
    {
        public BranchCheckout(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            GitBranchReferenceId branch)
            : base(
                panelId,
                GitAgentToolNames.BranchCheckout,
                SessionCapabilities.GitBranchCheckout,
                true)
        {
            State = state;
            Branch = branch;
        }

        public GitStateReferenceId State { get; }

        public GitBranchReferenceId Branch { get; }
    }

    public sealed record Commit : AgentGitRequest
    {
        public Commit(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            string subject,
            string? body)
            : base(panelId, GitAgentToolNames.Commit, SessionCapabilities.GitCommit, true)
        {
            State = state;
            Subject = RequireText(subject, 512, nameof(subject));
            Body = body is null
                ? null
                : RequireText(body, MaximumCommitBytes, nameof(body), allowNewLines: true);
        }

        public GitStateReferenceId State { get; }

        public string Subject { get; }

        public string? Body { get; }
    }

    public sealed record Push : AgentGitRequest
    {
        public Push(
            PanelInstanceId panelId,
            GitStateReferenceId state,
            GitRemoteStateReferenceId remoteState,
            GitRemoteReferenceId remote,
            GitBranchReferenceId branch)
            : base(panelId, GitAgentToolNames.Push, SessionCapabilities.GitPush, true)
        {
            State = state;
            RemoteState = remoteState;
            Remote = remote;
            Branch = branch;
        }

        public GitStateReferenceId State { get; }

        public GitRemoteStateReferenceId RemoteState { get; }

        public GitRemoteReferenceId Remote { get; }

        public GitBranchReferenceId Branch { get; }
    }

    private static string RequireText(
        string value,
        int maximumBytes,
        string parameterName,
        bool allowNewLines = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes
            || value.Any(character => character == '\0'
                || (!allowNewLines && char.IsControl(character))
                || allowNewLines && char.IsControl(character)
                    && character is not '\r' and not '\n' and not '\t')
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                "A Git action argument is invalid or contains literal secret material.",
                parameterName);
        }

        return string.Concat(value);
    }
}
