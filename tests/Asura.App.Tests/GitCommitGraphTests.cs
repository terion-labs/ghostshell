using Asura.App.ViewModels;
using Asura.Git;

namespace Asura.App.Tests;

public sealed class GitCommitGraphTests
{
    [Fact]
    public void LinearHistoryStaysInOneLane()
    {
        var rows = GitCommitGraph.Compute([
            Commit("a", "b"),
            Commit("b", "c"),
            Commit("c"),
        ]);

        Assert.All(rows, row => Assert.Equal(0, row.DotLane));
        Assert.All(rows, row => Assert.Equal(1, row.LaneCount));
        Assert.False(rows[0].IsMerge);
        Assert.Empty(rows[2].BottomEdges);
    }

    [Fact]
    public void MergeOpensASecondLaneThatConvergesAtTheBranchPoint()
    {
        // merge M has parents b (mainline) and f (feature); both descend from c.
        var rows = GitCommitGraph.Compute([
            Commit("m", "b", "f"),
            Commit("b", "c"),
            Commit("f", "c"),
            Commit("c"),
        ]);

        Assert.True(rows[0].IsMerge);
        Assert.Equal(0, rows[0].DotLane);
        // The merge fans out to its second parent's lane.
        Assert.Contains(rows[0].BottomEdges, edge => edge.FromLane == 0 && edge.ToLane == 1);
        // The feature commit sits on lane 1 while the mainline passes on lane 0.
        Assert.Equal(1, rows[2].DotLane);
        Assert.Equal(2, rows[2].LaneCount);
        // At the branch point both lanes converge onto the dot.
        Assert.Equal(0, rows[3].DotLane);
        Assert.Contains(rows[3].TopEdges, edge => edge.FromLane == 1 && edge.ToLane == 0);
        Assert.Empty(rows[3].BottomEdges);
    }

    [Fact]
    public void RowsStayIdenticalWhenLaterPagesAppend()
    {
        var commits = new[]
        {
            Commit("m", "b", "f"),
            Commit("b", "c"),
            Commit("f", "c"),
            Commit("c", "d"),
            Commit("d"),
        };

        var partial = GitCommitGraph.Compute([.. commits.Take(3)]);
        var full = GitCommitGraph.Compute(commits);

        for (var index = 0; index < partial.Count; index++)
        {
            Assert.Equal(partial[index], full[index], GraphRowEquality.Instance);
        }
    }

    private static GitCommitItem Commit(string sha, params string[] parents) => new(
        sha,
        sha,
        parents,
        "terion",
        "t@x",
        DateTimeOffset.FromUnixTimeSeconds(1_755_500_570),
        $"commit {sha}",
        []);

    private sealed class GraphRowEquality : IEqualityComparer<GitGraphRow>
    {
        public static GraphRowEquality Instance { get; } = new();

        public bool Equals(GitGraphRow? x, GitGraphRow? y) =>
            x is not null
            && y is not null
            && x.DotLane == y.DotLane
            && x.DotColorIndex == y.DotColorIndex
            && x.IsMerge == y.IsMerge
            && x.LaneCount == y.LaneCount
            && x.TopEdges.SequenceEqual(y.TopEdges)
            && x.BottomEdges.SequenceEqual(y.BottomEdges);

        public int GetHashCode(GitGraphRow obj) => obj.DotLane;
    }
}
