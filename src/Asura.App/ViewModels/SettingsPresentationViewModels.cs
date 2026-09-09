using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>One normal ANSI colour, shown as a swatch with its stored value.</summary>
public sealed record AnsiSwatchViewModel(string Name, string Hex);

public sealed record LayoutCardViewModel(
    LayoutId Id,
    long Revision,
    string Name,
    int Rows,
    int Columns,
    int SlotCount,
    IReadOnlyList<LauncherScreenPanelPreviewViewModel> PreviewPanels)
{
    /// <summary>Grid extent as columns × rows; rows alone do not identify a grid.</summary>
    public string GridSummary => $"{Columns} × {Rows}";

    /// <summary>
    /// Value comparison for list refreshes: the preview list is a collection, so
    /// the record's own equality would degrade to reference identity and churn
    /// every card on every catalog change.
    /// </summary>
    public bool PresentsSameAs(LayoutCardViewModel other) =>
        Id == other.Id
        && Revision == other.Revision
        && StringComparer.Ordinal.Equals(Name, other.Name)
        && Rows == other.Rows
        && Columns == other.Columns
        && SlotCount == other.SlotCount
        && PreviewPanels.SequenceEqual(other.PreviewPanels);
}

public sealed record ProductComponentViewModel(
    string Name,
    string Version,
    string Purpose,
    string License);

public sealed record KeybindingRowViewModel(
    string Category,
    string Command,
    string Shortcut,
    string Source,
    string Status,
    bool HasConflict);

/// <summary>
/// The window-chrome half of a theme, passed separately so a caller that does
/// not present these settings can leave the stored values untouched.
/// </summary>
public sealed record ThemeChromePreference(
    InterfaceDensity Density,
    bool ShowTabBar,
    bool ShowWorkspacesPanel,
    TabStripPlacement TabStripPlacement,
    WorkspacePanelPlacement WorkspacePanelPlacement,
    bool IsTranslucent,
    int BackdropOpacityPercent,
    bool HasGlassPanels,
    bool OverridesBackdropOpacity)
{
    public static ThemeChromePreference From(ThemePreference theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new(
            theme.Density,
            theme.ShowTabBar,
            theme.ShowWorkspacesPanel,
            theme.TabStripPlacement,
            theme.WorkspacePanelPlacement,
            theme.IsTranslucent,
            theme.BackdropOpacityPercent,
            theme.HasGlassPanels,
            theme.OverridesBackdropOpacity);
    }
}
