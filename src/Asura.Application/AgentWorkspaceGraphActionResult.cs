using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

public enum AgentWorkspaceGraphScopeKind
{
    Panel,
    ConnectionSession,
    OpenTab,
    Workspace,
    SelectedPanels,
}

/// <summary>
/// Closed, session-free graph projections returned after one-action
/// authorization is consumed.
/// </summary>
public abstract record AgentWorkspaceGraphActionResult
{
    public const int MaximumProjectionBytes = 64 * 1024;

    private AgentWorkspaceGraphActionResult(
        AgentWorkspaceGraphScopeKind scopeKind,
        bool scopeLimited)
    {
        ScopeKind = scopeKind;
        ScopeLimited = scopeLimited;
    }

    public AgentWorkspaceGraphScopeKind ScopeKind { get; }

    public bool ScopeLimited { get; }

    public sealed record WorkspaceInspected : AgentWorkspaceGraphActionResult
    {
        internal WorkspaceInspected(
            AgentWorkspaceGraphScopeKind scopeKind,
            bool scopeLimited,
            AgentWorkspaceGraphWorkspaceInspection workspace)
            : base(scopeKind, scopeLimited)
        {
            Workspace = workspace
                ?? throw new ArgumentNullException(nameof(workspace));
        }

        public AgentWorkspaceGraphWorkspaceInspection Workspace { get; }
    }

    public sealed record TabsListed : AgentWorkspaceGraphActionResult
    {
        internal TabsListed(
            AgentWorkspaceGraphScopeKind scopeKind,
            bool scopeLimited,
            AgentWorkspaceGraphPage<AgentWorkspaceGraphTab> page)
            : base(scopeKind, scopeLimited)
        {
            Page = page ?? throw new ArgumentNullException(nameof(page));
        }

        public AgentWorkspaceGraphPage<AgentWorkspaceGraphTab> Page { get; }
    }

    public sealed record PanelsListed : AgentWorkspaceGraphActionResult
    {
        internal PanelsListed(
            AgentWorkspaceGraphScopeKind scopeKind,
            bool scopeLimited,
            AgentWorkspaceGraphPage<AgentWorkspaceGraphPanel> page)
            : base(scopeKind, scopeLimited)
        {
            Page = page ?? throw new ArgumentNullException(nameof(page));
        }

        public AgentWorkspaceGraphPage<AgentWorkspaceGraphPanel> Page { get; }
    }

}

public sealed record AgentWorkspaceGraphPage<T>
    where T : class
{
    internal AgentWorkspaceGraphPage(
        int offset,
        IReadOnlyList<T> items,
        int? nextOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > AgentWorkspaceGraphRequest.PageSize)
        {
            throw new ArgumentException(
                "A workspace graph page exceeds its fixed size.",
                nameof(items));
        }

        if (nextOffset is { } next
            && (next <= offset || next != offset + items.Count))
        {
            throw new ArgumentException(
                "A workspace graph continuation offset is invalid.",
                nameof(nextOffset));
        }

        Offset = offset;
        Items = new ReadOnlyCollection<T>(
            [.. items
                .Select(item => item ?? throw new ArgumentException(
                    "A workspace graph page cannot contain null items.",
                    nameof(items)))]);
        NextOffset = nextOffset;
    }

    public int Offset { get; }

    public int PageSize => AgentWorkspaceGraphRequest.PageSize;

    public IReadOnlyList<T> Items { get; }

    public int? NextOffset { get; }
}

public sealed record AgentWorkspaceGraphWorkspace(
    WindowInstanceId WindowId,
    WorkspaceInstanceId WorkspaceId,
    long WorkspaceRevision,
    long GraphSequence,
    AgentWorkspaceGraphTitle? Title);

public sealed record AgentWorkspaceGraphTab(
    WindowInstanceId WindowId,
    WorkspaceInstanceId WorkspaceId,
    long WorkspaceRevision,
    long GraphSequence,
    TabInstanceId TabId,
    bool IsActive,
    AgentWorkspaceGraphTitle? Title);

public sealed record AgentWorkspaceGraphPanel(
    WindowInstanceId WindowId,
    WorkspaceInstanceId WorkspaceId,
    long WorkspaceRevision,
    long GraphSequence,
    TabInstanceId TabId,
    PanelInstanceId PanelId,
    PanelKind Kind,
    bool IsVisible,
    bool IsFocused,
    AgentWorkspaceGraphTitle? Title);

public sealed record AgentWorkspaceGraphTabInspection
{
    internal AgentWorkspaceGraphTabInspection(
        AgentWorkspaceGraphTab tab,
        IReadOnlyList<AgentWorkspaceGraphPanel> panels)
    {
        Tab = tab ?? throw new ArgumentNullException(nameof(tab));
        ArgumentNullException.ThrowIfNull(panels);
        Panels = new ReadOnlyCollection<AgentWorkspaceGraphPanel>(
            [.. panels
                .Select(panel => panel ?? throw new ArgumentException(
                    "A tab inspection cannot contain null panels.",
                    nameof(panels)))]);
    }

    public AgentWorkspaceGraphTab Tab { get; }

    public IReadOnlyList<AgentWorkspaceGraphPanel> Panels { get; }
}

public sealed record AgentWorkspaceGraphWorkspaceInspection
{
    internal AgentWorkspaceGraphWorkspaceInspection(
        AgentWorkspaceGraphWorkspace workspace,
        IReadOnlyList<AgentWorkspaceGraphTabInspection> tabs)
    {
        Workspace = workspace
            ?? throw new ArgumentNullException(nameof(workspace));
        ArgumentNullException.ThrowIfNull(tabs);
        Tabs = new ReadOnlyCollection<AgentWorkspaceGraphTabInspection>(
            [.. tabs
                .Select(tab => tab ?? throw new ArgumentException(
                    "A workspace inspection cannot contain null tabs.",
                    nameof(tabs)))]);
    }

    public AgentWorkspaceGraphWorkspace Workspace { get; }

    public IReadOnlyList<AgentWorkspaceGraphTabInspection> Tabs { get; }
}

/// <summary>
/// Bounded display-only text derived from untrusted workspace graph labels.
/// Raw labels never cross the governed graph result boundary.
/// </summary>
public sealed record AgentWorkspaceGraphTitle
{
    public const int MaximumTextBytes = 128;
    private const string Redaction = "[REDACTED SECRET-BEARING TITLE]";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] SecretMarkers =
    [
        "authorization:",
        "api_key=",
        "apikey=",
        "client_secret=",
        "password=",
        "password:",
        "passwd=",
        "private_key=",
        "refresh_token=",
        "secret=",
        "token=",
        "-----begin private key-----",
        "-----begin encrypted private key-----",
        "-----begin openssh private key-----",
    ];

    private static readonly string[] TokenPrefixes =
    [
        "ghp_",
        "github_pat_",
        "sk-",
        "akia",
        "xoxb-",
        "xoxp-",
    ];

    private static readonly string[] AssignmentKeys =
    [
        "access-token",
        "access_token",
        "api-key",
        "api_key",
        "apikey",
        "authorization",
        "client-secret",
        "client_secret",
        "password",
        "passwd",
        "private-key",
        "private_key",
        "refresh-token",
        "refresh_token",
        "secret",
        "token",
    ];

    internal AgentWorkspaceGraphTitle(
        string text,
        int redactions,
        bool truncated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!IsWellFormedUnicode(text)
            || ContainsUnsafeText(text)
            || StrictUtf8.GetByteCount(text) > MaximumTextBytes)
        {
            throw new ArgumentException(
                "A projected graph title must be printable and bounded.",
                nameof(text));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(redactions);
        Text = string.Concat(text);
        Redactions = redactions;
        Truncated = truncated;
    }

    public string Text { get; }

    public int Redactions { get; }

    public bool Truncated { get; }

    internal static AgentWorkspaceGraphTitle? FromUntrusted(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var redacted = !IsWellFormedUnicode(value)
            || ContainsUnsafeText(value)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value)
            || LooksSecretBearing(value);
        var candidate = redacted ? Redaction : value;
        var bounded = TruncateUtf8(candidate, MaximumTextBytes);
        return new AgentWorkspaceGraphTitle(
            bounded,
            redacted ? 1 : 0,
            !string.Equals(candidate, bounded, StringComparison.Ordinal));
    }

    private static bool LooksSecretBearing(string value)
    {
        if (SecretMarkers.Any(marker =>
                value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || AssignmentKeys.Any(key =>
                ContainsSecretAssignment(value, key)))
        {
            return true;
        }

        return value
            .Split(
                [' ', '\t', '"', '\'', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Length >= 12
                && TokenPrefixes.Any(prefix => token.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainsSecretAssignment(
        string value,
        string key)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var keyStart = value.IndexOf(
                key,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (keyStart < 0)
            {
                return false;
            }

            searchStart = keyStart + key.Length;
            if (keyStart > 0
                && IsAssignmentIdentifier(value[keyStart - 1]))
            {
                continue;
            }

            var cursor = searchStart;
            if (cursor < value.Length && value[cursor] is '"' or '\'')
            {
                cursor++;
            }
            else if (cursor < value.Length
                     && IsAssignmentIdentifier(value[cursor]))
            {
                continue;
            }

            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length
                || value[cursor] is not (':' or '='))
            {
                continue;
            }

            cursor++;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length
                || value[cursor] is ',' or '}' or ']')
            {
                continue;
            }

            return !IsNonSecretLiteral(value, cursor);
        }

        return false;
    }

    private static bool IsAssignmentIdentifier(char value) =>
        char.IsLetterOrDigit(value)
        || value is '_' or '-';

    private static bool IsNonSecretLiteral(string value, int start)
    {
        if (value.AsSpan(start).StartsWith("\"\"", StringComparison.Ordinal)
            || value.AsSpan(start).StartsWith("''", StringComparison.Ordinal))
        {
            return true;
        }

        return IsDelimitedLiteral(value, start, "null")
            || IsDelimitedLiteral(value, start, "false")
            || IsDelimitedLiteral(value, start, "true");
    }

    private static bool IsDelimitedLiteral(
        string value,
        int start,
        string literal)
    {
        if (!value.AsSpan(start).StartsWith(
                literal,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = start + literal.Length;
        return end == value.Length
            || char.IsWhiteSpace(value[end])
            || value[end] is ',' or '}' or ']';
    }

    private static bool ContainsUnsafeText(string value) =>
        value.EnumerateRunes().Any(rune =>
            Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator);

    private static bool IsWellFormedUnicode(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (StrictUtf8.GetByteCount(value) <= maximumBytes)
        {
            return string.Concat(value);
        }

        var builder = new StringBuilder(value.Length);
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            byteCount += runeBytes;
        }

        return builder.ToString();
    }
}
