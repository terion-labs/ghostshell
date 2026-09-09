using Asura.Docker;

namespace Asura.Application;

public sealed record AgentDockerStateSnapshot(
    DockerEngineGeneration EngineGeneration,
    DockerPanelSnapshot Snapshot);

public sealed record AgentDockerTextFileSnapshot(
    DockerResourceItem Resource,
    string Path,
    string Text,
    bool IsTruncated);

/// <summary>A detached, bounded result from one governed Docker observation.</summary>
public abstract record AgentDockerReadResult
{
    private AgentDockerReadResult(string toolName)
    {
        ToolName = toolName;
    }

    public string ToolName { get; }

    public sealed record State : AgentDockerReadResult
    {
        internal State(AgentDockerStateSnapshot value)
            : base(BuiltInAgentTools.DockerReadState) => Value = value;

        public AgentDockerStateSnapshot Value { get; }
    }

    public sealed record Inspection : AgentDockerReadResult
    {
        internal Inspection(DockerInspectionSnapshot value)
            : base(BuiltInAgentTools.DockerInspect) => Value = value;

        public DockerInspectionSnapshot Value { get; }
    }

    public sealed record Logs : AgentDockerReadResult
    {
        internal Logs(DockerContainerLogPage value)
            : base(BuiltInAgentTools.DockerLogs) => Value = value;

        public DockerContainerLogPage Value { get; }
    }

    public sealed record Files : AgentDockerReadResult
    {
        internal Files(DockerFilePage value)
            : base(BuiltInAgentTools.DockerFilesList) => Value = value;

        public DockerFilePage Value { get; }
    }

    public sealed record FileStat : AgentDockerReadResult
    {
        internal FileStat(DockerFileEntry value)
            : base(BuiltInAgentTools.DockerFilesStat) => Value = value;

        public DockerFileEntry Value { get; }
    }

    public sealed record FileText : AgentDockerReadResult
    {
        internal FileText(AgentDockerTextFileSnapshot value)
            : base(BuiltInAgentTools.DockerFileRead) => Value = value;

        public AgentDockerTextFileSnapshot Value { get; }
    }
}
