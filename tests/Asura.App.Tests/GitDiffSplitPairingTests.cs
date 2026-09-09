using Asura.App.ViewModels;
using Asura.Git;

namespace Asura.App.Tests;

public sealed class GitDiffSplitPairingTests
{
    [Fact]
    public void ARemovalRunPairsWithTheFollowingAdditionRunIndexWise()
    {
        var hunk = new GitDiffHunk(
            "@@ -1,4 +1,5 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, "keep", 1, 1),
                new GitDiffLine(GitDiffLineKind.Removed, "old-a", 2, null),
                new GitDiffLine(GitDiffLineKind.Removed, "old-b", 3, null),
                new GitDiffLine(GitDiffLineKind.Added, "new-a", null, 2),
                new GitDiffLine(GitDiffLineKind.Added, "new-b", null, 3),
                new GitDiffLine(GitDiffLineKind.Added, "new-c", null, 4),
                new GitDiffLine(GitDiffLineKind.Context, "tail", 4, 5),
            ]);

        var rows = GitDiffSplitRowViewModel.Build([hunk]);

        // Header, context, three paired change rows, context.
        Assert.Equal(6, rows.Count);
        Assert.True(rows[0].IsHunkHeader);
        Assert.Equal("@@ -1,4 +1,5 @@", rows[0].HeaderText);

        Assert.Equal("keep", rows[1].LeftText);
        Assert.Equal("keep", rows[1].RightText);
        Assert.False(rows[1].LeftIsRemoved);
        Assert.False(rows[1].RightIsAdded);

        Assert.Equal(("old-a", "new-a"), (rows[2].LeftText, rows[2].RightText));
        Assert.Equal(("old-b", "new-b"), (rows[3].LeftText, rows[3].RightText));
        Assert.True(rows[2].LeftIsRemoved);
        Assert.True(rows[2].RightIsAdded);
        Assert.Equal("−", rows[2].LeftMarkerText);
        Assert.Equal("+", rows[2].RightMarkerText);

        // The addition run is longer, so its tail faces a blank left side.
        Assert.Equal(("", "new-c"), (rows[4].LeftText, rows[4].RightText));
        Assert.False(rows[4].LeftIsRemoved);
        Assert.True(rows[4].RightIsAdded);
        Assert.Equal("", rows[4].LeftNumberText);
        Assert.Equal("4", rows[4].RightNumberText);

        Assert.Equal(("tail", "tail"), (rows[5].LeftText, rows[5].RightText));
        Assert.Equal(("4", "5"), (rows[5].LeftNumberText, rows[5].RightNumberText));
    }

    [Fact]
    public void AnAdditionOnlyBlockFillsTheRightSideOnly()
    {
        var hunk = new GitDiffHunk(
            "@@ -1,1 +1,2 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, "keep", 1, 1),
                new GitDiffLine(GitDiffLineKind.Added, "new", null, 2),
            ]);

        var rows = GitDiffSplitRowViewModel.Build([hunk]);

        Assert.Equal(3, rows.Count);
        Assert.Equal("", rows[2].LeftText);
        Assert.Equal("new", rows[2].RightText);
        Assert.True(rows[2].RightIsAdded);
    }
}
