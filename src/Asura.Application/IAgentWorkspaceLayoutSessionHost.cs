using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Governed dispatch for layout mutations. SessionHost consumes authorization
/// and verifies the final graph; the trusted desktop port owns visual layout.
/// </summary>
public interface IAgentWorkspaceLayoutSessionHost
{
    ValueTask<HostResult<AgentWorkspaceLayoutReceipt>>
        RunAgentWorkspaceLayoutActionAsync(
            AgentAuthorizationId authorizationId,
            AgentWorkspaceLayoutAction action,
            IAgentWorkspaceLayoutMutationPort mutationPort,
            CancellationToken cancellationToken);
}

public interface IAgentWorkspaceLayoutMutationPort
{
    WindowInstanceId WindowId { get; }

    WorkspaceInstanceId WorkspaceId { get; }

    IReadOnlySet<PanelKind> SupportedPanelKinds { get; }

    ValueTask<AgentWorkspaceLayoutMutationResult> MutateAsync(
        AgentWorkspaceLayoutRequest request,
        long expectedWorkspaceRevision,
        CancellationToken cancellationToken);
}

public abstract record AgentWorkspaceLayoutMutationResult
{
    private AgentWorkspaceLayoutMutationResult()
    {
    }

    public sealed record Applied(
        WorkspaceGraphSnapshot Snapshot,
        TabInstanceId? TabId,
        PanelInstanceId? PanelId,
        PanelKind? PanelKind,
        bool IsPanelReady = false) : AgentWorkspaceLayoutMutationResult;

    public sealed record Observed(
        WorkspaceGraphSnapshot Snapshot,
        IReadOnlyList<AgentWorkspaceConnectionOption> Connections)
        : AgentWorkspaceLayoutMutationResult;

    public sealed record Rejected(string StableCode)
        : AgentWorkspaceLayoutMutationResult;

    public sealed record OutcomeUnknown : AgentWorkspaceLayoutMutationResult;
}

public sealed record AgentWorkspaceLayoutReceipt
{
    public AgentWorkspaceLayoutReceipt(
        string operation,
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        long workspaceRevision,
        long graphSequence,
        TabInstanceId? tabId,
        PanelInstanceId? panelId,
        PanelKind? panelKind,
        IReadOnlyList<AgentWorkspaceConnectionOption>? connections = null,
        bool isPanelReady = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentOutOfRangeException.ThrowIfNegative(workspaceRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(graphSequence);
        Operation = string.Concat(operation);
        WindowId = windowId;
        WorkspaceId = workspaceId;
        WorkspaceRevision = workspaceRevision;
        GraphSequence = graphSequence;
        TabId = tabId;
        PanelId = panelId;
        PanelKind = panelKind;
        IsPanelReady = isPanelReady;
        Connections = Array.AsReadOnly((connections ?? []).ToArray());
    }

    public string Operation { get; }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }

    public long WorkspaceRevision { get; }

    public long GraphSequence { get; }

    public TabInstanceId? TabId { get; }

    public PanelInstanceId? PanelId { get; }

    public PanelKind? PanelKind { get; }

    /// <summary>
    /// True only when the created or connected panel has a live hosted
    /// session and can be targeted by its operational agent tools.
    /// </summary>
    public bool IsPanelReady { get; }

    public IReadOnlyList<AgentWorkspaceConnectionOption> Connections { get; }
}

public sealed record AgentWorkspaceConnectionOption
{
    public AgentWorkspaceConnectionOption(
        string reference,
        string name,
        string kind,
        IReadOnlyCollection<PanelKind> supportedPanelKinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(supportedPanelKinds);
        if (reference.Length > 128 || name.Length > 256 || kind.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(reference));
        }

        Reference = string.Concat(reference);
        Name = string.Concat(name);
        Kind = string.Concat(kind);
        SupportedPanelKinds = Array.AsReadOnly(supportedPanelKinds
            .Where(AgentWorkspaceLayoutRequest.IsCreatableKind)
            .Distinct()
            .OrderBy(value => (int)value)
            .ToArray());
    }

    public string Reference { get; }

    public string Name { get; }

    public string Kind { get; }

    public IReadOnlyList<PanelKind> SupportedPanelKinds { get; }
}
