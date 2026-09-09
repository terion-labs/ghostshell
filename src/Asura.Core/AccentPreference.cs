namespace Asura.Core;

public enum AccentPreferenceKind
{
    FollowHost,
    Custom,
    AsuraBronze,
}

public sealed record AccentPreference
{
    public AccentPreference(AccentPreferenceKind kind, RgbColor? customColor = null)
    {
        if (kind == AccentPreferenceKind.Custom && customColor is null)
        {
            throw new ArgumentException("A custom accent requires a color.", nameof(customColor));
        }

        if (kind != AccentPreferenceKind.Custom && customColor is not null)
        {
            throw new ArgumentException("Only a custom accent may specify a color.", nameof(customColor));
        }

        Kind = kind;
        CustomColor = customColor;
    }

    public AccentPreferenceKind Kind { get; }

    public RgbColor? CustomColor { get; }

    public static AccentPreference FollowHost { get; } = new(AccentPreferenceKind.FollowHost);

    public static AccentPreference AsuraBronze { get; } = new(AccentPreferenceKind.AsuraBronze);

    public static AccentPreference Custom(RgbColor color) => new(AccentPreferenceKind.Custom, color);
}
