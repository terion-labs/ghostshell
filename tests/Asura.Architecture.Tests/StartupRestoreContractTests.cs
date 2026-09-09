using System.Text.RegularExpressions;

namespace Asura.Architecture.Tests;

/// <summary>
/// The window comes back to what was there, and how the last process ended is
/// not part of the question.
///
/// It used to be: an unfinished run marker put a modal "Asura did not close
/// cleanly" in front of the window and made the person who did not crash
/// anything choose between Restore, Safe mode, and Discard before they could
/// work. Both branches then loaded the same snapshot from the same table — the
/// runtime state is written as the workspace changes, so it was already stored
/// before the process died and the choice decided nothing.
///
/// This reads startup's own source, because a branch on run cleanliness is one
/// line and lives where no behavioural test looks.
/// </summary>
public sealed class StartupRestoreContractTests
{
    private static readonly string StartupSource = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "Asura.App",
        "App.axaml.cs"));

    /// <summary>
    /// Nothing stands between the window opening and the session it had. A
    /// dialog here is modal on first paint, and every one of them is a question
    /// the shell could have answered itself.
    /// </summary>
    [Fact]
    public void Startup_opens_the_stored_session_without_asking()
    {
        var body = MethodBody(StartupSource, "OnStartupWindowOpened");

        Assert.NotNull(body);
        Assert.Contains("RestoreSessionOnStartupAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the previous run wrote its clean-shutdown marker is reportable,
    /// and nothing else. The moment startup reads it, "was it killed" is back
    /// to deciding what you get to come back to.
    /// </summary>
    [Fact]
    public void Startup_does_not_branch_on_how_the_previous_run_ended()
    {
        string[] cleanliness =
        [
            "PreviousRunWasInterrupted",
            "RecoveryRequired",
            "WasClean",
            "RecoveryState",
            "RecoveryChoice",
        ];

        var offenders = cleanliness
            .Where(term => StartupSource.Contains(term, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Startup reads how the last process ended: "
            + $"{string.Join(", ", offenders)}. The runtime snapshot is written as the "
            + "workspace changes, so it is already stored either way — reading this can only "
            + "take a session away from someone whose machine crashed.");
    }

    private static string? MethodBody(string source, string methodName)
    {
        var match = Regex.Match(
            source,
            $@"(private|public|internal|protected)[^\n;]*?\b{Regex.Escape(methodName)}\s*\([^)]*\)",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            return null;
        }

        var open = source.IndexOf('{', match.Index + match.Length);
        if (open < 0)
        {
            return null;
        }

        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(index + 1)];
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the Asura repository root.");
    }
}
