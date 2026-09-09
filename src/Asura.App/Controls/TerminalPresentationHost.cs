using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Asura.App.Controls;

/// <summary>
/// Presents every terminal through the Avalonia renderer while preserving one
/// XAML-facing control contract. The operating system no longer changes the view
/// hierarchy: platform differences stop at the session and input-adapter boundary.
/// </summary>
public sealed class TerminalPresentationHost : ContentControl
{
    private static readonly IBrush StartingBrush = Brush.Parse("#8B8B91");

    public static readonly StyledProperty<ISessionHostClient?> SessionClientProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, ISessionHostClient?>(
            nameof(SessionClient));

    public static readonly StyledProperty<EnsureTerminalSessionRequest?> SessionRequestProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, EnsureTerminalSessionRequest?>(
            nameof(SessionRequest));

    /// <summary>
    /// The live render profile. Separate from <see cref="SessionRequest"/> so a
    /// typography change reaches an open panel without restarting its session.
    /// </summary>
    public static readonly StyledProperty<TerminalRenderProfileSnapshot?> RenderProfileProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, TerminalRenderProfileSnapshot?>(
            nameof(RenderProfile));

    public static readonly StyledProperty<double> BackgroundOpacityProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, double>(
            nameof(BackgroundOpacity),
            1);

    public static readonly StyledProperty<ClientId?> ClientIdProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, ClientId?>(nameof(ClientId));

    public static readonly StyledProperty<TerminalStartupCommandDispatcher?> StartupCommandDispatcherProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, TerminalStartupCommandDispatcher?>(
            nameof(StartupCommandDispatcher));

    public static readonly StyledProperty<TerminalStartupCommandDispatchState?>
        StartupCommandDispatchStateProperty =
            AvaloniaProperty.Register<
                TerminalPresentationHost,
                TerminalStartupCommandDispatchState?>(
                nameof(StartupCommandDispatchState));

    public static readonly DirectProperty<TerminalPresentationHost, bool> IsLiveProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, bool>(
            nameof(IsLive),
            control => control.IsLive);

    public static readonly DirectProperty<TerminalPresentationHost, string?> InitializationErrorProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, string?>(
            nameof(InitializationError),
            control => control.InitializationError);

    public static readonly DirectProperty<TerminalPresentationHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, string>(
            nameof(StatusText),
            control => control.StatusText);

    public static readonly DirectProperty<TerminalPresentationHost, IBrush> StatusBrushProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, IBrush>(
            nameof(StatusBrush),
            control => control.StatusBrush);

    public static readonly DirectProperty<TerminalPresentationHost, bool> ShowFallbackProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, bool>(
            nameof(ShowFallback),
            control => control.ShowFallback);

    public static readonly DirectProperty<TerminalPresentationHost, string> StatusMessageProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, string>(
            nameof(StatusMessage),
            control => control.StatusMessage);

    private readonly ManagedTerminalSessionHost _presentation;
    private bool _isLive;
    private string? _initializationError;
    private string _statusText = "Starting";
    private IBrush _statusBrush = StartingBrush;
    private bool _showFallback;
    private string _statusMessage = string.Empty;

    public TerminalPresentationHost()
    {
        Focusable = true;
        AutomationProperties.SetName(this, "Interactive terminal");
        AutomationProperties.SetHelpText(
            this,
            "Terminal input and output for the active panel. Terminal status changes are announced politely.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        _presentation = new ManagedTerminalSessionHost();
        _presentation.SessionSnapshotChanged += OnSessionSnapshotChanged;
        _presentation.SessionInitializationFailed += OnSessionInitializationFailed;
        _presentation.StartupCommandDispatchCompleted += OnStartupCommandDispatchCompleted;

        _presentation.PropertyChanged += OnPresentationPropertyChanged;
        Content = _presentation;
        SynchronizeChildProperties();
        SynchronizeState();
    }

    public event EventHandler<TerminalSessionSnapshotEventArgs>? SessionSnapshotChanged;

    public event EventHandler<TerminalSessionFailureEventArgs>? SessionInitializationFailed;

    public event EventHandler<TerminalStartupCommandDispatchEventArgs>?
        StartupCommandDispatchCompleted;

    public ISessionHostClient? SessionClient
    {
        get => GetValue(SessionClientProperty);
        set => SetValue(SessionClientProperty, value);
    }

    public EnsureTerminalSessionRequest? SessionRequest
    {
        get => GetValue(SessionRequestProperty);
        set => SetValue(SessionRequestProperty, value);
    }

    public TerminalRenderProfileSnapshot? RenderProfile
    {
        get => GetValue(RenderProfileProperty);
        set => SetValue(RenderProfileProperty, value);
    }

    /// <summary>
    /// Alpha for the default terminal background only. This lets presentation
    /// surfaces opt into translucency without fading terminal content.
    /// </summary>
    public double BackgroundOpacity
    {
        get => GetValue(BackgroundOpacityProperty);
        set => SetValue(BackgroundOpacityProperty, value);
    }

    public ClientId? ClientId
    {
        get => GetValue(ClientIdProperty);
        set => SetValue(ClientIdProperty, value);
    }

    public TerminalStartupCommandDispatcher? StartupCommandDispatcher
    {
        get => GetValue(StartupCommandDispatcherProperty);
        set => SetValue(StartupCommandDispatcherProperty, value);
    }

    public TerminalStartupCommandDispatchState? StartupCommandDispatchState
    {
        get => GetValue(StartupCommandDispatchStateProperty);
        set => SetValue(StartupCommandDispatchStateProperty, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set => SetAndRaise(IsLiveProperty, ref _isLive, value);
    }

    public string? InitializationError
    {
        get => _initializationError;
        private set => SetAndRaise(InitializationErrorProperty, ref _initializationError, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetAndRaise(StatusBrushProperty, ref _statusBrush, value);
    }

    public bool ShowFallback
    {
        get => _showFallback;
        private set => SetAndRaise(ShowFallbackProperty, ref _showFallback, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetAndRaise(StatusMessageProperty, ref _statusMessage, value);
    }

    internal Control Presentation => _presentation;

    public ValueTask SendTextAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        _presentation.SendTextAsync(text, cancellationToken);

    /// <summary>
    /// Replays application-prefix input directly to the PTY. This deliberately bypasses the
    /// terminal keymap because those physical strokes were already resolved by the shell.
    /// </summary>
    internal async ValueTask<bool> ReplayApplicationKeyStrokesAsync(
        IReadOnlyList<KeyStroke>? strokes,
        CancellationToken cancellationToken = default)
    {
        if (strokes is null || strokes.Count == 0)
        {
            return true;
        }

        var replay = new (TerminalKeyStroke? KeyStroke, string Text)[strokes.Count];
        for (var index = 0; index < strokes.Count; index++)
        {
            if (!ManagedTerminalInput.TryMapReplayStroke(
                    strokes[index],
                    out var keyStroke,
                    out var text))
            {
                return false;
            }

            replay[index] = (keyStroke, text);
        }

        foreach (var input in replay)
        {
            if (input.KeyStroke is { } keyStroke)
            {
                await ((IManagedTerminalInputSink)_presentation).SendKeyAsync(
                    keyStroke,
                    cancellationToken);
            }
            else
            {
                await SendTextAsync(input.Text, cancellationToken);
            }
        }

        return true;
    }

    public ValueTask<string> ReadScreenAsync(CancellationToken cancellationToken = default) =>
        _presentation.ReadScreenAsync(cancellationToken);

    public bool TryCancelPendingInteraction() =>
        _presentation.TryCancelPendingInteraction();

    internal bool RequestInputFocus() => _presentation.RequestInputFocus();

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this))
        {
            RequestInputFocus();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionClientProperty
            || change.Property == SessionRequestProperty
            || change.Property == ClientIdProperty
            || change.Property == StartupCommandDispatcherProperty
            || change.Property == StartupCommandDispatchStateProperty
            || change.Property == RenderProfileProperty
            || change.Property == BackgroundOpacityProperty)
        {
            SynchronizeChildProperties();
        }
    }

    private void SynchronizeChildProperties()
    {
        _presentation.StartupCommandDispatchState = StartupCommandDispatchState;
        _presentation.SessionClient = SessionClient;
        _presentation.SessionRequest = SessionRequest;
        _presentation.RenderProfile = RenderProfile;
        _presentation.BackgroundOpacity = BackgroundOpacity;
        _presentation.ClientId = ClientId;
        _presentation.StartupCommandDispatcher = StartupCommandDispatcher;
    }

    private void OnPresentationPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.Property.Name is nameof(IsLive)
            or nameof(InitializationError)
            or nameof(StatusText)
            or nameof(StatusBrush)
            or nameof(ShowFallback)
            or nameof(StatusMessage))
        {
            SynchronizeState();
        }
    }

    private void SynchronizeState()
    {
        IsLive = _presentation.IsLive;
        InitializationError = _presentation.InitializationError;
        StatusText = _presentation.StatusText;
        StatusBrush = _presentation.StatusBrush;
        ShowFallback = _presentation.ShowFallback;
        StatusMessage = _presentation.StatusMessage;

        AutomationProperties.SetItemStatus(
            this,
            string.IsNullOrWhiteSpace(StatusMessage)
                ? StatusText
                : $"{StatusText}: {StatusMessage}");
    }

    private void OnSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e)
    {
        _ = sender;
        SessionSnapshotChanged?.Invoke(this, e);
    }

    private void OnSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e)
    {
        _ = sender;
        SessionInitializationFailed?.Invoke(this, e);
    }

    private void OnStartupCommandDispatchCompleted(
        object? sender,
        TerminalStartupCommandDispatchEventArgs e)
    {
        _ = sender;
        StartupCommandDispatchCompleted?.Invoke(this, e);
    }
}
