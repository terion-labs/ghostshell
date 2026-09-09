namespace Asura.Application;

public enum AuditOutcome
{
    Requested,
    Approved,
    Started,
    Succeeded,
    Denied,
    Failed,
    Cancelled,
}

public sealed record AuditTarget(string Kind, string Id)
{
    public AuditTarget Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        return this;
    }
}

public sealed record AuditEventRecord(
    string EventId,
    string CorrelationId,
    ActorDescriptor Actor,
    string Action,
    AuditTarget? Target,
    AuditOutcome Outcome,
    AuditDetails Details,
    DateTimeOffset OccurredAt);
