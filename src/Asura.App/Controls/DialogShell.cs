using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Asura.App.Controls;

/// <summary>
/// Which of the shell's two dialog builds this window is.
/// </summary>
internal enum DialogChrome
{
    /// <summary>A focused task: a name prompt, a transfer, a recorder.</summary>
    Utility,

    /// <summary>A definition editor: larger title, identity subtitle, close glyph.</summary>
    Editor,
}

/// <summary>
/// A dialog's frame: the header band, the scrolling body, and the footer with
/// its rule and its button cluster.
///
/// Ten dialogs carried this by hand in two dialects — a "TopChrome" band whose
/// subtitle size drifted between files, and an editor grid whose row heights
/// came in three variants and whose close glyph was a literal "×" in three of
/// four. A dialog states its title, its body, and its buttons; the frame is
/// decided here.
/// </summary>
internal sealed class DialogShell : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogShell, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogShell, string?>(nameof(Subtitle));

    /// <summary>Extra header state — a status chip beside an editor's title.</summary>
    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<DialogShell, object?>(nameof(HeaderContent));

    /// <summary>The footer's button cluster, right-aligned after the hint.</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<DialogShell, object?>(nameof(Footer));

    /// <summary>The muted sentence at the footer's left edge.</summary>
    public static readonly StyledProperty<string?> FooterHintProperty =
        AvaloniaProperty.Register<DialogShell, string?>(nameof(FooterHint));

    public static readonly StyledProperty<DialogChrome> ChromeProperty =
        AvaloniaProperty.Register<DialogShell, DialogChrome>(nameof(Chrome));

    public static readonly StyledProperty<bool> ShowsCloseProperty =
        AvaloniaProperty.Register<DialogShell, bool>(nameof(ShowsClose));

    /// <summary>
    /// Whether the body scrolls. On for editors; a fixed-size utility dialog
    /// sizes itself to its content instead.
    /// </summary>
    public static readonly StyledProperty<bool> ScrollsProperty =
        AvaloniaProperty.Register<DialogShell, bool>(nameof(Scrolls), true);

    public static readonly DirectProperty<DialogShell, bool> IsFooterVisibleProperty =
        AvaloniaProperty.RegisterDirect<DialogShell, bool>(
            nameof(IsFooterVisible),
            shell => shell.IsFooterVisible);

    private bool _isFooterVisible;
    private Button? _close;

    static DialogShell()
    {
        ChromeProperty.Changed.AddClassHandler<DialogShell>(
            (shell, _) => shell.UpdateChromeClass());
        FooterProperty.Changed.AddClassHandler<DialogShell>(
            (shell, _) => shell.UpdateFooterVisibility());
        FooterHintProperty.Changed.AddClassHandler<DialogShell>(
            (shell, _) => shell.UpdateFooterVisibility());
    }

    public DialogShell()
    {
        UpdateChromeClass();
        UpdateFooterVisibility();
    }

    /// <summary>Raised when the user presses the header's close glyph.</summary>
    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public string? FooterHint
    {
        get => GetValue(FooterHintProperty);
        set => SetValue(FooterHintProperty, value);
    }

    public DialogChrome Chrome
    {
        get => GetValue(ChromeProperty);
        set => SetValue(ChromeProperty, value);
    }

    public bool ShowsClose
    {
        get => GetValue(ShowsCloseProperty);
        set => SetValue(ShowsCloseProperty, value);
    }

    public bool Scrolls
    {
        get => GetValue(ScrollsProperty);
        set => SetValue(ScrollsProperty, value);
    }

    public bool IsFooterVisible
    {
        get => _isFooterVisible;
        private set => SetAndRaise(IsFooterVisibleProperty, ref _isFooterVisible, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _close?.Click -= OnCloseClick;

        _close = e.NameScope.Find<Button>("PART_Close");
        _close?.Click += OnCloseClick;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        CloseRequested?.Invoke(this, e);
    }

    private void UpdateChromeClass() =>
        PseudoClasses.Set(":editor", Chrome == DialogChrome.Editor);

    private void UpdateFooterVisibility() =>
        IsFooterVisible = Footer is not null || FooterHint is not null;
}
