namespace Asura.Core;

internal static class RuntimeInstanceValidation
{
    private const int MaximumTitleLength = 200;

    public static void RequireId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A runtime identifier is required.", parameterName);
        }
    }

    public static string RequireTitle(string title, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title, parameterName);
        var normalized = title.Trim();
        if (normalized.Length > MaximumTitleLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A runtime title must be at most {MaximumTitleLength} printable characters.",
                parameterName);
        }

        return normalized;
    }

    public static void RequireUniqueIds(
        IEnumerable<string> ids,
        string message,
        string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            RequireId(id, parameterName);
            if (!seen.Add(id))
            {
                throw new ArgumentException(message, parameterName);
            }
        }
    }
}
