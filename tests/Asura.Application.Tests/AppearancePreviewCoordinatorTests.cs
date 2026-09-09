using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AppearancePreviewCoordinatorTests
{
    [Fact]
    public void Second_window_cannot_mutate_or_cancel_the_owner_preview()
    {
        var coordinator = new AppearancePreviewCoordinator();
        var acquisition = coordinator.TryAcquire("window-1", 11, 21);
        Assert.NotNull(acquisition.Lease);
        using var owner = acquisition.Lease;
        Assert.True(owner.PreviewTheme(ThemePreference.Default));

        var conflict = coordinator.TryAcquire("window-2", 12, 22);

        Assert.False(conflict.IsSuccess);
        Assert.Contains("another window", conflict.Conflict, StringComparison.Ordinal);
        Assert.Equal("window-1", coordinator.Current.OwnerId);
        Assert.Equal(ThemePreference.Default, coordinator.Current.Theme);
    }

    [Fact]
    public void Lease_captures_baselines_and_releases_only_after_both_sections_clear()
    {
        var coordinator = new AppearancePreviewCoordinator();
        var acquisition = coordinator.TryAcquire("window-1", 11, 21);
        Assert.NotNull(acquisition.Lease);
        using var lease = acquisition.Lease;
        var render = TerminalRenderProfileSnapshot.FromProfile(Profile());
        Assert.Equal(11, lease.BaselineThemeRevision);
        Assert.Equal(21, lease.BaselineTerminalRevision);
        Assert.True(lease.PreviewTheme(ThemePreference.Default));
        Assert.True(lease.PreviewTerminal(render));
        Assert.True(lease.AdvanceThemeBaseline(12));
        Assert.True(lease.AdvanceTerminalBaseline(22));
        Assert.Equal(12, lease.BaselineThemeRevision);
        Assert.Equal(22, lease.BaselineTerminalRevision);

        Assert.True(lease.ClearTheme());
        Assert.Equal("window-1", coordinator.Current.OwnerId);
        Assert.True(coordinator.Current.HasTerminalDraft);
        Assert.True(lease.ClearTerminal());
        Assert.True(coordinator.Current.IsEmpty);
        Assert.True(coordinator.TryAcquire("window-2", 12, 22).IsSuccess);
    }

    [Fact]
    public void Disposing_owner_restores_an_empty_coordinator()
    {
        var coordinator = new AppearancePreviewCoordinator();
        var acquisition = coordinator.TryAcquire("window-1", null, null);
        Assert.NotNull(acquisition.Lease);
        var lease = acquisition.Lease;
        Assert.True(lease.PreviewTheme(ThemePreference.Default));

        lease.Dispose();

        Assert.True(coordinator.Current.IsEmpty);
        Assert.True(coordinator.TryAcquire("window-2", null, null).IsSuccess);
    }

    private static TerminalProfile Profile() => new(
        new TerminalProfileId("terminal"),
        "Terminal",
        "JetBrains Mono",
        14,
        1.2,
        TerminalCursorStyle.Block,
        true,
        10_000,
        TerminalPalette.AsuraDark,
        BuiltInKeymaps.LinuxTerminalId);
}
