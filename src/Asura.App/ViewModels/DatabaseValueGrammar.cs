using System.Text.Json;
using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>
/// Which grammar a database value should be highlighted with.
///
/// Two honest tiers: the column's declared kind — a json/jsonb column is JSON
/// because the driver said so — and, for plain text columns only, a bounded
/// sniff of the value itself. The sniff is conservative: it answers only when
/// the shape is unambiguous, and a wrong guess costs colours, never data.
/// </summary>
public static class DatabaseValueGrammar
{
    /// <summary>Values larger than this are not worth parsing to colour.</summary>
    private const int SniffLimit = 256 * 1024;

    public static string? DetectExtension(DatabaseValueKind kind, string? text)
    {
        if (kind == DatabaseValueKind.Json)
        {
            return ".json";
        }

        if (kind != DatabaseValueKind.Text
            || text is null
            || text.Length > SniffLimit)
        {
            return null;
        }

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length < 2)
        {
            return null;
        }

        if (trimmed[0] is '{' or '[')
        {
            return ParsesAsJson(text) ? ".json" : null;
        }

        if (trimmed[0] == '<')
        {
            return trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<div", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<p>", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<span", StringComparison.OrdinalIgnoreCase)
                ? ".html"
                : ".xml";
        }

        return null;
    }

    private static bool ParsesAsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
