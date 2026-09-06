using System.Collections.ObjectModel;
using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Run-owned MCP discovery and governed execution. Provider assemblies never
/// receive this interface or the underlying process transport.
/// </summary>
public interface IAgentMcpSessionHost
{
    ValueTask<AgentMcpHostResult<AgentMcpRunManifest>> OpenRunAsync(
        AgentMcpOpenRunRequest request,
        CancellationToken cancellationToken);

    ValueTask<AgentMcpHostResult<AgentMcpToolCallReceipt>> RunToolAsync(
        AgentAuthorizationId authorizationId,
        AgentMcpToolCallAction action,
        CancellationToken cancellationToken);

    ValueTask CloseRunAsync(
        AgentRunId runId,
        CancellationToken cancellationToken);
}

public sealed record AgentMcpOpenRunRequest
{
    public AgentMcpOpenRunRequest(
        AgentRunId runId,
        ActorDescriptor actor,
        WorkspaceInstanceId workspaceId,
        DateTimeOffset openedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(runId.Value))
        {
            throw new ArgumentException(
                "An MCP run requires an identifier.",
                nameof(runId));
        }

        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != ActorKind.Agent)
        {
            throw new ArgumentException(
                "An MCP run requires an authenticated agent actor.",
                nameof(actor));
        }

        if (openedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An MCP run timestamp must be UTC.",
                nameof(openedAtUtc));
        }

        RunId = runId;
        Actor = actor;
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException(
                "An MCP run requires a workspace identifier.",
                nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        OpenedAtUtc = openedAtUtc;
    }

    public AgentRunId RunId { get; }

    public ActorDescriptor Actor { get; }

    public WorkspaceInstanceId WorkspaceId { get; }

    public DateTimeOffset OpenedAtUtc { get; }
}

public sealed record AgentMcpRunManifest
{
    public AgentMcpRunManifest(
        AgentRunId runId,
        DateTimeOffset openedAtUtc,
        IReadOnlyList<AgentMcpToolManifest> tools)
    {
        if (string.IsNullOrWhiteSpace(runId.Value))
        {
            throw new ArgumentException(
                "An MCP run manifest requires an identifier.",
                nameof(runId));
        }

        if (openedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An MCP run manifest timestamp must be UTC.",
                nameof(openedAtUtc));
        }

        ArgumentNullException.ThrowIfNull(tools);
        var copies = tools
            .Select(tool => tool ?? throw new ArgumentException(
                "An MCP run manifest cannot contain null tools.",
                nameof(tools)))
            .OrderBy(tool => tool.ProviderAlias, StringComparer.Ordinal)
            .ToArray();
        if (copies
            .Select(tool => tool.ProviderAlias)
            .Distinct(StringComparer.Ordinal)
            .Count() != copies.Length)
        {
            throw new ArgumentException(
                "An MCP run manifest cannot contain duplicate provider aliases.",
                nameof(tools));
        }

        RunId = runId;
        OpenedAtUtc = openedAtUtc;
        Tools = new ReadOnlyCollection<AgentMcpToolManifest>(copies);
    }

    public AgentRunId RunId { get; }

    public DateTimeOffset OpenedAtUtc { get; }

    public IReadOnlyList<AgentMcpToolManifest> Tools { get; }
}

public sealed record AgentMcpToolCallReceipt
{
    public const string ContentOrigin = "untrusted_mcp";
    public const int MaximumProviderJsonBytes = 64 * 1024;

    public AgentMcpToolCallReceipt(
        string providerJson,
        bool isError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJson);
        if (System.Text.Encoding.UTF8.GetByteCount(providerJson)
            > MaximumProviderJsonBytes)
        {
            throw new ArgumentException(
                "An MCP tool receipt exceeds its provider projection limit.",
                nameof(providerJson));
        }

        ProviderJson = string.Concat(providerJson);
        IsError = isError;
    }

    public string ProviderJson { get; }

    public bool IsError { get; }
}

public abstract record AgentMcpHostResult<T>
{
    private AgentMcpHostResult()
    {
    }

    public sealed record Success(T Value) : AgentMcpHostResult<T>;

    public sealed record Failure(AgentMcpHostError Error)
        : AgentMcpHostResult<T>;
}

public sealed record AgentMcpHostError
{
    public AgentMcpHostError(
        string stableCode,
        string message,
        bool outcomeUnknown = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        if (stableCode.Length > 128
            || stableCode.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'))
        {
            throw new ArgumentException(
                "An MCP host error code must be a bounded stable identifier.",
                nameof(stableCode));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 512 || message.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An MCP host error message must be bounded and printable.",
                nameof(message));
        }

        StableCode = stableCode;
        Message = message;
        OutcomeUnknown = outcomeUnknown;
    }

    public string StableCode { get; }

    public string Message { get; }

    public bool OutcomeUnknown { get; }
}
