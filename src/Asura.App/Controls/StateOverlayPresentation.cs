using Avalonia.Automation;
using FluentIcons.Common;

namespace Asura.App.Controls;

/// <summary>The reason a surface is showing status instead of ordinary content.</summary>
internal enum StateOverlayKind
{
    Empty,
    NoResults,
    Loading,
    Offline,
    PermissionRequired,
    Unsupported,
    Stale,
    Partial,
    Conflict,
    Retry,
    TerminalError,
    Cancelled,
    DestructiveAction,
}

/// <summary>Where focus moves when a state replaces the active content.</summary>
internal enum StateOverlayFocusTarget
{
    Preserve,
    PrimaryAction,
}

internal enum StateOverlayLayout
{
    Neutral,
    Working,
    Attention,
}

/// <summary>
/// The non-colour presentation contract for one state. Every state has a label
/// and glyph even when two states share a tone or layout.
/// </summary>
internal sealed record StateOverlayPresentation(
    Symbol Glyph,
    SurfaceTone Tone,
    StateOverlayLayout Layout,
    AutomationLiveSetting LiveSetting,
    string StateLabel)
{
    public static StateOverlayPresentation For(StateOverlayKind kind) => kind switch
    {
        StateOverlayKind.Empty =>
            new(Symbol.Info, SurfaceTone.Default, StateOverlayLayout.Neutral,
                AutomationLiveSetting.Polite, "Empty"),
        StateOverlayKind.NoResults =>
            new(Symbol.Search, SurfaceTone.Default, StateOverlayLayout.Neutral,
                AutomationLiveSetting.Polite, "No results"),
        StateOverlayKind.Loading =>
            new(Symbol.ArrowSync, SurfaceTone.Default, StateOverlayLayout.Working,
                AutomationLiveSetting.Polite, "Loading"),
        StateOverlayKind.Offline =>
            new(Symbol.Globe, SurfaceTone.Notice, StateOverlayLayout.Attention,
                AutomationLiveSetting.Polite, "Offline"),
        StateOverlayKind.PermissionRequired =>
            new(Symbol.Shield, SurfaceTone.Warning, StateOverlayLayout.Attention,
                AutomationLiveSetting.Assertive, "Permission required"),
        StateOverlayKind.Unsupported =>
            new(Symbol.Warning, SurfaceTone.Notice, StateOverlayLayout.Neutral,
                AutomationLiveSetting.Polite, "Unsupported"),
        StateOverlayKind.Stale =>
            new(Symbol.History, SurfaceTone.Notice, StateOverlayLayout.Attention,
                AutomationLiveSetting.Polite, "Stale"),
        StateOverlayKind.Partial =>
            new(Symbol.Info, SurfaceTone.Warning, StateOverlayLayout.Attention,
                AutomationLiveSetting.Polite, "Partially complete"),
        StateOverlayKind.Conflict =>
            new(Symbol.Warning, SurfaceTone.Warning, StateOverlayLayout.Attention,
                AutomationLiveSetting.Assertive, "Conflict"),
        StateOverlayKind.Retry =>
            new(Symbol.ArrowClockwise, SurfaceTone.Notice, StateOverlayLayout.Attention,
                AutomationLiveSetting.Polite, "Retry available"),
        StateOverlayKind.TerminalError =>
            new(Symbol.ErrorCircle, SurfaceTone.Danger, StateOverlayLayout.Attention,
                AutomationLiveSetting.Assertive, "Failed"),
        StateOverlayKind.Cancelled =>
            new(Symbol.Dismiss, SurfaceTone.Default, StateOverlayLayout.Neutral,
                AutomationLiveSetting.Polite, "Cancelled"),
        StateOverlayKind.DestructiveAction =>
            new(Symbol.Delete, SurfaceTone.Danger, StateOverlayLayout.Attention,
                AutomationLiveSetting.Assertive, "Destructive action"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
