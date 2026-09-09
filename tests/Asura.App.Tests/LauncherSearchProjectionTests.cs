using Asura.App.ViewModels;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.Tests;

public sealed class LauncherSearchProjectionTests
{
    [Fact]
    public void Search_ranks_exact_then_prefix_then_contains_then_secondary_matches()
    {
        var secondary = Result(
            new LauncherSearchTarget.Workspace(new WorkspaceId("secondary")),
            "Operations",
            searchTerms: ["alpha"]);
        var contains = Result(
            new LauncherSearchTarget.Screen(new ScreenId("contains")),
            "My alpha dashboard");
        var prefix = Result(
            new LauncherSearchTarget.Connection(new ConnectionId("prefix")),
            "Alpha production");
        var exact = Result(
            new LauncherSearchTarget.Command(new CommandId("exact")),
            "alpha");

        var results = LauncherSearchProjection.Search(
            "  ALPHA ",
            [secondary, contains, prefix, exact]);

        Assert.Collection(
            results,
            item => Assert.Equal(new LauncherSearchTarget.Command(
                new CommandId("exact")), item.Target),
            item => Assert.Equal(new LauncherSearchTarget.Connection(
                new ConnectionId("prefix")), item.Target),
            item => Assert.Equal(new LauncherSearchTarget.Screen(
                new ScreenId("contains")), item.Target),
            item => Assert.Equal(new LauncherSearchTarget.Workspace(
                new WorkspaceId("secondary")), item.Target));
    }

    [Fact]
    public void Equal_scores_are_stable_by_availability_kind_title_and_target_id()
    {
        var candidates = new[]
        {
            Result(
                new LauncherSearchTarget.Command(new CommandId("disabled-command")),
                "A command",
                isAvailable: false,
                unavailableReason: "Unavailable"),
            Result(
                new LauncherSearchTarget.Screen(new ScreenId("screen")),
                "Alpha"),
            Result(
                new LauncherSearchTarget.Connection(new ConnectionId("connection-z")),
                "Alpha"),
            Result(
                new LauncherSearchTarget.Connection(new ConnectionId("connection-a")),
                "Alpha"),
            Result(
                new LauncherSearchTarget.Connection(new ConnectionId("connection-b")),
                "Beta"),
        };

        var forward = LauncherSearchProjection.Search(string.Empty, candidates);
        var reverse = LauncherSearchProjection.Search(string.Empty, candidates.Reverse());

        Assert.Equal(forward.Select(TargetId), reverse.Select(TargetId), StringComparer.Ordinal);
        Assert.Equal(
            ["connection-a", "connection-z", "connection-b", "screen", "disabled-command"],
            forward.Select(TargetId), StringComparer.Ordinal);
    }

    [Fact]
    public void Empty_query_preserves_recent_sessions_newest_first()
    {
        var newest = Result(
            new LauncherSearchTarget.RecentSession(new SessionId("newest")),
            "Zulu newest");
        var older = Result(
            new LauncherSearchTarget.RecentSession(new SessionId("older")),
            "Alpha older");

        var results = LauncherSearchProjection.Search(string.Empty, [newest, older]);

        Assert.Equal(["newest", "older"], results.Select(TargetId), StringComparer.Ordinal);
    }

    [Fact]
    public void Unavailable_results_remain_visible_but_navigation_skips_them()
    {
        var unavailableConnection = Result(
            new LauncherSearchTarget.Connection(new ConnectionId("unavailable")),
            "Unavailable connection",
            isAvailable: false,
            unavailableReason: "Unavailable on this platform");
        var availableScreen = Result(
            new LauncherSearchTarget.Screen(new ScreenId("screen")),
            "Available screen");
        var staleRecent = Result(
            new LauncherSearchTarget.RecentSession(new SessionId("stale")),
            "Stale session",
            isAvailable: false,
            unavailableReason: "The saved definition no longer exists.");
        var items = new[] { unavailableConnection, availableScreen, staleRecent };

        Assert.Equal("Unavailable on this platform", unavailableConnection.DisplayDetail);
        Assert.Equal("The saved definition no longer exists.", staleRecent.DisplayDetail);
        Assert.Equal(1, LauncherSearchProjection.FindNextAvailableIndex(items, -1, 1));
        Assert.Equal(1, LauncherSearchProjection.FindNextAvailableIndex(items, 1, 1));
        Assert.Null(LauncherSearchProjection.ConfirmSelection(unavailableConnection));
        Assert.Equal(
            new LauncherSearchTarget.Screen(new ScreenId("screen")),
            LauncherSearchProjection.ConfirmSelection(availableScreen));
    }

    [Fact]
    public void Unknown_query_has_no_results_or_available_selection()
    {
        var results = LauncherSearchProjection.Search(
            "not-present",
            [Result(
                new LauncherSearchTarget.Command(new CommandId("tab.new")),
                "New tab",
                searchTerms: ["command", "tabs"])]);

        Assert.Empty(results);
        Assert.Equal(-1, LauncherSearchProjection.FindNextAvailableIndex(results, -1, 1));
        Assert.Null(LauncherSearchProjection.ResolveAvailableSelection(results, null));
    }

    [Fact]
    public void Keyboard_selection_wraps_and_preserved_selection_rehomes_when_unavailable()
    {
        var disabled = Result(
            new LauncherSearchTarget.Command(new CommandId("disabled")),
            "Disabled",
            isAvailable: false,
            unavailableReason: "Unavailable");
        var screen = Result(
            new LauncherSearchTarget.Screen(new ScreenId("screen")),
            "Screen");
        var stale = Result(
            new LauncherSearchTarget.RecentSession(new SessionId("stale")),
            "Stale",
            isAvailable: false,
            unavailableReason: "Missing");
        var workspaceTarget = new LauncherSearchTarget.Workspace(new WorkspaceId("workspace"));
        var workspace = Result(workspaceTarget, "Workspace");
        var items = new[] { disabled, screen, stale, workspace };

        Assert.Equal(1, LauncherSearchProjection.FindNextAvailableIndex(items, -1, 1));
        Assert.Equal(3, LauncherSearchProjection.FindNextAvailableIndex(items, 1, 1));
        Assert.Equal(1, LauncherSearchProjection.FindNextAvailableIndex(items, 3, 1));
        Assert.Equal(3, LauncherSearchProjection.FindNextAvailableIndex(items, 1, -1));
        Assert.Same(
            workspace,
            LauncherSearchProjection.ResolveAvailableSelection(items, workspaceTarget));

        var workspaceUnavailable = workspace with
        {
            IsAvailable = false,
            UnavailableReason = "Missing",
        };
        var refreshed = new[] { disabled, screen, stale, workspaceUnavailable };

        Assert.Same(
            screen,
            LauncherSearchProjection.ResolveAvailableSelection(refreshed, workspaceTarget));
        Assert.Null(LauncherSearchProjection.ConfirmSelection(workspaceUnavailable));
    }

    [Fact]
    public void Parameterized_command_selection_is_preserved_by_argument_values()
    {
        var right = Result(
            new LauncherSearchTarget.Command(
                BuiltInCommands.FocusPanel,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["direction"] = "right" }),
            "Focus panel right");
        var left = Result(
            new LauncherSearchTarget.Command(
                BuiltInCommands.FocusPanel,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["direction"] = "left" }),
            "Focus panel left");
        var refreshedRight = Result(
            new LauncherSearchTarget.Command(
                BuiltInCommands.FocusPanel,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["direction"] = "right" }),
            "Focus panel right");

        var resolved = LauncherSearchProjection.ResolveAvailableSelection(
            [left, refreshedRight],
            right.Target);

        Assert.Same(refreshedRight, resolved);
        var command = Assert.IsType<LauncherSearchTarget.Command>(
            LauncherSearchProjection.ConfirmSelection(resolved));
        Assert.Equal("right", command.Arguments["direction"]);
    }

    private static LauncherSearchResultViewModel Result(
        LauncherSearchTarget target,
        string title,
        bool isAvailable = true,
        string? unavailableReason = null,
        IReadOnlyList<string>? searchTerms = null) => new(
        target,
        Symbol.Code,
        "TEST",
        title,
        "Detail",
        string.Empty,
        isAvailable,
        unavailableReason,
        searchTerms ?? []);

    private static string TargetId(LauncherSearchResultViewModel result) => result.Target switch
    {
        LauncherSearchTarget.Command command => command.Id.Value,
        LauncherSearchTarget.Connection connection => connection.Id.Value,
        LauncherSearchTarget.Screen screen => screen.Id.Value,
        LauncherSearchTarget.Workspace workspace => workspace.Id.Value,
        LauncherSearchTarget.RecentSession recent => recent.Id.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };
}
