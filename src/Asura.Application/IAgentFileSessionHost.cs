using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// The only File Viewer execution port accepted by the governed agent path. The host consumes
/// one-action authorization after re-resolving the exact live session and trusted provider root.
/// </summary>
public interface IAgentFileSessionHost
{
    ValueTask<HostResult<AgentFileActionResult>> RunAgentFileActionAsync(
        AgentAuthorizationId authorizationId,
        AgentFileAction action,
        CancellationToken cancellationToken);
}

public abstract record AgentFileActionResult
{
    private AgentFileActionResult()
    {
    }

    public sealed record Page(FilePanelPage Value) : AgentFileActionResult;

    public sealed record SearchResults(
        ImmutableArray<FilePanelEntry> Entries,
        bool IsTruncated)
        : AgentFileActionResult;

    public sealed record Entry(
        FilePanelEntry Value,
        AgentFileEntryReference? Reference = null)
        : AgentFileActionResult;

    public sealed record Preview(FilePanelPreview Value) : AgentFileActionResult;

    public sealed record AccessControl(
        FilePanelAccessControl Value,
        bool IsTruncated = false)
        : AgentFileActionResult;

    public sealed record Transfers(
        ImmutableArray<FilePanelTransferSnapshot> Values,
        bool IsTruncated)
        : AgentFileActionResult;

    public sealed record CreatedDirectory(FilePanelEntry Value) : AgentFileActionResult;

    public sealed record CreatedText(FilePanelTextWriteReceipt Value) : AgentFileActionResult;

    public sealed record ReplacedText(FilePanelTextWriteReceipt Value) : AgentFileActionResult;

    public sealed record Copied(FilePanelCopyReceipt Value) : AgentFileActionResult;

    public sealed record Moved(FilePanelEntry Value) : AgentFileActionResult;

    public sealed record Deleted(FilePanelDeleteReceipt Value) : AgentFileActionResult;
}
