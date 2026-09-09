using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class RecentSessionContractsTests
{
    private static readonly DateTimeOffset StartedAt = new(
        2026,
        7,
        22,
        12,
        0,
        0,
        TimeSpan.FromHours(3));

    [Fact]
    public void ActiveRecordNormalizesBoundedDefinitionMetadata()
    {
        var record = new RecentSessionRecord(
            new SessionId("session-1"),
            new DefinitionKey(DefinitionKind.Connection, "connection-1"),
            PanelKind.Terminal,
            "  Production shell  ",
            StartedAt,
            endedAt: null,
            RecentSessionOutcome.Active);

        Assert.Equal("Production shell", record.Title);
        Assert.Equal(TimeSpan.Zero, record.StartedAt.Offset);
        Assert.Equal(record.StartedAt, record.LastUsedAt);
    }

    [Fact]
    public void OutcomeAndTimestampsMustDescribeAReachableLifecycleState()
    {
        Assert.Throws<ArgumentException>(() => new RecentSessionRecord(
            new SessionId("session-active-ended"),
            new DefinitionKey(DefinitionKind.Connection, "connection-1"),
            PanelKind.Terminal,
            "Production shell",
            StartedAt,
            StartedAt.AddMinutes(1),
            RecentSessionOutcome.Active));
        Assert.Throws<ArgumentException>(() => new RecentSessionRecord(
            new SessionId("session-closed-open"),
            new DefinitionKey(DefinitionKind.Connection, "connection-1"),
            PanelKind.Terminal,
            "Production shell",
            StartedAt,
            endedAt: null,
            RecentSessionOutcome.GracefullyClosed));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentSessionRecord(
            new SessionId("session-time-travel"),
            new DefinitionKey(DefinitionKind.Connection, "connection-1"),
            PanelKind.Terminal,
            "Production shell",
            StartedAt,
            StartedAt.AddMinutes(-1),
            RecentSessionOutcome.Failed));
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("tab\ttitle")]
    public void TitleRejectsControlCharacters(string title)
    {
        Assert.Throws<ArgumentException>(() => new RecentSessionRecord(
            new SessionId("session-1"),
            new DefinitionKey(DefinitionKind.Connection, "connection-1"),
            PanelKind.Terminal,
            title,
            StartedAt,
            endedAt: null,
            RecentSessionOutcome.Active));
    }

    [Fact]
    public void CompletionCannotUseTheActiveOutcome()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentSessionCompletion(
            new SessionId("session-1"),
            StartedAt.AddMinutes(1),
            RecentSessionOutcome.Active));
    }

    [Fact]
    public void QueriesAreAlwaysBounded()
    {
        Assert.Equal(
            RecentSessionQuery.DefaultLimit,
            new RecentSessionQuery().Limit);
        Assert.Equal(1_000, RecentSessionQuery.MaximumLimit);
        Assert.Equal(
            RecentSessionQuery.MaximumLimit,
            new RecentSessionQuery(RecentSessionQuery.MaximumLimit).Limit);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentSessionQuery(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentSessionQuery(
            RecentSessionQuery.MaximumLimit + 1));
    }

    [Fact]
    public void RetentionCanDisableHistoryButCannotBecomeUnbounded()
    {
        var disabled = new RecentSessionRetentionPolicy(
            maximumEntries: 0,
            TimeSpan.FromDays(1));

        Assert.False(disabled.IsEnabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentSessionRetentionPolicy(
            RecentSessionRetentionPolicy.MaximumSupportedEntries + 1,
            TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentSessionRetentionPolicy(
            maximumEntries: 1,
            RecentSessionRetentionPolicy.MaximumSupportedAge + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void StoredRetentionAndUpdateResultsRequireReachableValues()
    {
        var stored = new StoredRecentSessionRetentionPolicy(
            RecentSessionRetentionPolicy.Default,
            revision: 3);
        var update = new RecentSessionRetentionUpdateResult(
            stored,
            prunedSessionCount: 2);

        Assert.Same(RecentSessionRetentionPolicy.Default, stored.Policy);
        Assert.Equal(3, stored.Revision);
        Assert.Same(stored, update.StoredPolicy);
        Assert.Equal(2, update.PrunedSessionCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StoredRecentSessionRetentionPolicy(
                RecentSessionRetentionPolicy.Default,
                revision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecentSessionRetentionUpdateResult(stored, prunedSessionCount: -1));
    }

    [Fact]
    public void RecordShapeHasNoArbitraryPayloadOrDetailField()
    {
        var propertyNames = typeof(RecentSessionRecord)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "EndedAt",
                "Kind",
                "LastUsedAt",
                "Outcome",
                "SessionId",
                "SourceDefinition",
                "StartedAt",
                "Title",
            },
            propertyNames);
    }
}
