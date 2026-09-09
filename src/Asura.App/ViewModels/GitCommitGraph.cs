using Asura.Git;

namespace Asura.App.ViewModels;

/// <summary>A line segment in one graph row, in lane coordinates.</summary>
public sealed record GitGraphEdge(int FromLane, int ToLane, int ColorIndex);

/// <summary>
/// One history row's slice of the commit graph: edges entering from the row
/// above, edges leaving toward the row below, and the commit's own dot.
/// </summary>
public sealed record GitGraphRow(
    int DotLane,
    int DotColorIndex,
    bool IsMerge,
    IReadOnlyList<GitGraphEdge> TopEdges,
    IReadOnlyList<GitGraphEdge> BottomEdges,
    int LaneCount);

/// <summary>
/// Assigns lanes to a topologically ordered commit page. Each lane tracks the
/// commit it expects next; a commit lands on the first lane expecting it,
/// merges pull the other expecting lanes into the dot, and parents continue
/// the lane or open new ones. Colors follow lanes, not branches — a page has
/// no branch names, only ancestry.
/// </summary>
public static class GitCommitGraph
{
    public const int ColorCount = 5;

    public static IReadOnlyList<GitGraphRow> Compute(IReadOnlyList<GitCommitItem> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var rows = new List<GitGraphRow>(commits.Count);
        var lanes = new List<(string ExpectedSha, int Color)>();
        var nextColor = 0;

        foreach (var commit in commits)
        {
            var dotLaneBefore = lanes.FindIndex(lane =>
                string.Equals(lane.ExpectedSha, commit.Sha, StringComparison.Ordinal));
            if (dotLaneBefore < 0)
            {
                dotLaneBefore = lanes.Count;
                lanes.Add((commit.Sha, nextColor));
                nextColor = (nextColor + 1) % ColorCount;
            }

            var dotColor = lanes[dotLaneBefore].Color;

            // Lanes converging on this commit end here, and every lane to
            // their right slides left; the upper half of the row draws that
            // slide so rows stay continuous at their shared border.
            var newIndex = new int[lanes.Count];
            var removed = 0;
            for (var lane = 0; lane < lanes.Count; lane++)
            {
                var converges = lane != dotLaneBefore
                    && string.Equals(lanes[lane].ExpectedSha, commit.Sha, StringComparison.Ordinal);
                newIndex[lane] = converges ? -1 : lane - removed;
                if (converges)
                {
                    removed++;
                }
            }

            var dotLane = newIndex[dotLaneBefore];
            var topEdges = new List<GitGraphEdge>();
            for (var lane = 0; lane < lanes.Count; lane++)
            {
                topEdges.Add(new GitGraphEdge(
                    lane,
                    newIndex[lane] < 0 ? dotLane : newIndex[lane],
                    lanes[lane].Color));
            }

            for (var lane = lanes.Count - 1; lane >= 0; lane--)
            {
                if (newIndex[lane] < 0)
                {
                    lanes.RemoveAt(lane);
                }
            }

            var laneCountBefore = lanes.Count;

            // The first parent inherits the commit's lane and color; further
            // parents join an existing lane or open a new one to the right.
            var bottomEdges = new List<GitGraphEdge>();
            if (commit.ParentShas.Count == 0)
            {
                lanes.RemoveAt(dotLane);
            }
            else
            {
                lanes[dotLane] = (commit.ParentShas[0], dotColor);
                bottomEdges.Add(new GitGraphEdge(dotLane, dotLane, dotColor));
                foreach (var parent in commit.ParentShas.Skip(1))
                {
                    var parentLane = lanes.FindIndex(lane =>
                        string.Equals(lane.ExpectedSha, parent, StringComparison.Ordinal));
                    if (parentLane < 0)
                    {
                        parentLane = lanes.Count;
                        lanes.Add((parent, nextColor));
                        nextColor = (nextColor + 1) % ColorCount;
                    }

                    bottomEdges.Add(new GitGraphEdge(dotLane, parentLane, lanes[parentLane].Color));
                }
            }

            // Unrelated lanes pass straight through the lower half as well.
            for (var lane = 0; lane < laneCountBefore; lane++)
            {
                if (lane != dotLane)
                {
                    bottomEdges.Add(new GitGraphEdge(lane, lane, lanes[lane].Color));
                }
            }

            rows.Add(new GitGraphRow(
                dotLane,
                dotColor,
                commit.ParentShas.Count > 1,
                topEdges,
                bottomEdges,
                Math.Max(Math.Max(topEdges.Count, lanes.Count), dotLane + 1)));
        }

        return rows;
    }
}
