using Asura.Application;

namespace Asura.Terminal.Tests;

public sealed class RemoteTerminalIdleClassifierTests
{
    [Theory]
    [InlineData("root@ubuntu:~# ", 15)]
    [InlineData("deploy@host:/srv$ ", 18)]
    [InlineData("[user@host project]$ ", 21)]
    [InlineData("user@host% ", 11)]
    [InlineData("PS C:\\Users\\deploy> ", 19)]
    [InlineData("/ # ", 3)]
    [InlineData("/app # ", 6)]
    [InlineData("~/workspace $ ", 13)]
    [InlineData("asura-5cce9f181cf6e094c296cea0:~# ", 39)]
    [InlineData("container-name:/workspace# ", 26)]
    public void Common_remote_shell_prompts_are_idle(string line, int cursorColumn)
    {
        var state = State(cursorColumn);

        Assert.True(IsAtShellPrompt(line, state));
    }

    [Fact]
    public void Soft_wrapped_prompt_is_classified_from_its_complete_logical_line()
    {
        var snapshot = Snapshot(
            [
                ("root@Ubuntu-2404-noble-amd64-", true),
                ("base ~ # ", false),
            ],
            State(cursorRow: 1, cursorColumn: 9));

        Assert.True(RemoteTerminalIdleClassifier.IsAtShellPrompt(snapshot));
    }

    [Fact]
    public void Soft_wrapped_non_prompt_remains_confirmation_worthy()
    {
        var snapshot = Snapshot(
            [("service:", true), ("healthy# ", false)],
            State(cursorRow: 1, cursorColumn: 9));

        Assert.False(RemoteTerminalIdleClassifier.IsAtShellPrompt(snapshot));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("root@ubuntu:~# sleep 100\n", 0)]
    [InlineData("mysql> ", 7)]
    [InlineData("service:healthy# ", 16)]
    [InlineData("building package 42%", 20)]
    public void Running_or_non_shell_interactions_remain_confirmation_worthy(
        string screen,
        int cursorColumn)
    {
        var state = State(cursorColumn);

        Assert.False(IsAtShellPrompt(screen, state));
    }

    /// <summary>
    /// Bracketed paste is what a shell turns on at its prompt so that a paste
    /// arrives as text rather than as commands. Every modern bash and zsh does
    /// it, so treating it as a sign of activity meant every remote shell looked
    /// busy and every close asked to confirm.
    /// </summary>
    [Fact]
    public void A_prompt_that_protects_pastes_is_still_a_prompt()
    {
        var state = State(cursorColumn: 15) with
        {
            IsBracketedPasteEnabled = true,
        };

        Assert.True(IsAtShellPrompt("root@ubuntu:~# ", state));
    }

    /// <summary>
    /// What actually means something else has the terminal: a program drawing
    /// on the alternate screen, or one reading the mouse. Both still say so
    /// even where a prompt would otherwise be recognised.
    /// </summary>
    [Fact]
    public void Mouse_tracking_is_never_treated_as_an_idle_shell()
    {
        var state = State(cursorColumn: 15) with
        {
            IsMouseTrackingEnabled = true,
        };

        Assert.False(IsAtShellPrompt("root@ubuntu:~# ", state));
    }

    [Fact]
    public void Alternate_screen_is_never_treated_as_an_idle_shell()
    {
        var state = State(cursorColumn: 15) with
        {
            IsAlternateScreen = true,
        };

        Assert.False(IsAtShellPrompt("root@ubuntu:~# ", state));
    }

    [Fact]
    public void Explicit_prompt_shape_fallback_applies_to_local_container_shells()
    {
        var launch = new TerminalLaunchRequest(
            "/tmp",
            shellActivityFallback:
                TerminalShellActivityFallback.PromptShape);

        Assert.True(RemoteTerminalIdleClassifier.AppliesTo(launch));
    }

    [Fact]
    public void Prompt_fallback_does_not_apply_to_unrelated_local_commands()
    {
        var launch = new TerminalLaunchRequest(
            "/tmp",
            initialCommand: "dotnet test");

        Assert.False(RemoteTerminalIdleClassifier.AppliesTo(launch));
    }

    private static bool IsAtShellPrompt(string screen, ScreenState state) =>
        RemoteTerminalIdleClassifier.IsAtShellPrompt(Snapshot(
            [.. screen.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => (line, false))],
            state));

    private static TerminalScreenSnapshot Snapshot(
        IReadOnlyList<(string Text, bool IsWrapped)> rows,
        ScreenState state)
    {
        var columns = Math.Max(
            state.CursorColumn + 1,
            rows.Max(row => row.Text.Length));
        TerminalScreenRow[] structuredRows = [.. rows
            .Select((row, index) => new TerminalScreenRow(
                index,
                [.. row.Text.Select(character => new TerminalScreenCell(
                    character.ToString(),
                    1,
                    TerminalCellColor.Default,
                    TerminalCellColor.Default))],
                row.IsWrapped))];
        return new TerminalScreenSnapshot(
            PlainText: string.Concat(rows.Select(row => row.Text)),
            CursorRow: state.CursorRow,
            CursorColumn: state.CursorColumn,
            Rows: structuredRows.Length,
            Columns: columns,
            IsAlternateScreen: state.IsAlternateScreen,
            WorkingDirectory: null,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            StructuredRows: structuredRows,
            IsBracketedPasteEnabled: state.IsBracketedPasteEnabled,
            IsMouseTrackingEnabled: state.IsMouseTrackingEnabled);
    }

    private static ScreenState State(int cursorColumn, int cursorRow = 0) =>
        new(
            CursorRow: cursorRow,
            CursorColumn: cursorColumn,
            IsAlternateScreen: false,
            IsBracketedPasteEnabled: false,
            IsMouseTrackingEnabled: false);

    private readonly record struct ScreenState(
        int CursorRow,
        int CursorColumn,
        bool IsAlternateScreen,
        bool IsBracketedPasteEnabled,
        bool IsMouseTrackingEnabled);
}
