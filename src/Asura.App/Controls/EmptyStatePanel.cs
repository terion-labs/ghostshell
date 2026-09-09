using Avalonia;
using Avalonia.Controls;
using FluentIcons.Common;

namespace Asura.App.Controls;

/// <summary>
/// What a view shows when it has nothing to show: a glyph, a heading, a sentence
/// explaining why, and whatever the user can do about it.
///
/// It was hand-built at every use — a fixed 54×54 tile with a hardcoded radius, a
/// 27pt glyph, a heading, a body capped at some width, and a centring that had to
/// be got right at four levels because a <c>StackPanel</c> ignores a vertical
/// centre. Getting it wrong is what put the agent panel's empty state in the top
/// third of a blank panel through three separate attempts to fix it.
/// </summary>
internal sealed class EmptyStatePanel : ContentControl
{
    public static readonly StyledProperty<Symbol> GlyphProperty =
        AvaloniaProperty.Register<EmptyStatePanel, Symbol>(nameof(Glyph), Symbol.Info);

    public static readonly StyledProperty<string> HeadingProperty =
        AvaloniaProperty.Register<EmptyStatePanel, string>(nameof(Heading), string.Empty);

    public static readonly StyledProperty<string?> BodyProperty =
        AvaloniaProperty.Register<EmptyStatePanel, string?>(nameof(Body));

    /// <summary>
    /// Whether the glyph is shown at all. A state that appears inside an already
    /// small panel says more with one line than with an illustration.
    /// </summary>
    public static readonly StyledProperty<bool> ShowsGlyphProperty =
        AvaloniaProperty.Register<EmptyStatePanel, bool>(nameof(ShowsGlyph), defaultValue: true);

    static EmptyStatePanel()
    {
        BodyProperty.Changed.AddClassHandler<EmptyStatePanel>(
            (panel, _) => panel.UpdateStateClasses());
        ShowsGlyphProperty.Changed.AddClassHandler<EmptyStatePanel>(
            (panel, _) => panel.UpdateStateClasses());
    }

    public EmptyStatePanel() => UpdateStateClasses();

    public Symbol Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public bool ShowsGlyph
    {
        get => GetValue(ShowsGlyphProperty);
        set => SetValue(ShowsGlyphProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(EmptyStatePanel);

    private void UpdateStateClasses()
    {
        PseudoClasses.Set(":bodied", !string.IsNullOrWhiteSpace(Body));
        PseudoClasses.Set(":glyphed", ShowsGlyph);
    }
}
