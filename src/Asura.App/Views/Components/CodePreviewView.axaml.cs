using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Asura.App.Views.Components;

/// <summary>
/// Read-only source preview with TextMate syntax highlighting. The grammar is
/// picked from the file name, the token theme follows the application's theme
/// variant, and files without a known grammar render as plain text.
/// </summary>
public sealed partial class CodePreviewView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodePreviewView, string?>(nameof(Text));

    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<CodePreviewView, string?>(nameof(FileName));

    /// <summary>
    /// Whether the editor is exactly as tall as the code it holds. A fenced
    /// block inside a document has no scroll of its own — the document scrolls
    /// — so an editor left at its default height would show a few lines
    /// through a window and hide the rest.
    /// </summary>
    public static readonly StyledProperty<bool> FitsContentProperty =
        AvaloniaProperty.Register<CodePreviewView, bool>(nameof(FitsContent));

    /// <summary>
    /// Whether long lines wrap. Prose and code read better wrapped in a narrow
    /// preview column; a hex dump is a fixed-width grid and must not wrap, or
    /// every row folds and the columns stop lining up.
    /// </summary>
    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<CodePreviewView, bool>(nameof(WordWrap), defaultValue: true);

    /// <summary>
    /// Whether the gutter shows line numbers. A file preview earns them; a
    /// short value inside a narrow inspector column does not.
    /// </summary>
    public static readonly StyledProperty<bool> ShowLineNumbersProperty =
        AvaloniaProperty.Register<CodePreviewView, bool>(
            nameof(ShowLineNumbers),
            defaultValue: true);

    private RegistryOptions? _registryOptions;
    private TextMate.Installation? _textMate;
    private bool _highlightPending;
    private bool _presentationReady;
    private bool _isInView;

    /// <summary>
    /// Whether the currently visible editor has finished its deferred grammar
    /// installation. Design captures use this to avoid recording the plain
    /// text frame that precedes syntax highlighting.
    /// </summary>
    internal bool IsPresentationReady => _presentationReady && !_highlightPending;

    public CodePreviewView()
    {
        InitializeComponent();
        AddHandler(
            RequestBringIntoViewEvent,
            OnRequestBringIntoView,
            RoutingStrategies.Bubble);
        // A preview is a document reader, not an editor with an insertion area
        // below the last line. AvaloniaEdit otherwise includes roughly another
        // viewport in its vertical extent, so scrolling to the end can leave the
        // final line stranded at the top above a screenful of empty canvas.
        Editor.Options.AllowScrollBelowDocument = false;
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyTheme();
            ApplyTypography();
        };

        // Wrapped text is only as tall as its width allows, so a height
        // measured once — before the panel had settled on a width — is wrong
        // for the width it ends up at. Re-measuring on every layout pass is
        // what keeps a fenced block exactly as tall as its code.
        // Fitted when the text actually re-lays out — not on every layout pass
        // in the window. LayoutUpdated fires for unrelated work anywhere in the
        // tree, and each height we set starts another pass: a document with a
        // few fenced blocks turned one toggle into dozens of measure rounds.
        Editor.TextArea.TextView.VisualLinesChanged += (_, _) => FitHeightToContent();
        SizeChanged += (_, _) => FitHeightToContent();

        // A fenced block far down a document is not worth a grammar until it
        // is on its way to being looked at.
        EffectiveViewportChanged += (_, e) =>
        {
            if (e.EffectiveViewport.Height > 0 && e.EffectiveViewport.Width > 0)
            {
                _isInView = true;
                RequestHighlighting();
            }
        };
    }

    private void OnRequestBringIntoView(
        object? sender,
        RequestBringIntoViewEventArgs e)
    {
        if (FitsContent)
        {
            // AvaloniaEdit brings its read-only caret into view after a pointer
            // press. A fitted fenced block has no scroll of its own, so letting
            // that request escape moves the surrounding document instead.
            e.Handled = true;
        }
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? FileName
    {
        get => GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public bool FitsContent
    {
        get => GetValue(FitsContentProperty);
        set => SetValue(FitsContentProperty, value);
    }

    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _presentationReady = false;
        // Text set hard against the line-number column reads as one run-on
        // column; a few pixels separate the two.
        ApplyTypography();
        SyncDocument();
        RequestHighlighting();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _textMate?.Dispose();
        _textMate = null;
        _registryOptions = null;
        _presentationReady = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            SyncDocument();
        }
        else if (change.Property == FileNameProperty)
        {
            RequestHighlighting();
        }
        else if (change.Property == WordWrapProperty)
        {
            Editor.WordWrap = WordWrap;
            // Without wrapping there has to be another way to reach the end of
            // a long line.
            Editor.HorizontalScrollBarVisibility = WordWrap
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        }
        else if (change.Property == ShowLineNumbersProperty)
        {
            Editor.ShowLineNumbers = ShowLineNumbers;
        }
    }

    /// <summary>
    /// Colours the text once it is on screen. Installing a grammar costs tens
    /// of milliseconds — enough to be felt — and nothing about it needs to
    /// happen before a reader can start reading, so the text is shown plain
    /// and coloured a moment later.
    /// </summary>
    private void RequestHighlighting()
    {
        // A block sized to its content lives inside a scrolling document, so
        // it waits until the reader has scrolled to it. A full-file preview
        // fills the panel and is always the thing being looked at.
        if (_highlightPending || !IsAttachedToVisualTree())
        {
            return;
        }

        if (FitsContent && !_isInView)
        {
            // Off-screen fenced blocks deliberately stay plain until scrolled
            // into view. Their current presentation is therefore complete.
            _presentationReady = true;
            return;
        }

        _presentationReady = false;
        _highlightPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _highlightPending = false;
                if (!IsAttachedToVisualTree())
                {
                    return;
                }

                var options = _registryOptions ??= TextMateRegistries.For(CurrentThemeName());
                // Nothing to install for a file no grammar covers: compiling a
                // grammar costs tens of milliseconds, and a plain fenced block
                // would pay it for a highlighting that never happens.
                if (_textMate is null && ResolveLanguage(FileName) is null)
                {
                    _presentationReady = true;
                    return;
                }

                _textMate ??= Editor.InstallTextMate(options);
                SyncGrammar();
                _presentationReady = true;
            },
            DispatcherPriority.Background);
    }

    private bool IsAttachedToVisualTree() => VisualRoot is not null;

    private void SyncDocument()
    {
        Editor.Document.Text = Text ?? string.Empty;
        // A new preview starts at the top; a stale scroll offset from the
        // previous file would show the middle of the next one.
        Editor.ScrollToHome();
        FitHeightToContent();
    }

    private void FitHeightToContent()
    {
        if (!FitsContent)
        {
            return;
        }

        // Taken from what the view actually laid out rather than from a line
        // height read before the document had one: multiplying a placeholder
        // line height by the line count produced a block several times too
        // tall, with the code scrolled out of sight inside it.
        var height = Editor.TextArea.TextView.DocumentHeight;
        if (height <= 0)
        {
            return;
        }

        // A block sized to its content has nothing left to scroll, so the
        // scrollbar track should not be drawn beside it either.
        Editor.VerticalScrollBarVisibility =
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        var target = Math.Ceiling(height) + 6;
        if (double.IsNaN(Editor.Height) || Math.Abs(Editor.Height - target) > 0.5)
        {
            Editor.Height = target;
            // A block that now fits has nothing to scroll; an offset left from
            // the moment it did not fit would hide its first lines.
            Editor.ScrollToHome();
        }
    }

    private void SyncGrammar()
    {
        if (_textMate is null || _registryOptions is null)
        {
            return;
        }

        var language = ResolveLanguage(FileName);
        _textMate.SetGrammar(language is null
            ? null
            : _registryOptions.GetScopeByLanguageId(language.Id));
    }

    private Language? ResolveLanguage(string? fileName)
    {
        if (_registryOptions is null
            || SourcePreviewGrammar.ResolveExtension(fileName) is not { } extension)
        {
            return null;
        }

        return _registryOptions.GetLanguageByExtension(extension);
    }

    private void ApplyTheme()
    {
        if (_textMate is null || _registryOptions is null)
        {
            return;
        }

        _textMate.SetTheme(_registryOptions.LoadTheme(CurrentThemeName()));
    }

    private void ApplyTypography()
    {
        Editor.TextArea.TextView.Margin = new Thickness(8, 0, 0, 0);
        if (this.TryFindResource("ShellDataFontFamily", ActualThemeVariant, out var family)
            && family is Avalonia.Media.FontFamily fontFamily)
        {
            Editor.FontFamily = fontFamily;
        }

        if (this.TryFindResource("ShellFontSize11", ActualThemeVariant, out var size)
            && size is double fontSize)
        {
            Editor.FontSize = fontSize;
            return;
        }

        Editor.FontSize = 13;
    }

    private ThemeName CurrentThemeName() =>
        ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;

}
