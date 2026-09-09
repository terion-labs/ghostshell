namespace Asura.App.Tests;

public sealed class QuickTerminalTransitionTimelineTests
{
    [Fact]
    public void Reversing_show_to_hide_rejects_the_stale_show_completion()
    {
        var timeline = new QuickTerminalTransitionTimeline();
        const long startedAt = 1_000;

        var showGeneration = timeline.Begin(
            from: 0,
            to: 1,
            durationMilliseconds: 200,
            startedAt: startedAt);

        Assert.Equal(QuickTerminalVisibilityState.Showing, timeline.State);
        Assert.Equal(200, timeline.DurationMilliseconds);

        var reversedAt = timeline.Pause(startedAt + 100);

        Assert.Equal(0.875, reversedAt, precision: 3);

        var hideGeneration = timeline.Begin(
            from: reversedAt,
            to: 0,
            durationMilliseconds: 200,
            startedAt: startedAt + 100);

        Assert.Equal(QuickTerminalVisibilityState.Hiding, timeline.State);
        Assert.Equal(175, timeline.DurationMilliseconds);
        Assert.False(timeline.TryComplete(showGeneration));
        Assert.Equal(QuickTerminalVisibilityState.Hiding, timeline.State);
        Assert.Equal(hideGeneration, timeline.Generation);
        Assert.Equal(175, timeline.DurationMilliseconds);

        Assert.True(timeline.TryComplete(hideGeneration));
        Assert.Equal(QuickTerminalVisibilityState.Hidden, timeline.State);
        Assert.Equal(0, timeline.Progress);
    }
}
