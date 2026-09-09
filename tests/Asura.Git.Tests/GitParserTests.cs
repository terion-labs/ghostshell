namespace Asura.Git.Tests;

public sealed class GitStatusParserTests
{
    [Fact]
    public void ParsesBranchHeadersAndChanges()
    {
        var output =
            "# branch.oid 4ff4a98aa11111111111111111111111111111ab\0"
            + "# branch.head dev\0"
            + "# branch.upstream origin/dev\0"
            + "# branch.ab +2 -1\0"
            + "1 .M N... 100644 100644 100644 aaa bbb src/App/Main.cs\0"
            + "1 M. N... 100644 100644 100644 aaa bbb docs/readme with spaces.md\0"
            + "? new file.txt\0";

        var result = GitStatusParser.Parse(output);

        Assert.Equal("dev", result.Head.BranchName);
        Assert.Equal("origin/dev", result.Head.Upstream);
        Assert.Equal(2, result.Head.Ahead);
        Assert.Equal(1, result.Head.Behind);
        Assert.False(result.Head.IsDetached);
        Assert.False(result.Head.IsUnborn);

        Assert.Equal(2, result.UnstagedChanges.Count);
        Assert.Equal("src/App/Main.cs", result.UnstagedChanges[0].Path);
        Assert.Equal(GitChangeKind.Modified, result.UnstagedChanges[0].Kind);
        Assert.Equal("new file.txt", result.UnstagedChanges[1].Path);
        Assert.Equal(GitChangeKind.Untracked, result.UnstagedChanges[1].Kind);

        var staged = Assert.Single(result.StagedChanges);
        Assert.Equal("docs/readme with spaces.md", staged.Path);
        Assert.Equal(GitChangeArea.Staged, staged.Area);
    }

    [Fact]
    public void ParsesStagedRenameWithSeparateSourceRecord()
    {
        var output =
            "# branch.oid abc\0# branch.head main\0"
            + "2 R. N... 100644 100644 100644 aaa bbb R100 new/name.cs\0old/name.cs\0";

        var result = GitStatusParser.Parse(output);

        var staged = Assert.Single(result.StagedChanges);
        Assert.Equal(GitChangeKind.Renamed, staged.Kind);
        Assert.Equal("new/name.cs", staged.Path);
        Assert.Equal("old/name.cs", staged.OriginalPath);
        Assert.Empty(result.UnstagedChanges);
    }

    [Fact]
    public void ParsesConflictAndSubmoduleStates()
    {
        var output =
            "# branch.oid abc\0# branch.head main\0"
            + "u UU N... 100644 100644 100644 100644 a b c both/changed.cs\0"
            + "1 .M S.M. 160000 160000 160000 aaa bbb vendor/lib\0";

        var result = GitStatusParser.Parse(output);

        Assert.Equal(2, result.UnstagedChanges.Count);
        Assert.Equal(GitChangeKind.Conflicted, result.UnstagedChanges[0].Kind);
        Assert.True(result.UnstagedChanges[1].IsSubmodule);
    }

    [Fact]
    public void RecognizesUnbornAndDetachedHeads()
    {
        var unborn = GitStatusParser.Parse("# branch.oid (initial)\0# branch.head main\0");
        Assert.True(unborn.Head.IsUnborn);
        Assert.Equal("main", unborn.Head.BranchName);

        var detached = GitStatusParser.Parse(
            "# branch.oid 4ff4a98aa11111111111111111111111111111ab\0# branch.head (detached)\0");
        Assert.True(detached.Head.IsDetached);
        Assert.Equal("4ff4a98a", detached.Head.BranchName);
    }
}

public sealed class GitRefsParserTests
{
    [Fact]
    public void ParsesBranchRemoteAndTagRefs()
    {
        var sha = "4ff4a98aa11111111111111111111111111111ab";
        var peeled = "9994a98aa11111111111111111111111111111ab";
        var output =
            $"refs/heads/dev\0dev\0{sha}\0\0origin/dev\0ahead 1\0*\n"
            + $"refs/heads/main\0main\0{sha}\0\0\0\0 \n"
            + $"refs/remotes/origin/dev\0origin/dev\0{sha}\0\0\0\0 \n"
            + $"refs/tags/v1\0v1\0{sha}\0{peeled}\0\0\0 \n";

        var refs = GitRefsParser.ParseRefs(output);

        Assert.Equal(4, refs.Count);
        Assert.Equal(GitRefKind.LocalBranch, refs[0].Kind);
        Assert.True(refs[0].IsCurrent);
        Assert.Equal(1, refs[0].Ahead);
        Assert.Equal(0, refs[0].Behind);
        Assert.Null(refs[1].Ahead);
        Assert.Equal(GitRefKind.RemoteBranch, refs[2].Kind);
        Assert.Equal(GitRefKind.Tag, refs[3].Kind);
        Assert.Equal(peeled, refs[3].TargetSha);
    }

    [Fact]
    public void ParsesRemotesStashesWorktreesAndSubmodules()
    {
        var remotes = GitRefsParser.ParseRemotes(
            "origin\tgit@github.com:t/x.git (fetch)\norigin\tgit@github.com:t/x.git (push)\n");
        Assert.Equal("origin", Assert.Single(remotes).Name);
        Assert.Equal("git@github.com:t/x.git", remotes[0].FetchUrl);

        var guardedRemotes = GitRefsParser.ParseRemotes(
            "--all\tfile:///attacker (fetch)\nteam/ö\tssh://example.test/repo (fetch)\n");
        Assert.Equal("team/ö", Assert.Single(guardedRemotes).Name);

        var stashes = GitRefsParser.ParseStashes(
            "stash@{0}\0WIP on dev: quick fix\0stash@{1}\0On main: idea\0");
        Assert.Equal(2, stashes.Count);
        Assert.Equal("stash@{0}", stashes[0].Reference);
        Assert.Equal("On main: idea", stashes[1].Subject);

        var worktrees = GitRefsParser.ParseWorktrees(
            "worktree /repo\0HEAD abc\0branch refs/heads/dev\0\0"
            + "worktree /repo-wt\0HEAD def\0detached\0\0");
        Assert.Equal(2, worktrees.Count);
        Assert.True(worktrees[0].IsMain);
        Assert.Equal("dev", worktrees[0].Branch);
        Assert.Null(worktrees[1].Branch);

        var submodules = GitRefsParser.ParseSubmodules(
            " abc123 vendor/lib (v1.0)\n+def456 vendor/other (heads/main)\n");
        Assert.Equal(2, submodules.Count);
        Assert.Equal("vendor/lib", submodules[0].Path);
        Assert.Equal("clean", submodules[0].State);
        Assert.Equal("modified", submodules[1].State);
    }
}

public sealed class GitLogParserTests
{
    [Fact]
    public void ParsesCommitPageRecords()
    {
        var output =
            "aaaa\0aa\0bbbb cccc\0terion\0t@x\01755500570\0HEAD -> dev, origin/dev\0browser new tab\n"
            + "bbbb\0bb\0\0terion\0t@x\01755400000\0\0first\n";

        var commits = GitLogParser.ParseCommits(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal(["bbbb", "cccc"], commits[0].ParentShas);
        Assert.Equal(["dev", "origin/dev"], commits[0].RefNames);
        Assert.Equal("browser new tab", commits[0].Subject);
        Assert.Empty(commits[1].ParentShas);
        Assert.Empty(commits[1].RefNames);
        Assert.Equal(2025, commits[0].AuthoredAt.Year);
    }

    [Fact]
    public void ParsesCommitDetailWithMultilineBody()
    {
        var output =
            "aaaa\0aa\0bbbb\0terion\0t@x\01755500570\0\0subject line\0terion\01755500600\0"
            + "body first line\n\nbody second paragraph\n";

        var detail = GitLogParser.ParseCommitDetail(output);

        Assert.Equal("subject line", detail.Commit.Subject);
        Assert.Equal("body first line\n\nbody second paragraph", detail.Body);
        Assert.Equal("terion", detail.CommitterName);
    }

    [Fact]
    public void ParsesNameStatusWithRenames()
    {
        var changes = GitLogParser.ParseNameStatus(
            "M\0src/a.cs\0R100\0old/path.cs\0new/path.cs\0A\0added.txt\0");

        Assert.Equal(3, changes.Count);
        Assert.Equal(GitChangeKind.Modified, changes[0].Kind);
        Assert.Equal("new/path.cs", changes[1].Path);
        Assert.Equal("old/path.cs", changes[1].OriginalPath);
        Assert.Equal(GitChangeKind.Added, changes[2].Kind);
    }
}

public sealed class GitDiffParserTests
{
    [Fact]
    public void ParsesHunksWithPairedLineNumbers()
    {
        var output =
            "diff --git a/f.txt b/f.txt\n"
            + "index aaa..bbb 100644\n"
            + "--- a/f.txt\n"
            + "+++ b/f.txt\n"
            + "@@ -1,3 +1,4 @@ context heading\n"
            + " unchanged\n"
            + "-removed line\n"
            + "+added one\n"
            + "+added two\n"
            + " tail\n";

        var document = GitDiffParser.Parse("f.txt", null, output, isTruncated: false);

        var hunk = Assert.Single(document.Hunks);
        Assert.Equal("@@ -1,3 +1,4 @@ context heading", hunk.Header);
        Assert.Equal(5, hunk.Lines.Count);
        Assert.Equal((1, 1), (hunk.Lines[0].OldLineNumber, hunk.Lines[0].NewLineNumber));
        Assert.Equal(GitDiffLineKind.Removed, hunk.Lines[1].Kind);
        Assert.Equal(2, hunk.Lines[1].OldLineNumber);
        Assert.Null(hunk.Lines[1].NewLineNumber);
        Assert.Equal(GitDiffLineKind.Added, hunk.Lines[2].Kind);
        Assert.Equal(2, hunk.Lines[2].NewLineNumber);
        Assert.Equal((3, 4), (hunk.Lines[4].OldLineNumber, hunk.Lines[4].NewLineNumber));
        Assert.False(document.IsBinary);
    }

    [Fact]
    public void RecognizesBinaryDiffs()
    {
        var document = GitDiffParser.Parse(
            "img.png",
            null,
            "diff --git a/img.png b/img.png\nBinary files a/img.png and b/img.png differ\n",
            isTruncated: false);

        Assert.True(document.IsBinary);
        Assert.Empty(document.Hunks);
    }
}
