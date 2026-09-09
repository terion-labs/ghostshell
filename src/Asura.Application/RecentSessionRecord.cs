using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Privacy-bounded metadata for reopening and describing a definition-backed session.
/// <see cref="Title"/> is a snapshot of the durable definition's display name; callers
/// must never substitute a terminal title, command, output, credential, or secret value.
/// </summary>
public sealed record RecentSessionRecord
{
    public const int MaximumTitleLength = 200;

    public RecentSessionRecord(
        SessionId sessionId,
        DefinitionKey sourceDefinition,
        PanelKind kind,
        string title,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        RecentSessionOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value))
        {
            throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(sourceDefinition.Kind.Value)
            || string.IsNullOrWhiteSpace(sourceDefinition.Value))
        {
            throw new ArgumentException(
                "A stable source definition is required.",
                nameof(sourceDefinition));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length > MaximumTitleLength
            || normalizedTitle.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A recent-session title must be at most {MaximumTitleLength} printable characters.",
                nameof(title));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var normalizedStartedAt = startedAt.ToUniversalTime();
        var normalizedEndedAt = endedAt?.ToUniversalTime();
        if (outcome == RecentSessionOutcome.Active && normalizedEndedAt is not null)
        {
            throw new ArgumentException(
                "An active recent session cannot have an end timestamp.",
                nameof(endedAt));
        }

        if (outcome != RecentSessionOutcome.Active && normalizedEndedAt is null)
        {
            throw new ArgumentException(
                "A completed recent session requires an end timestamp.",
                nameof(endedAt));
        }

        if (normalizedEndedAt < normalizedStartedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endedAt),
                "A recent session cannot end before it starts.");
        }

        SessionId = sessionId;
        SourceDefinition = sourceDefinition;
        Kind = kind;
        Title = normalizedTitle;
        StartedAt = normalizedStartedAt;
        EndedAt = normalizedEndedAt;
        Outcome = outcome;
    }

    public SessionId SessionId { get; }

    public DefinitionKey SourceDefinition { get; }

    public PanelKind Kind { get; }

    public string Title { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? EndedAt { get; }

    public RecentSessionOutcome Outcome { get; }

    public DateTimeOffset LastUsedAt => EndedAt ?? StartedAt;
}
