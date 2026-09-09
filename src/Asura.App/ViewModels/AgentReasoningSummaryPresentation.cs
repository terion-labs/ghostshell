namespace Asura.App.ViewModels;

internal static class AgentReasoningSummaryPresentation
{
    public static string Format(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var normalized = summary.ReplaceLineEndings("\n").Trim();
        return TryFormatBoldStages(normalized, out var stages)
            ? stages
            : normalized;
    }

    private static bool TryFormatBoldStages(string summary, out string formatted)
    {
        var stages = new List<string>();
        var position = 0;
        while (position < summary.Length)
        {
            while (position < summary.Length && char.IsWhiteSpace(summary[position]))
            {
                position++;
            }

            if (position >= summary.Length)
            {
                break;
            }

            if (!summary.AsSpan(position).StartsWith("**", StringComparison.Ordinal))
            {
                formatted = string.Empty;
                return false;
            }

            var contentStart = position + 2;
            var contentEnd = summary.IndexOf("**", contentStart, StringComparison.Ordinal);
            if (contentEnd < 0)
            {
                formatted = string.Empty;
                return false;
            }

            var stage = summary[contentStart..contentEnd].Trim();
            if (stage.Length == 0)
            {
                formatted = string.Empty;
                return false;
            }

            stages.Add(stage);
            position = contentEnd + 2;
        }

        if (stages.Count == 0)
        {
            formatted = string.Empty;
            return false;
        }

        // Responses summary parts are individually bold. Older saved turns
        // concatenate them while newer ones separate them with blank lines;
        // a turn can contain both shapes after provider reconnects. Treat the
        // markers as part boundaries only when they cover the entire summary,
        // leaving ordinary inline Markdown untouched.
        formatted = string.Join("\n\n", stages);
        return true;
    }

    public static string LatestStage(string? summary)
    {
        var formatted = Format(summary);
        if (formatted.Length == 0)
        {
            return string.Empty;
        }

        var boundary = formatted.LastIndexOf("\n\n", StringComparison.Ordinal);
        return boundary < 0
            ? formatted
            : formatted[(boundary + 2)..].Trim();
    }
}
