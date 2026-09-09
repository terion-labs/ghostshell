using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Asura.App.Views.Components;

/// <summary>
/// The editable sibling of <see cref="CodePreviewView"/>: an AvaloniaEdit
/// editor with a bindable <see cref="Text"/>, an optional TextMate grammar
/// chosen by extension, and a watermark. The grammar installs lazily and only
/// when one actually applies, so a plain-text box costs what a TextBox does.
/// </summary>
public sealed partial class CodeEditBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditBox, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>".sql", ".json", … — or null for plain text.</summary>
    public static readonly StyledProperty<string?> GrammarExtensionProperty =
        AvaloniaProperty.Register<CodeEditBox, string?>(nameof(GrammarExtension));

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<CodeEditBox, string?>(nameof(Watermark));

    private RegistryOptions? _registryOptions;
    private TextMate.Installation? _textMate;
    private bool _syncingText;
    private bool _highlightPending;

    public CodeEditBox()
    {
        InitializeComponent();
        InitializeSqlIntelligence();
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyTheme();
            ApplySqlDiagnosticTheme();
        };
        Editor.Document.TextChanged += (_, _) =>
        {
            OnSqlDocumentChanged();
            if (_syncingText)
            {
                return;
            }

            _syncingText = true;
            try
            {
                SetCurrentValue(TextProperty, Editor.Document.Text);
            }
            finally
            {
                _syncingText = false;
            }

            SyncWatermark();
        };
        // The inner editor claims Enter and friends before they can bubble, so
        // consumers listen to the tunnel-stage relay instead of KeyDown.
        Editor.AddHandler(
            KeyDownEvent,
            OnEditorTunnelKeyDown,
            RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Key events seen before the editor consumes them — the place to bind
    /// Enter/Cmd+Enter/Escape behaviors that must beat the editor's own.
    /// </summary>
    public event EventHandler<KeyEventArgs>? EditorKeyDown;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? GrammarExtension
    {
        get => GetValue(GrammarExtensionProperty);
        set => SetValue(GrammarExtensionProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public void FocusEditor(bool caretToEnd = false)
    {
        // The text area is what actually takes keystrokes; focusing the outer
        // editor control does not reliably delegate on every platform.
        Editor.TextArea.Focus();
        if (caretToEnd)
        {
            Editor.CaretOffset = Editor.Document.TextLength;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncDocument();
        RequestHighlighting();
        AttachSqlIntelligence();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _textMate?.Dispose();
        _textMate = null;
        _registryOptions = null;
        DetachSqlIntelligence();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            SyncDocument();
        }
        else if (change.Property == GrammarExtensionProperty)
        {
            RequestHighlighting();
        }
        else if (change.Property == WatermarkProperty)
        {
            WatermarkText.Text = Watermark;
            SyncWatermark();
        }
        else if (change.Property == SqlLanguageSessionProperty)
        {
            RestartSqlIntelligence();
        }
        else if (change.Property == SqlLanguageStatusProperty)
        {
            ApplySqlLanguageStatus();
        }
        else if (change.Property == SqlCompletionContextProperty)
        {
            RestartSqlCompletionContext();
        }
    }

    private void SyncDocument()
    {
        if (_syncingText)
        {
            return;
        }

        var text = Text ?? string.Empty;
        if (!string.Equals(Editor.Document.Text, text, StringComparison.Ordinal))
        {
            _syncingText = true;
            try
            {
                Editor.Document.Text = text;
            }
            finally
            {
                _syncingText = false;
            }
        }

        SyncWatermark();
    }

    private void SyncWatermark() =>
        WatermarkText.IsVisible = !string.IsNullOrEmpty(Watermark)
            && Editor.Document.TextLength == 0;

    /// <summary>
    /// Colours the text a moment after it is on screen — installing a grammar
    /// costs tens of milliseconds and nothing about it blocks typing.
    /// </summary>
    private void RequestHighlighting()
    {
        if (_highlightPending || VisualRoot is null)
        {
            return;
        }

        _highlightPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _highlightPending = false;
                if (VisualRoot is null)
                {
                    return;
                }

                var options = _registryOptions ??= TextMateRegistries.For(CurrentThemeName());
                if (_textMate is null && ResolveLanguage() is null)
                {
                    return;
                }

                _textMate ??= Editor.InstallTextMate(options);
                SyncGrammar();
            },
            DispatcherPriority.Background);
    }

    private void SyncGrammar()
    {
        if (_textMate is null || _registryOptions is null)
        {
            return;
        }

        var language = ResolveLanguage();
        _textMate.SetGrammar(language is null
            ? null
            : _registryOptions.GetScopeByLanguageId(language.Id));
    }

    private Language? ResolveLanguage() =>
        _registryOptions is { } options && GrammarExtension is { Length: > 0 } extension
            ? options.GetLanguageByExtension(extension)
            : null;

    private void ApplyTheme()
    {
        if (_textMate is null || _registryOptions is null)
        {
            return;
        }

        _textMate.SetTheme(_registryOptions.LoadTheme(CurrentThemeName()));
    }

    private ThemeName CurrentThemeName() =>
        ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;
}
