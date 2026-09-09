using Asura.Core;

namespace Asura.Application;

public sealed record KeybindingEditorRow(
    KeybindingEditorRowId Id,
    CommandId CommandId,
    string Title,
    string Category,
    CommandContext Contexts,
    KeySequence? Sequence,
    IReadOnlyDictionary<string, string> Arguments,
    bool IsUnknownCommand,
    bool CanReset,
    IReadOnlyList<KeybindingEditorIssue> Issues)
{
    public bool IsBound => Sequence is not null;

    public string Shortcut => Sequence?.ToString() ?? "Unbound";

    public bool HasBlockingConflict =>
        Issues.Any(issue => issue.Severity == KeymapIssueSeverity.Error);
}
