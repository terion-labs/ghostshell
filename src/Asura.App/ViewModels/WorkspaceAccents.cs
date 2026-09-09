namespace Asura.App.ViewModels;

/// <summary>
/// One accent choice in the workspace editor.
/// </summary>
public sealed record WorkspaceAccentOption(string Name, string Hex);

/// <summary>
/// The workspace accent presets.
///
/// A workspace accent is an identity colour shown at small sizes — a tab chip, a
/// list tile — so the presets are chosen to stay distinguishable from each other
/// there rather than to cover the colour space evenly.
/// </summary>
internal static class WorkspaceAccents
{
    public static IReadOnlyList<WorkspaceAccentOption> All { get; } =
    [
        new("Bronze", "#B8793A"),
        new("Amber", "#E08C2B"),
        new("Green", "#5FA97A"),
        new("Blue", "#5B8FD1"),
        new("Violet", "#8F7BD4"),
        new("Rose", "#C87BA4"),
        new("Teal", "#4FA9A2"),
        new("Slate", "#7E8794"),
    ];
}
