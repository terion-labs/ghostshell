using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

public enum WorkspaceEditorEntryKind
{
    Connection,
    SavedScreen,
    WorkspaceTab,
}

public enum WorkspaceEditorCancelDisposition
{
    Close,
    ConfirmDiscard,
}

public sealed record WorkspaceEditorSaveRequest(
    WorkspaceDefinition Definition,
    long? ExpectedRevision);

public sealed record WorkspaceEditorOperationResult(
    bool IsSuccess,
    WorkspaceEntryId? EntryId,
    string? Error)
{
    public static WorkspaceEditorOperationResult Applied(WorkspaceEntryId entryId) =>
        new(true, entryId, null);

    public static WorkspaceEditorOperationResult Rejected(string error) =>
        new(false, null, error);
}

/// <summary>
/// What a new tab opens.
///
/// A tab holds one thing: a connection, or a screen. "Just for this workspace"
/// qualifies that thing rather than the tab — a screen created here belongs to
/// the workspace and nothing else opens it, where a linked one stays shared.
/// </summary>
public abstract record WorkspaceTabSource
{
    private WorkspaceTabSource()
    {
    }

    /// <summary>A saved connection, opened as its own tab.</summary>
    public sealed record Connection(ConnectionId Id) : WorkspaceTabSource;

    /// <summary>A saved screen, still shared: later edits to it apply here too.</summary>
    public sealed record LinkedScreen(ScreenId Id) : WorkspaceTabSource;

    /// <summary>A copy of a saved screen, from here on this workspace's alone.</summary>
    public sealed record CopiedScreen(ScreenId Id) : WorkspaceTabSource;

    /// <summary>A screen that exists only in this workspace, built from a layout.</summary>
    public sealed record NewScreen(LayoutId LayoutId, string Name) : WorkspaceTabSource;
}

public sealed record WorkspaceScreenOption(
    ScreenId Id,
    string Name,
    string LayoutName,
    bool IsAvailable)
{
    public string DisplayName => IsAvailable
        ? $"{Name} · {LayoutName}"
        : $"Missing · {Name}";
}

public sealed record WorkspaceLayoutOption(
    LayoutId Id,
    string Name,
    bool IsAvailable,
    LayoutDefinition? Definition)
{
    public string DisplayName => IsAvailable ? Name : $"Missing · {Name}";
}

public sealed record WorkspaceLayoutSlotOption(
    LayoutSlotId Id,
    string Name,
    bool IsAvailable)
{
    public string DisplayName => IsAvailable ? Name : $"Missing · {Name}";
}

public sealed record WorkspaceTerminalMultiplexingOption(
    TerminalMultiplexingMode? Mode,
    string DisplayName);

public sealed record WorkspaceBrowserProfileOption(
    WorkspaceBrowserProfileMode? Mode,
    string DisplayName);

/// <summary>
/// One choice in the workspace icon picker. <paramref name="Keywords"/> exists so
/// the picker can be searched by purpose ("prod", "db") and not only by the
/// icon's own name.
/// </summary>
public sealed record WorkspaceIconOption(
    string Id,
    string Name,
    Symbol Symbol = Symbol.Window,
    string Keywords = "")
{
    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Id.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Keywords.Contains(term, StringComparison.OrdinalIgnoreCase);
}
