using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Application;

public sealed record BrowserViewportState
{
    public const double MaximumCssExtent = 100_000;
    public const double MinimumScale = 0.1;
    public const double MaximumScale = 16;

    public BrowserViewportState(
        double widthCss,
        double heightCss,
        double deviceScaleFactor)
    {
        if (!double.IsFinite(widthCss)
            || !double.IsFinite(heightCss)
            || !double.IsFinite(deviceScaleFactor)
            || widthCss < 0
            || heightCss < 0
            || widthCss > MaximumCssExtent
            || heightCss > MaximumCssExtent
            || deviceScaleFactor < MinimumScale
            || deviceScaleFactor > MaximumScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthCss),
                "Browser viewport values must be finite and bounded.");
        }

        WidthCss = widthCss;
        HeightCss = heightCss;
        DeviceScaleFactor = deviceScaleFactor;
    }

    public double WidthCss { get; }

    public double HeightCss { get; }

    public double DeviceScaleFactor { get; }

    public bool Contains(double xCss, double yCss) =>
        double.IsFinite(xCss)
        && double.IsFinite(yCss)
        && xCss >= 0
        && yCss >= 0
        && xCss < WidthCss
        && yCss < HeightCss;

    public static BrowserViewportState Empty { get; } = new(0, 0, 1);
}

/// <summary>
/// Freshness evidence for one physical browser input or script dispatch.
/// It is comparison material, never reusable authority.
/// </summary>
public sealed record BrowserAutomationBinding
{
    public BrowserAutomationBinding(
        BrowserDocumentBinding document,
        BrowserViewportState viewport,
        long viewportRevision,
        long inputEpoch)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        ArgumentOutOfRangeException.ThrowIfNegative(viewportRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(inputEpoch);
        ViewportRevision = viewportRevision;
        InputEpoch = inputEpoch;
    }

    public BrowserDocumentBinding Document { get; }

    public BrowserViewportState Viewport { get; }

    public long ViewportRevision { get; }

    public long InputEpoch { get; }

    public bool Matches(BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Document.Matches(state)
            && Viewport == state.Viewport
            && ViewportRevision == state.ViewportRevision
            && InputEpoch == state.InputEpoch;
    }

    public static BrowserAutomationBinding FromState(BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BrowserAutomationBinding(
            BrowserDocumentBinding.FromState(state),
            state.Viewport,
            state.ViewportRevision,
            state.InputEpoch);
    }
}

[Flags]
public enum BrowserInputModifiers
{
    None = 0,
    Alt = 1 << 0,
    Control = 1 << 1,
    Meta = 1 << 2,
    Shift = 1 << 3,
}

[Flags]
public enum BrowserMouseButtons
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
    Back = 1 << 3,
    Forward = 1 << 4,
}

public enum BrowserMouseAction
{
    Move,
    Click,
    Wheel,
}

public enum BrowserMouseButton
{
    None,
    Left,
    Right,
    Middle,
    Back,
    Forward,
}

public sealed record BrowserMouseRequest
{
    public const double MaximumWheelDelta = 10_000;

    public BrowserMouseRequest(
        SessionId sessionId,
        BrowserAutomationBinding binding,
        BrowserMouseAction action,
        double xCss,
        double yCss,
        BrowserMouseButton button = BrowserMouseButton.None,
        BrowserMouseButtons buttons = BrowserMouseButtons.None,
        BrowserInputModifiers modifiers = BrowserInputModifiers.None,
        int clickCount = 0,
        double deltaX = 0,
        double deltaY = 0)
    {
        ValidateSession(sessionId);
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (!Enum.IsDefined(action)
            || !Enum.IsDefined(button)
            || !AreDefined(buttons)
            || !AreDefined(modifiers))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        if (!binding.Viewport.Contains(xCss, yCss))
        {
            throw new ArgumentOutOfRangeException(
                nameof(xCss),
                "Mouse coordinates must be inside the bound CSS viewport.");
        }

        if (!double.IsFinite(deltaX)
            || !double.IsFinite(deltaY)
            || Math.Abs(deltaX) > MaximumWheelDelta
            || Math.Abs(deltaY) > MaximumWheelDelta)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        }

        var isButtonAction = action is BrowserMouseAction.Click;
        if (isButtonAction != (button is not BrowserMouseButton.None)
            || (isButtonAction && clickCount is < 1 or > 3)
            || (!isButtonAction && clickCount != 0)
            || (action == BrowserMouseAction.Wheel
                && deltaX == 0
                && deltaY == 0)
            || (action != BrowserMouseAction.Wheel
                && (deltaX != 0 || deltaY != 0)))
        {
            throw new ArgumentException(
                "Mouse button, click-count, and delta fields do not match the action.");
        }

        SessionId = sessionId;
        Action = action;
        XCss = xCss;
        YCss = yCss;
        Button = button;
        Buttons = buttons;
        Modifiers = modifiers;
        ClickCount = clickCount;
        DeltaX = deltaX;
        DeltaY = deltaY;
    }

    public SessionId SessionId { get; }
    public BrowserAutomationBinding Binding { get; }
    public BrowserMouseAction Action { get; }
    public double XCss { get; }
    public double YCss { get; }
    public BrowserMouseButton Button { get; }
    public BrowserMouseButtons Buttons { get; }
    public BrowserInputModifiers Modifiers { get; }
    public int ClickCount { get; }
    public double DeltaX { get; }
    public double DeltaY { get; }

    private static bool AreDefined(BrowserMouseButtons value) =>
        (value & ~(BrowserMouseButtons.Left
            | BrowserMouseButtons.Right
            | BrowserMouseButtons.Middle
            | BrowserMouseButtons.Back
            | BrowserMouseButtons.Forward)) == BrowserMouseButtons.None;

    private static bool AreDefined(BrowserInputModifiers value) =>
        (value & ~(BrowserInputModifiers.Alt
            | BrowserInputModifiers.Control
            | BrowserInputModifiers.Meta
            | BrowserInputModifiers.Shift)) == BrowserInputModifiers.None;

    private static void ValidateSession(SessionId sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value))
        {
            throw new ArgumentException("A browser session ID is required.", nameof(sessionId));
        }
    }
}

public enum BrowserKeyAction
{
    Press,
}

/// <summary>A deliberately closed, normalized keyboard vocabulary.</summary>
public enum BrowserKey
{
    Backspace, Tab, Enter, Escape, Space,
    ArrowLeft, ArrowUp, ArrowRight, ArrowDown,
    Insert, Delete, Home, End, PageUp, PageDown,
    Alt, Control, Meta, Shift,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit0, Digit1, Digit2, Digit3, Digit4,
    Digit5, Digit6, Digit7, Digit8, Digit9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Minus, Equal, BracketLeft, BracketRight, Backslash,
    Semicolon, Quote, Backquote, Comma, Period, Slash,
}

public sealed record BrowserKeyRequest
{
    public BrowserKeyRequest(
        SessionId sessionId,
        BrowserAutomationBinding binding,
        BrowserKeyAction action,
        BrowserKey key,
        BrowserInputModifiers modifiers = BrowserInputModifiers.None)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value))
        {
            throw new ArgumentException("A browser session ID is required.", nameof(sessionId));
        }

        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (!Enum.IsDefined(action)
            || !Enum.IsDefined(key)
            || (modifiers & ~(BrowserInputModifiers.Alt
                | BrowserInputModifiers.Control
                | BrowserInputModifiers.Meta
                | BrowserInputModifiers.Shift)) != BrowserInputModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        SessionId = sessionId;
        Action = action;
        Key = key;
        Modifiers = modifiers;
    }

    public SessionId SessionId { get; }
    public BrowserAutomationBinding Binding { get; }
    public BrowserKeyAction Action { get; }
    public BrowserKey Key { get; }
    public BrowserInputModifiers Modifiers { get; }
}

public sealed record BrowserScrollRequest
{
    public const double MaximumDelta = 100_000;

    public BrowserScrollRequest(
        SessionId sessionId,
        BrowserAutomationBinding binding,
        double originXCss,
        double originYCss,
        double deltaX,
        double deltaY,
        BrowserInputModifiers modifiers = BrowserInputModifiers.None)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value))
        {
            throw new ArgumentException("A browser session ID is required.", nameof(sessionId));
        }

        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (!binding.Viewport.Contains(originXCss, originYCss)
            || !double.IsFinite(deltaX)
            || !double.IsFinite(deltaY)
            || (deltaX == 0 && deltaY == 0)
            || Math.Abs(deltaX) > MaximumDelta
            || Math.Abs(deltaY) > MaximumDelta
            || (modifiers & ~(BrowserInputModifiers.Alt
                | BrowserInputModifiers.Control
                | BrowserInputModifiers.Meta
                | BrowserInputModifiers.Shift)) != BrowserInputModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        }

        SessionId = sessionId;
        OriginXCss = originXCss;
        OriginYCss = originYCss;
        DeltaX = deltaX;
        DeltaY = deltaY;
        Modifiers = modifiers;
    }

    public SessionId SessionId { get; }
    public BrowserAutomationBinding Binding { get; }
    public double OriginXCss { get; }
    public double OriginYCss { get; }
    public double DeltaX { get; }
    public double DeltaY { get; }
    public BrowserInputModifiers Modifiers { get; }
}

public enum BrowserEvaluationWorld
{
    Isolated,
    Main,
}

public sealed record BrowserEvaluateRequest
{
    public const int MaximumSourceBytes = 32 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] ForbiddenSourceFragments =
    [
        "document.cookie", "cookiestore", "localstorage", "sessionstorage",
        "indexeddb", "authorization", "set-cookie", "proxy-authorization",
        "password", "passwd", "credential", "apikey", "api_key",
        "token", "secret", "private_key", "privatekey",
        "['cookie']", "[\"cookie\"]", "['authorization']", "[\"authorization\"]",
        "['localstorage']", "[\"localstorage\"]", "['sessionstorage']",
        "[\"sessionstorage\"]",
        "settimeout", "setinterval", "requestanimationframe", "queuemicrotask",
        "addEventListener", "addeventlistener", "new worker", "sharedworker",
        "serviceworker", "postmessage", "broadcastchannel",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public BrowserEvaluateRequest(
        SessionId sessionId,
        BrowserAutomationBinding binding,
        string source,
        BrowserEvaluationWorld world = BrowserEvaluationWorld.Isolated,
        bool awaitPromise = true,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value))
        {
            throw new ArgumentException("A browser session ID is required.", nameof(sessionId));
        }

        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!Enum.IsDefined(world)
            || source.Contains('\0', StringComparison.Ordinal)
            || !IsStrictUtf8Within(source, MaximumSourceBytes))
        {
            throw new ArgumentException("The browser script source is invalid.", nameof(source));
        }

        // Defense in depth only. Authority remains the typed runner, policy,
        // isolated world, frozen-origin guard, and result validator.
        var normalized = source.ToLowerInvariant();
        if (ForbiddenSourceFragments.Any(fragment =>
                normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The browser script may not access credentials, cookies, authentication headers, or persistent storage.",
                nameof(source));
        }

        var boundedTimeout = timeout ?? DefaultTimeout;
        if (boundedTimeout <= TimeSpan.Zero || boundedTimeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        SessionId = sessionId;
        Source = source;
        World = world;
        AwaitPromise = awaitPromise;
        Timeout = boundedTimeout;
    }

    public SessionId SessionId { get; }
    public BrowserAutomationBinding Binding { get; }
    public string Source { get; }
    public BrowserEvaluationWorld World { get; }
    public bool AwaitPromise { get; }
    public TimeSpan Timeout { get; }

    private static bool IsStrictUtf8Within(string value, int maximumBytes)
    {
        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

public sealed record BrowserAutomationReceipt(
    BrowserAutomationBinding SourceBinding,
    BrowserSessionState FreshState);

public sealed record BrowserEvaluationResult
{
    // Leaves room for the trusted browser state and tool-result envelope under
    // the agent kernel's 64 KiB result limit.
    public const int MaximumJsonBytes = 48 * 1024;

    private static readonly string[] SecretPropertyFragments =
    [
        "cookie", "authorization", "password", "passwd", "credential",
        "secret", "token", "apikey", "api_key", "localstorage",
        "sessionstorage",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public BrowserEvaluationResult(
        BrowserAutomationBinding sourceBinding,
        BrowserSessionState freshState,
        string json)
    {
        SourceBinding = sourceBinding
            ?? throw new ArgumentNullException(nameof(sourceBinding));
        FreshState = freshState ?? throw new ArgumentNullException(nameof(freshState));
        ArgumentNullException.ThrowIfNull(json);
        if (!IsStrictUtf8Within(json, MaximumJsonBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(json));
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 64,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        RejectSecrets(document.RootElement);
        Json = json;
    }

    public BrowserAutomationBinding SourceBinding { get; }
    public BrowserSessionState FreshState { get; }
    public string Json { get; }

    private static void RejectSecrets(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name.ToLowerInvariant();
                    if (SecretPropertyFragments.Any(fragment =>
                            name.Contains(fragment, StringComparison.Ordinal)))
                    {
                        throw new ArgumentException(
                            "The browser script result contains secret-bearing fields.",
                            nameof(element));
                    }

                    RejectSecrets(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RejectSecrets(item);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (value.Contains("authorization:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("set-cookie:", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("proxy-authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The browser script result contains secret-bearing header data.",
                        nameof(element));
                }

                break;
        }
    }

    private static bool IsStrictUtf8Within(string value, int maximumBytes)
    {
        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
