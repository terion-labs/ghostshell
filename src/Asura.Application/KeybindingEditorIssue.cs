using Asura.Core;

namespace Asura.Application;

public sealed record KeybindingEditorIssue(
    KeymapIssueSeverity Severity,
    KeymapIssueKind Kind,
    KeybindingEditorRowId RowId,
    KeybindingEditorRowId? OtherRowId,
    string Message);
