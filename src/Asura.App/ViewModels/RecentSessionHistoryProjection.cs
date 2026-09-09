using Asura.Core;

namespace Asura.App.ViewModels;

internal static class RecentSessionHistoryProjection
{
    private const int NoMatch = int.MaxValue;

    public static IReadOnlyList<RecentSessionHistoryItemViewModel> Search(
        string? query,
        IEnumerable<RecentSessionHistoryItemViewModel> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var normalizedQuery = query?.Trim() ?? string.Empty;

        return [.. candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = Score(candidate, normalizedQuery),
            })
            .Where(item => item.Score != NoMatch)
            .OrderBy(item => item.Score)
            .ThenByDescending(item => item.Candidate.Record.LastUsedAt)
            .ThenByDescending(item => item.Candidate.Record.StartedAt)
            .ThenBy(item => item.Candidate.SessionId.Value, StringComparer.Ordinal)
            .Select(item => item.Candidate)];
    }

    public static RecentSessionHistoryItemViewModel? ResolveSelection(
        IReadOnlyList<RecentSessionHistoryItemViewModel> items,
        SessionId? preferredSessionId)
    {
        ArgumentNullException.ThrowIfNull(items);
        return preferredSessionId is { } sessionId
            ? items.FirstOrDefault(item => item.SessionId == sessionId) ?? items.FirstOrDefault()
            : items.FirstOrDefault();
    }

    private static int Score(RecentSessionHistoryItemViewModel candidate, string query)
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

        var secondaryScore = new[]
            {
                candidate.SourceKind,
                candidate.SourceIdentifier,
                candidate.SessionIdentifier,
                candidate.PanelKindName,
                candidate.OutcomeName,
            }
            .Select(value => MatchScore(value, query))
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
}
