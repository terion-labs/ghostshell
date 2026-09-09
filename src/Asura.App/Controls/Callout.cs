using Avalonia;
using Avalonia.Controls;
using FluentIcons.Common;

namespace Asura.App.Controls;

/// <summary>
/// A sentence the page wants read before the user acts: a scope note, a
/// warning, a consequence. One glyph, an optional title, the sentence, and an
/// optional footnote, on the card tone that says how seriously to take it.
///
/// This shape was hand-built over a dozen times — a raw <c>Border</c> with a
/// soft fill, an icon whose size drifted between 15, 16, and 17, and a muted
/// paragraph — which is how the same warning came to look different on every
/// page that gave it.
/// </summary>
internal sealed class Callout : ContentControl
{
    public static readonly StyledProperty<SurfaceTone> ToneProperty =
        AvaloniaProperty.Register<Callout, SurfaceTone>(
            nameof(Tone),
            SurfaceTone.Notice);

    public static readonly StyledProperty<Symbol> GlyphProperty =
        AvaloniaProperty.Register<Callout, Symbol>(nameof(Glyph), Symbol.Info);

    /// <summary>Whether the glyph is drawn at all; a quiet scope note reads as prose.</summary>
    public static readonly StyledProperty<bool> ShowsGlyphProperty =
        AvaloniaProperty.Register<Callout, bool>(nameof(ShowsGlyph), true);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<Callout, string?>(nameof(Title));

    /// <summary>The sentence itself. Extra structure goes in Content below it.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Callout, string?>(nameof(Text));

    public static readonly StyledProperty<string?> FootnoteProperty =
        AvaloniaProperty.Register<Callout, string?>(nameof(Footnote));

    /// <summary>What can be done about it, at the callout's trailing edge —
    /// a Retry button beside a refresh warning.</summary>
    public static readonly StyledProperty<object?> ActionProperty =
        AvaloniaProperty.Register<Callout, object?>(nameof(Action));

    static Callout()
    {
        ToneProperty.Changed.AddClassHandler<Callout>(
            (callout, _) => callout.UpdateToneClass());
    }

    public Callout() => UpdateToneClass();

    public SurfaceTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public Symbol Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool ShowsGlyph
    {
        get => GetValue(ShowsGlyphProperty);
        set => SetValue(ShowsGlyphProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Footnote
    {
        get => GetValue(FootnoteProperty);
        set => SetValue(FootnoteProperty, value);
    }

    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Callout);

    private void UpdateToneClass()
    {
        PseudoClasses.Set(":tone-notice", Tone == SurfaceTone.Notice);
        PseudoClasses.Set(":tone-warning", Tone == SurfaceTone.Warning);
        PseudoClasses.Set(":tone-danger", Tone == SurfaceTone.Danger);
        PseudoClasses.Set(":tone-success", Tone == SurfaceTone.Success);
    }
}
