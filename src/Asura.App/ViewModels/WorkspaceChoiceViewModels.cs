using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

/// <summary>
/// One workspace in the editor's rail — the list you switch between, not the
/// one you are editing.
///
/// The rail carries the same colour and icon the shell shows elsewhere, because
/// its job is recognition: you find a workspace here by the mark you already
/// know it by, before you read its name.
/// </summary>
public sealed record WorkspaceRailItemViewModel(
    WorkspaceId Id,
    string Name,
    string Summary,
    string Color,
    Symbol IconSymbol,
    bool IsCurrent)
{
    /// <summary>
    /// The rail's summary counts what a workspace opens, split by kind, because
    /// "5 items" says nothing about whether opening it costs five connections.
    /// A workspace with nothing in it says so rather than showing "0 tabs".
    /// </summary>
    internal static WorkspaceRailItemViewModel From(WorkspaceDefinition workspace, bool isCurrent)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var tabs = workspace.Entries.Count(entry => entry is WorkspaceEntry.Tab);
        var screens = workspace.Entries.Count(entry => entry is WorkspaceEntry.ScreenReference);
        var connections = workspace.Entries.Count(entry => entry is WorkspaceEntry.ConnectionReference);
        List<string> parts = [];
        if (tabs > 0)
        {
            parts.Add(Plural(tabs, "tab"));
        }

        if (screens > 0)
        {
            parts.Add(Plural(screens, "screen"));
        }

        if (connections > 0)
        {
            parts.Add(Plural(connections, "connection"));
        }

        return new(
            workspace.Id,
            workspace.Name,
            parts.Count == 0 ? "Empty" : string.Join(" · ", parts),
            WorkspaceTints.Of(workspace),
            WorkspaceIcons.SymbolFor(workspace.Icon),
            isCurrent);
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>
/// Resolves the colour a workspace is drawn with.
///
/// A workspace saved before colours existed has neither a colour nor
/// necessarily an accent, and a tile with no colour is a hole in the rail — so
/// the identity colour falls back to the accent, and then to the shell's own,
/// rather than to nothing. Every surface that paints a workspace goes through
/// here so the rail, the launcher, and the tabs never disagree.
/// </summary>
public static class WorkspaceTints
{
    public static string Of(WorkspaceDefinition workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Of(workspace.Color, workspace.Accent);
    }

    public static string Of(string? color, string? accent)
    {
        if (!string.IsNullOrWhiteSpace(color))
        {
            return color;
        }

        return string.IsNullOrWhiteSpace(accent)
            ? ThemePreference.BronzeFallback.ToString()
            : accent;
    }
}

/// <summary>
/// One selectable tile in the workspace icon grid.
///
/// Selection lives on the item rather than on the grid because Avalonia can only
/// bind a style class from a single value, and comparing the item with the
/// editor's current choice needs both. The editor owns a private set of these,
/// so marking one selected never leaks into another editor's grid.
/// </summary>
public sealed class WorkspaceIconChoiceViewModel(WorkspaceIconOption option)
    : ObservableObject
{
    private bool _isSelected;

    public string Id => option.Id;

    public string Name => option.Name;

    public Symbol Symbol => option.Symbol;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool Matches(string term) => option.Matches(term);
}

/// <summary>
/// One colour a swatch row offers. The picker takes this rather than a concrete
/// type so the same control serves a workspace's colour, its accent, and
/// anything else the palette comes to cover.
/// </summary>
public interface IColorChoice
{
    string Name { get; }

    string Hex { get; }

    bool IsSelected { get; }
}

/// <summary>One selectable swatch in a colour row.</summary>
public sealed class WorkspaceAccentChoiceViewModel(WorkspaceAccentOption option)
    : ObservableObject, IColorChoice
{
    private bool _isSelected;

    public string Name => option.Name;

    public string Hex => option.Hex;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
