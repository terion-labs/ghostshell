using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class RecentSessionHistoryProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Search_ranks_title_matches_before_typed_metadata_matches()
    {
        var secondary = Item("secondary", "Operations", sourceId: "alpha");
        var contains = Item("contains", "My alpha dashboard");
        var prefix = Item("prefix", "Alpha production");
        var exact = Item("exact", "alpha");

        var results = RecentSessionHistoryProjection.Search(
            "  ALPHA ",
            [secondary, contains, prefix, exact]);

        Assert.Equal(
            ["exact", "prefix", "contains", "secondary"],
            results.Select(item => item.SessionId.Value), StringComparer.Ordinal);
    }

    [Fact]
    public void Search_is_case_insensitive_and_covers_session_panel_and_outcome_metadata()
    {
        var failedBrowser = Item(
            "session-special",
            "Unrelated",
            PanelKind.Browser,
            RecentSessionOutcome.Failed);

        Assert.Single(RecentSessionHistoryProjection.Search("SESSION-SPECIAL", [failedBrowser]));
        Assert.Single(RecentSessionHistoryProjection.Search("browser", [failedBrowser]));
        Assert.Single(RecentSessionHistoryProjection.Search("failed", [failedBrowser]));
        Assert.Empty(RecentSessionHistoryProjection.Search("terminal output", [failedBrowser]));
    }

    [Fact]
    public void Search_matches_the_spaced_labels_shown_to_the_user()
    {
        var item = Item(
            "visible-labels",
            "Unrelated",
            PanelKind.FileViewer,
            RecentSessionOutcome.ForceTerminated);

        Assert.Single(RecentSessionHistoryProjection.Search("File Viewer", [item]));
        Assert.Single(RecentSessionHistoryProjection.Search("Force terminated", [item]));
    }

    [Fact]
    public void Equal_scores_have_deterministic_newest_first_order()
    {
        var older = Item("older", "Session", startedAt: Now.AddHours(-3));
        var newest = Item("newest", "Session", startedAt: Now.AddHours(-1));

        var forward = RecentSessionHistoryProjection.Search(string.Empty, [older, newest]);
        var reverse = RecentSessionHistoryProjection.Search(string.Empty, [newest, older]);

        Assert.Equal(["newest", "older"], forward.Select(item => item.SessionId.Value), StringComparer.Ordinal);
        Assert.Equal(forward.Select(item => item.SessionId), reverse.Select(item => item.SessionId));
    }

    [Fact]
    public void Selection_is_preserved_by_session_id_even_when_definition_is_stale()
    {
        var first = Item("first", "First");
        var stale = Item("stale", "Stale", canOpen: false);
        var refreshedStale = Item("stale", "Stale", canOpen: false);

        var selected = RecentSessionHistoryProjection.ResolveSelection(
            [first, refreshedStale],
            stale.SessionId);

        Assert.Same(refreshedStale, selected);
        Assert.NotNull(selected);
        Assert.False(selected.CanOpen);
        Assert.Contains("no longer exists", selected.ReopenStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detail_strings_use_the_explicit_observation_time_and_utc_metadata()
    {
        var item = Item(
            "detail",
            "Detail",
            startedAt: Now.AddMinutes(-5),
            endedAt: Now.AddMinutes(-2));

        Assert.Equal("2 min ago", item.LastUsed);
        Assert.Equal("2026-07-23 11:55:00 UTC", item.Started);
        Assert.Equal("2026-07-23 11:58:00 UTC", item.Ended);
        Assert.Equal("3 min", item.Duration);
        Assert.Equal("Terminal · Closed", item.Detail);
    }

    /// <summary>
    /// Record equality cannot answer this: every refresh stamps a new
    /// <c>ObservedAt</c>, so two projections of the same session are never equal
    /// and the recent-sessions list rebuilt every row on every refresh — dropping
    /// whatever the pointer was hovering.
    /// </summary>
    [Fact]
    public void Two_projections_of_the_same_session_present_the_same()
    {
        var first = Item("session", "Prod web");
        var second = Item("session", "Prod web") with { ObservedAt = Now.AddSeconds(3) };

        Assert.NotEqual(first, second);
        Assert.True(first.PresentsSameAs(second));
    }

    [Fact]
    public void A_row_that_can_no_longer_be_reopened_presents_differently()
    {
        Assert.False(
            Item("session", "Prod web")
                .PresentsSameAs(Item("session", "Prod web", canOpen: false)));
    }

    /// <summary>
    /// The relative timestamp is on the row, so a projection observed far enough
    /// later reads differently and has to count as a change.
    /// </summary>
    [Fact]
    public void A_row_whose_relative_timestamp_moved_on_presents_differently()
    {
        var first = Item("session", "Prod web");
        var later = Item("session", "Prod web") with { ObservedAt = Now.AddHours(4) };

        Assert.False(first.PresentsSameAs(later));
    }

    private static RecentSessionHistoryItemViewModel Item(
        string id,
        string title,
        PanelKind kind = PanelKind.Terminal,
        RecentSessionOutcome outcome = RecentSessionOutcome.GracefullyClosed,
        string sourceId = "source",
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null,
        bool canOpen = true)
    {
        var started = startedAt ?? Now.AddMinutes(-2);
        DateTimeOffset? ended = outcome == RecentSessionOutcome.Active
            ? null
            : endedAt ?? started.AddMinutes(1);
        return new RecentSessionHistoryItemViewModel(
            new RecentSessionRecord(
                new SessionId(id),
                new DefinitionKey(ConnectionProfile.Kind, sourceId),
                kind,
                title,
                started,
                ended,
                outcome),
            canOpen,
            Now);
    }
}
