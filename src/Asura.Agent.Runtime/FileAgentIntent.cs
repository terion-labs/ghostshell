using System.Collections.Immutable;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record FileAgentIntent
{
    private FileAgentIntent()
    {
    }

    public abstract ImmutableArray<FilePanelPathSegment> RelativePath { get; }

    public sealed record List : FileAgentIntent
    {
        public List(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record Search : FileAgentIntent
    {
        public Search(
            IEnumerable<FilePanelPathSegment> pathSegments,
            string query,
            FilePanelDiscoveryScope scope,
            int maximumResults)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
            Query = query;
            Scope = scope;
            MaximumResults = maximumResults;
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }

        public string Query { get; }

        public FilePanelDiscoveryScope Scope { get; }

        public int MaximumResults { get; }
    }

    public sealed record Stat : FileAgentIntent
    {
        public Stat(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record Read : FileAgentIntent
    {
        public Read(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record AccessRead : FileAgentIntent
    {
        public AccessRead(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record Transfers : FileAgentIntent
    {
        public override ImmutableArray<FilePanelPathSegment> RelativePath => [];
    }

    public sealed record CreateDirectory : FileAgentIntent
    {
        public CreateDirectory(IEnumerable<FilePanelPathSegment> pathSegments)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }
    }

    public sealed record CreateText : FileAgentIntent
    {
        public CreateText(IEnumerable<FilePanelPathSegment> pathSegments, string content)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }

        public string Content { get; }
    }

    public sealed record ReplaceText : FileAgentIntent
    {
        public ReplaceText(
            IEnumerable<FilePanelPathSegment> pathSegments,
            AgentFileEntryReference entryReference,
            string content)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
            EntryReference = entryReference;
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }

        public AgentFileEntryReference EntryReference { get; }

        public string Content { get; }
    }

    public sealed record Copy : FileAgentIntent
    {
        public Copy(
            IEnumerable<FilePanelPathSegment> sourcePathSegments,
            AgentFileEntryReference entryReference,
            IEnumerable<FilePanelPathSegment> destinationPathSegments)
        {
            ArgumentNullException.ThrowIfNull(sourcePathSegments);
            ArgumentNullException.ThrowIfNull(destinationPathSegments);
            RelativePath = [.. sourcePathSegments];
            EntryReference = entryReference;
            DestinationRelativePath = [.. destinationPathSegments];
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }

        public AgentFileEntryReference EntryReference { get; }

        public ImmutableArray<FilePanelPathSegment> DestinationRelativePath { get; }
    }

    public sealed record Move : FileAgentIntent
    {
        public Move(
            IEnumerable<FilePanelPathSegment> sourcePathSegments,
            AgentFileEntryReference entryReference,
            IEnumerable<FilePanelPathSegment> destinationPathSegments)
        {
            ArgumentNullException.ThrowIfNull(sourcePathSegments);
            ArgumentNullException.ThrowIfNull(destinationPathSegments);
            RelativePath = [.. sourcePathSegments];
            EntryReference = entryReference;
            DestinationRelativePath = [.. destinationPathSegments];
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }

        public AgentFileEntryReference EntryReference { get; }

        public ImmutableArray<FilePanelPathSegment> DestinationRelativePath { get; }
    }

    public sealed record Delete : FileAgentIntent
    {
        public Delete(
            IEnumerable<FilePanelPathSegment> pathSegments,
            AgentFileEntryReference entryReference,
            bool recursive = false)
        {
            ArgumentNullException.ThrowIfNull(pathSegments);
            RelativePath = [.. pathSegments];
            EntryReference = entryReference;
            Recursive = recursive;
        }

        public override ImmutableArray<FilePanelPathSegment> RelativePath { get; }

        public AgentFileEntryReference EntryReference { get; }

        public bool Recursive { get; }
    }
}

internal abstract record FileAgentIntentResult
{
    private FileAgentIntentResult()
    {
    }

    public sealed record Parsed(
        FileAgentIntent Intent,
        PanelInstanceId? PanelId = null)
        : FileAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : FileAgentIntentResult;
}
