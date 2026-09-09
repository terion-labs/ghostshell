using TextMateSharp.Grammars;

namespace Asura.App.Views.Components;

/// <summary>
/// One TextMate registry per theme for the whole application. Building one
/// parses every shipped grammar and theme definition — far too much to repeat
/// per editor, and both the read-only previews and the editable code boxes
/// draw from the same well.
/// </summary>
internal static class TextMateRegistries
{
    private static readonly Dictionary<ThemeName, RegistryOptions> Shared = [];

    public static RegistryOptions For(ThemeName theme)
    {
        if (!Shared.TryGetValue(theme, out var options))
        {
            options = new RegistryOptions(theme);
            Shared[theme] = options;
        }

        return options;
    }
}
