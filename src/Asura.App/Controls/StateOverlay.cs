using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentIcons.Common;

namespace Asura.App.Controls;

/// <summary>
/// A surface's not-content, with a heading, a sentence, and at most one primary
/// action. The kind supplies the shared glyph, tone, layout, and announcement.
///
/// Every panel hand-built these — over thirty blocks across the shell, several
/// byte-identical between panels, none agreeing on icon size or width. A view
/// states which of the three it is in and what the sentence says; what each
/// state looks like is decided once, here.
/// </summary>
internal sealed class StateOverlay : ContentControl
{
    public static readonly StyledProperty<StateOverlayKind> KindProperty =
        AvaloniaProperty.Register<StateOverlay, StateOverlayKind>(nameof(Kind));

    public static readonly StyledProperty<Symbol?> GlyphProperty =
        AvaloniaProperty.Register<StateOverlay, Symbol?>(nameof(Glyph));

    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<StateOverlay, string?>(nameof(Heading));

    public static readonly StyledProperty<string?> BodyProperty =
        AvaloniaProperty.Register<StateOverlay, string?>(nameof(Body));

    /// <summary>The one action the state offers — "Retry", "Reconnect". Absent,
    /// no button is drawn. Richer actions go in Content instead.</summary>
    public static readonly StyledProperty<string?> ActionLabelProperty =
        AvaloniaProperty.Register<StateOverlay, string?>(nameof(ActionLabel));

    /// <summary>The action as a command, for views that bind one; the
    /// <see cref="ActionRequested"/> event serves the ones that handle clicks.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<StateOverlay, System.Windows.Input.ICommand?>(
            nameof(ActionCommand));

    public static readonly StyledProperty<StateOverlayFocusTarget> FocusTargetProperty =
        AvaloniaProperty.Register<StateOverlay, StateOverlayFocusTarget>(
            nameof(FocusTarget));

    public static readonly DirectProperty<StateOverlay, Symbol> EffectiveGlyphProperty =
        AvaloniaProperty.RegisterDirect<StateOverlay, Symbol>(
            nameof(EffectiveGlyph),
            overlay => overlay.EffectiveGlyph);

    public static readonly DirectProperty<StateOverlay, SurfaceTone> PresentationToneProperty =
        AvaloniaProperty.RegisterDirect<StateOverlay, SurfaceTone>(
            nameof(PresentationTone),
            overlay => overlay.PresentationTone);

    public static readonly DirectProperty<StateOverlay, AutomationLiveSetting> AnnouncementModeProperty =
        AvaloniaProperty.RegisterDirect<StateOverlay, AutomationLiveSetting>(
            nameof(AnnouncementMode),
            overlay => overlay.AnnouncementMode);

    public static readonly DirectProperty<StateOverlay, string> AccessibleStatusProperty =
        AvaloniaProperty.RegisterDirect<StateOverlay, string>(
            nameof(AccessibleStatus),
            overlay => overlay.AccessibleStatus);

    private Button? _action;
    private StateOverlayPresentation _presentation =
        StateOverlayPresentation.For(StateOverlayKind.Empty);
    private Symbol _effectiveGlyph = Symbol.Info;
    private SurfaceTone _presentationTone;
    private AutomationLiveSetting _announcementMode = AutomationLiveSetting.Polite;
    private string _accessibleStatus = "Empty";

    static StateOverlay()
    {
        KindProperty.Changed.AddClassHandler<StateOverlay>(
            (overlay, _) => overlay.SynchronizePresentation());
        GlyphProperty.Changed.AddClassHandler<StateOverlay>(
            (overlay, _) => overlay.SynchronizePresentation());
        HeadingProperty.Changed.AddClassHandler<StateOverlay>(
            (overlay, _) => overlay.SynchronizePresentation());
        BodyProperty.Changed.AddClassHandler<StateOverlay>(
            (overlay, _) => overlay.SynchronizePresentation());
        FocusTargetProperty.Changed.AddClassHandler<StateOverlay>(
            (overlay, _) => overlay.RequestFocus());
        IsVisibleProperty.Changed.AddClassHandler<StateOverlay>(
            (overlay, _) => overlay.RequestFocus());
    }

    public StateOverlay() => SynchronizePresentation();

    /// <summary>Raised when the state's one action is pressed.</summary>
    public event EventHandler<RoutedEventArgs>? ActionRequested;

    public StateOverlayKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Symbol? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string? ActionLabel
    {
        get => GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public System.Windows.Input.ICommand? ActionCommand
    {
        get => GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public StateOverlayFocusTarget FocusTarget
    {
        get => GetValue(FocusTargetProperty);
        set => SetValue(FocusTargetProperty, value);
    }

    public Symbol EffectiveGlyph => _effectiveGlyph;

    public SurfaceTone PresentationTone => _presentationTone;

    public AutomationLiveSetting AnnouncementMode => _announcementMode;

    public string AccessibleStatus => _accessibleStatus;

    protected override Type StyleKeyOverride => typeof(StateOverlay);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _action?.Click -= OnActionClick;

        _action = e.NameScope.Find<Button>("PART_Action");
        _action?.Click += OnActionClick;
        RequestFocus();
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        ActionRequested?.Invoke(this, e);
    }

    private void SynchronizePresentation()
    {
        var previousGlyph = _effectiveGlyph;
        var previousTone = _presentationTone;
        var previousAnnouncement = _announcementMode;
        var previousStatus = _accessibleStatus;

        _presentation = StateOverlayPresentation.For(Kind);
        _effectiveGlyph = Glyph ?? _presentation.Glyph;
        _presentationTone = _presentation.Tone;
        _announcementMode = _presentation.LiveSetting;
        _accessibleStatus = BuildAccessibleStatus(_presentation.StateLabel, Heading, Body);

        RaisePropertyChanged(EffectiveGlyphProperty, previousGlyph, _effectiveGlyph);
        RaisePropertyChanged(PresentationToneProperty, previousTone, _presentationTone);
        RaisePropertyChanged(
            AnnouncementModeProperty,
            previousAnnouncement,
            _announcementMode);
        RaisePropertyChanged(AccessibleStatusProperty, previousStatus, _accessibleStatus);

        foreach (var kind in Enum.GetValues<StateOverlayKind>())
        {
            PseudoClasses.Set(
                $":{KindClass(kind)}",
                Kind == kind);
        }

        PseudoClasses.Set(":working", _presentation.Layout == StateOverlayLayout.Working);
        PseudoClasses.Set(":attention", _presentation.Layout == StateOverlayLayout.Attention);
    }

    private void RequestFocus()
    {
        if (!IsVisible || FocusTarget != StateOverlayFocusTarget.PrimaryAction)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsVisible
                    && FocusTarget == StateOverlayFocusTarget.PrimaryAction
                    && _action is { IsVisible: true, IsEnabled: true })
                {
                    _action.Focus();
                }
            },
            DispatcherPriority.Input);
    }

    private static string BuildAccessibleStatus(
        string stateLabel,
        string? heading,
        string? body) => string.Join(
            ". ",
            new[] { stateLabel, heading, body }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string KindClass(StateOverlayKind kind) => kind switch
    {
        StateOverlayKind.NoResults => "no-results",
        StateOverlayKind.PermissionRequired => "permission-required",
        StateOverlayKind.TerminalError => "terminal-error",
        StateOverlayKind.DestructiveAction => "destructive-action",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
