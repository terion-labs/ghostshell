using Asura.Core;
using Asura.Docker;

namespace Asura.Application;

/// <summary>
/// One closed Docker lifecycle mutation. It carries the opaque resource and
/// one-shot state revision returned by a preceding governed state read.
/// </summary>
public sealed record AgentDockerControlRequest
{
    public AgentDockerControlRequest(
        PanelInstanceId panelId,
        DockerResourceReferenceId container,
        DockerEngineGeneration engineGeneration,
        DockerContainerRevision containerRevision,
        DockerContainerAction action,
        string expectedState)
    {
        if (string.IsNullOrWhiteSpace(panelId.Value)
            || panelId.Value.Length > 256
            || panelId.Value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Docker mutation requires a bounded panel identifier.",
                nameof(panelId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);
        if (expectedState.Length > 64 || expectedState.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Docker mutation requires a bounded expected state.",
                nameof(expectedState));
        }

        PanelId = panelId;
        Container = container;
        EngineGeneration = engineGeneration;
        ContainerRevision = containerRevision;
        Action = action;
        ExpectedState = expectedState.Trim().ToLowerInvariant();
        (ToolName, RequiredSessionCapability) = action switch
        {
            DockerContainerAction.Start => (
                BuiltInAgentTools.DockerContainerStart,
                SessionCapabilities.DockerContainerStart),
            DockerContainerAction.Stop => (
                BuiltInAgentTools.DockerContainerStop,
                SessionCapabilities.DockerContainerStop),
            DockerContainerAction.Restart => (
                BuiltInAgentTools.DockerContainerRestart,
                SessionCapabilities.DockerContainerRestart),
            DockerContainerAction.Pause => (
                BuiltInAgentTools.DockerContainerPause,
                SessionCapabilities.DockerContainerPause),
            DockerContainerAction.Resume => (
                BuiltInAgentTools.DockerContainerResume,
                SessionCapabilities.DockerContainerResume),
            DockerContainerAction.Remove => (
                BuiltInAgentTools.DockerContainerRemove,
                SessionCapabilities.DockerContainerRemove),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
        if (!AllowsExpectedState(action, ExpectedState))
        {
            throw new ArgumentException(
                "The expected state is not valid for this Docker lifecycle operation.",
                nameof(expectedState));
        }
    }

    public PanelInstanceId PanelId { get; }

    public DockerResourceReferenceId Container { get; }

    public DockerEngineGeneration EngineGeneration { get; }

    public DockerContainerRevision ContainerRevision { get; }

    public DockerContainerAction Action { get; }

    public string ExpectedState { get; }

    public string ToolName { get; }

    public string RequiredSessionCapability { get; }

    public DockerContainerControlRequest ToSessionRequest() => new(
        Container,
        EngineGeneration,
        ContainerRevision,
        Action,
        ExpectedState);

    private static bool AllowsExpectedState(
        DockerContainerAction action,
        string expectedState) => action switch
        {
            DockerContainerAction.Start => expectedState is "created" or "exited",
            DockerContainerAction.Stop
                or DockerContainerAction.Restart
                or DockerContainerAction.Pause => string.Equals(
                    expectedState,
                    "running",
                    StringComparison.Ordinal),
            DockerContainerAction.Resume => string.Equals(
                expectedState,
                "paused",
                StringComparison.Ordinal),
            DockerContainerAction.Remove => expectedState is "created" or "exited" or "dead",
            _ => false,
        };
}
