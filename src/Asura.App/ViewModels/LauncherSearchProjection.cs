namespace Asura.App.ViewModels;

internal static class LauncherSearchProjection
{
    private const int NoMatch = int.MaxValue;

    public static IReadOnlyList<LauncherSearchResultViewModel> Search(
        string? query,
        IEnumerable<LauncherSearchResultViewModel> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var normalizedQuery = query?.Trim() ?? string.Empty;

        return [.. candidates
            .Select((candidate, sourceIndex) => new
            {
                Candidate = candidate,
                SourceIndex = sourceIndex,
                Score = Score(candidate, normalizedQuery),
            })
            .Where(item => item.Score != NoMatch)
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Candidate.IsAvailable ? 0 : 1)
            .ThenBy(item => item.Candidate.Kind)
            .ThenBy(item => item.Candidate.Kind == LauncherSearchResultKind.RecentSession
                ? item.SourceIndex
                : 0)
            .ThenBy(item => item.Candidate.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => StableTargetId(item.Candidate.Target), StringComparer.Ordinal)
            .Select(item => item.Candidate)];
    }

    public static int FindNextAvailableIndex(
        IReadOnlyList<LauncherSearchResultViewModel> items,
        int currentIndex,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || direction == 0)
        {
            return -1;
        }

        var step = Math.Sign(direction);
        var start = currentIndex < 0
            ? step > 0 ? 0 : items.Count - 1
            : Mod(currentIndex + step, items.Count);
        for (var visited = 0; visited < items.Count; visited++)
        {
            var index = Mod(start + (visited * step), items.Count);
            if (items[index].IsAvailable)
            {
                return index;
            }
        }

        return -1;
    }

    public static LauncherSearchResultViewModel? ResolveAvailableSelection(
        IReadOnlyList<LauncherSearchResultViewModel> items,
        LauncherSearchTarget? preferredTarget)
    {
        ArgumentNullException.ThrowIfNull(items);
        return preferredTarget is null
            ? items.FirstOrDefault(item => item.IsAvailable)
            : items.FirstOrDefault(item =>
                item.IsAvailable
                && string.Equals(StableTargetId(item.Target), StableTargetId(preferredTarget), StringComparison.Ordinal))
                ?? items.FirstOrDefault(item => item.IsAvailable);
    }

    public static LauncherSearchTarget? ConfirmSelection(
        LauncherSearchResultViewModel? selected) =>
        selected is { IsAvailable: true } ? selected.Target : null;

    private static int Score(LauncherSearchResultViewModel candidate, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var titleScore = MatchScore(candidate.Title, query);
        if (titleScore != NoMatch)
        {
            return titleScore;
        }

        var secondaryScore = candidate.SearchTerms
            .Select(term => MatchScore(term, query))
            .DefaultIfEmpty(NoMatch)
            .Min();
        return secondaryScore == NoMatch ? NoMatch : secondaryScore + 30;
    }

    private static int MatchScore(string value, string query)
    {
        if (string.Equals(value, query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? 20 : NoMatch;
    }

    private static string StableTargetId(LauncherSearchTarget target) => target switch
    {
        LauncherSearchTarget.CreatePanel createPanel => $"create:{createPanel.Kind}",
        LauncherSearchTarget.Command command =>
            $"command:{command.Id.Value}{command.InvocationKey}",
        LauncherSearchTarget.Connection connection => $"connection:{connection.Id.Value}",
        LauncherSearchTarget.FileConnection fileConnection =>
            $"file-connection:{fileConnection.Id.Value}",
        LauncherSearchTarget.DatabaseConnection databaseConnection =>
            $"database-connection:{databaseConnection.Id.Value}",
        LauncherSearchTarget.Screen screen => $"screen:{screen.Id.Value}",
        LauncherSearchTarget.Workspace workspace => $"workspace:{workspace.Id.Value}",
        LauncherSearchTarget.RecentSession recent => $"recent:{recent.Id.Value}",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;
}
