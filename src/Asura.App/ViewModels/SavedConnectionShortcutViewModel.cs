using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

/// <summary>
/// One actionable saved target offered by the launcher.
/// The launch list is projected from the target's domain capabilities.
///
/// A saved target is one thing whichever way it is opened, so it is one row with
/// the usual way on it and the others behind a chevron — rather than one row per
/// (target, adapter) pair, which listed the same host four times.
/// </summary>
public sealed record SavedConnectionShortcutViewModel(
    PanelConnectionOptionViewModel.Target Target,
    string Name,
    string Kind,
    string Detail,
    bool CanOpen,
    SavedConnectionLaunchViewModel DefaultLaunch,
    IReadOnlyList<SavedConnectionLaunchViewModel> AlternativeLaunches)
{
    public bool HasAlternatives => AlternativeLaunches.Count > 0;

    /// <summary>
    /// Whether this row can be edited where it stands. A saved connection can;
    /// a file provider profile is configured on its own settings page, and
    /// offering to edit it here would open something else entirely.
    /// </summary>
    public bool CanEdit => Target is PanelConnectionOptionViewModel.Target.Connection;
}

public sealed record SavedConnectionLaunchViewModel(
    PanelConnectionOptionViewModel.Target Target,
    PanelKind Panel,
    string Label,
    Symbol Icon);
