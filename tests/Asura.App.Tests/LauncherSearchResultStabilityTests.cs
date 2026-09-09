using Asura.App.ViewModels;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.Tests;

/// <summary>
/// The command palette rebuilt its whole list on every refresh, and most refreshes
/// come from things unrelated to the palette. Rebuilding tears down every row, so
/// whatever the pointer was over changed while the pointer had not moved — the
/// highlight appeared to jump on its own.
/// </summary>
public sealed class LauncherSearchResultStabilityTests
{
    private static LauncherSearchResultViewModel Result(
        string title = "New terminal",
        bool available = true) =>
        new(
            new LauncherSearchTarget.CreatePanel(PanelKind.Terminal),
            Symbol.WindowConsole,
            "CREATE · TERMINAL",
            title,
            "Start a local PTY in a new tab.",
            "OPEN",
            available,
            UnavailableReason: null,
            ["create", "new", "terminal"]);

    /// <summary>
    /// Record equality cannot answer this: search terms are an array, so two
    /// results built from the same source are never equal.
    /// </summary>
    [Fact]
    public void Two_results_from_the_same_source_present_the_same()
    {
        var first = Result();
        var second = Result();

        Assert.NotEqual(first, second);
        Assert.True(first.PresentsSameAs(second));
    }

    [Fact]
    public void A_changed_title_presents_differently()
    {
        Assert.False(Result().PresentsSameAs(Result(title: "New browser")));
    }

    /// <summary>
    /// Availability changes what the row offers, so it has to count as a change
    /// even though the row's text is identical.
    /// </summary>
    [Fact]
    public void A_changed_availability_presents_differently()
    {
        Assert.False(Result().PresentsSameAs(Result(available: false)));
    }

    [Fact]
    public void A_different_target_presents_differently()
    {
        var browser = new LauncherSearchResultViewModel(
            new LauncherSearchTarget.CreatePanel(PanelKind.Browser),
            Symbol.WindowConsole,
            "CREATE · TERMINAL",
            "New terminal",
            "Start a local PTY in a new tab.",
            "OPEN",
            IsAvailable: true,
            UnavailableReason: null,
            ["create", "new", "terminal"]);

        Assert.False(Result().PresentsSameAs(browser));
    }
}

/// <summary>
/// The palette kept rebuilding every row because its command targets never
/// compared equal: a command's arguments live in a dictionary, and records compare
/// those by reference.
/// </summary>
public sealed class LauncherSearchTargetIdentityTests
{
    private static LauncherSearchTarget.Command Command() => new(
        new CommandId("panel.focus"),
        [new KeyValuePair<string, string>("direction", "down")]);

    [Fact]
    public void Two_commands_from_the_same_source_identify_the_same()
    {
        var first = Command();
        var second = Command();

        // The reason the helper exists, stated as an assertion.
        Assert.NotEqual(first, second);
        Assert.True(first.IdentifiesSameAs(second));
    }

    [Fact]
    public void A_different_argument_is_a_different_target()
    {
        var down = Command();
        var up = new LauncherSearchTarget.Command(
            new CommandId("panel.focus"),
            [new KeyValuePair<string, string>("direction", "up")]);

        Assert.False(down.IdentifiesSameAs(up));
    }

    [Fact]
    public void A_different_command_is_a_different_target()
    {
        Assert.False(
            Command().IdentifiesSameAs(new LauncherSearchTarget.Command(new CommandId("panel.close"))));
    }

    [Fact]
    public void Targets_of_different_kinds_never_identify_the_same()
    {
        Assert.False(
            new LauncherSearchTarget.CreatePanel(PanelKind.Terminal)
                .IdentifiesSameAs(new LauncherSearchTarget.CreatePanel(PanelKind.Browser)));
        Assert.True(
            new LauncherSearchTarget.CreatePanel(PanelKind.Terminal)
                .IdentifiesSameAs(new LauncherSearchTarget.CreatePanel(PanelKind.Terminal)));
    }
}
