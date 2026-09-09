using Asura.Docker;

namespace Asura.Application;

public readonly record struct DockerContainerRevision
{
    public DockerContainerRevision(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "A Docker container revision must be an opaque bounded token.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record DockerContainerControlRequest(
    DockerResourceReferenceId Container,
    DockerEngineGeneration EngineGeneration,
    DockerContainerRevision ContainerRevision,
    DockerContainerAction Action,
    string ExpectedState);

public enum DockerContainerControlOutcome
{
    Applied,
    NotDispatched,
    OutcomeUnknown,
}

public sealed record DockerContainerControlResult(
    DockerContainerControlOutcome Outcome,
    string StableCode,
    bool Retryable);
