using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Asura.Application;

namespace Asura.Browser;

/// <summary>
/// Owns the one retained agent cursor for a CEF renderer and turns browser
/// input into bounded, human-readable gestures. Semantic and coordinate tools
/// share this instance so the visible cursor is the cursor that Chromium
/// actually receives.
/// </summary>
internal sealed class CefHumanizedInput : IDisposable
{
    private const int MaximumCurvePoints = 20;
    private const int MinimumCurvePoints = 3;
    private const int MaximumTypingBursts = 96;
    private const double MinimumMovementMilliseconds = 27;
    private const double MaximumMovementMilliseconds = 210;
    private const double MovementMillisecondsPerCssPixel = 0.15;
    private const double MovementFrameMilliseconds = 8;
    private const double OvershootTravelFraction = 0.72;
    private const double OvershootThresholdCss = 500;
    private const double OvershootRadiusCss = 120;
    private const double CorrectionSpreadCss = 10;
    private const int MaximumCdpReplyBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ICefDevToolsTransport _transport;
    private readonly Random _random;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Action<CefCursorPoint>? _cursorActivity;
    private readonly SemaphoreSlim _gestureGate = new(1, 1);

    public void Dispose() => _gestureGate.Dispose();
    private CefCursorPoint? _cursor;

    public CefHumanizedInput(
        ICefDevToolsTransport transport,
        Random? random = null,
        Func<TimeSpan, Task>? delay = null,
        Action<CefCursorPoint>? cursorActivity = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _random = random ?? Random.Shared;
        _delay = delay ?? (duration => Task.Delay(duration));
        _cursorActivity = cursorActivity;
    }

    public async Task<NativeBrowserViewport> ReadViewportAsync()
    {
        using var reply = await ExecuteAsync(
                "Page.getLayoutMetrics",
                parametersJson: null)
            .ConfigureAwait(false);
        var result = RequireResult(reply.RootElement);
        var viewport = result.TryGetProperty("cssVisualViewport", out var visual)
            ? visual
            : result.GetProperty("cssLayoutViewport");
        return new NativeBrowserViewport(
            viewport.GetProperty("clientWidth").GetDouble(),
            viewport.GetProperty("clientHeight").GetDouble()).Validate();
    }

    public async Task MoveAsync(
        double x,
        double y,
        BrowserMouseButtons buttons = BrowserMouseButtons.None,
        BrowserInputModifiers modifiers = BrowserInputModifiers.None,
        double targetWidth = 12)
    {
        ValidatePoint(x, y);
        await _gestureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await MoveCoreAsync(
                    new CefCursorPoint(x, y),
                    buttons,
                    modifiers,
                    targetWidth)
                .ConfigureAwait(false);
        }
        finally
        {
            _gestureGate.Release();
        }
    }

    public async Task ClickAsync(
        double x,
        double y,
        BrowserMouseButton button,
        BrowserMouseButtons buttons,
        BrowserInputModifiers modifiers,
        int clickCount,
        double targetWidth = 12)
    {
        ValidatePoint(x, y);
        await _gestureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await MoveCoreAsync(
                    new CefCursorPoint(x, y),
                    buttons,
                    modifiers,
                    targetWidth)
                .ConfigureAwait(false);
            await PauseAsync(35, 105).ConfigureAwait(false);
            await DispatchMouseEventAsync(
                    "mousePressed",
                    x,
                    y,
                    button,
                    buttons | ButtonFlag(button),
                    modifiers,
                    clickCount,
                    deltaX: 0,
                    deltaY: 0)
                .ConfigureAwait(false);
            await PauseAsync(45, 125).ConfigureAwait(false);
            await DispatchMouseEventAsync(
                    "mouseReleased",
                    x,
                    y,
                    button,
                    buttons & ~ButtonFlag(button),
                    modifiers,
                    clickCount,
                    deltaX: 0,
                    deltaY: 0)
                .ConfigureAwait(false);
            KeepCursorVisible();
        }
        finally
        {
            _gestureGate.Release();
        }
    }

    public async Task ScrollAsync(
        double originX,
        double originY,
        double deltaX,
        double deltaY,
        BrowserInputModifiers modifiers,
        BrowserMouseButtons buttons = BrowserMouseButtons.None)
    {
        ValidatePoint(originX, originY);
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaX),
                "Wheel deltas must be finite.");
        }

        await _gestureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await MoveCoreAsync(
                    new CefCursorPoint(originX, originY),
                    buttons,
                    modifiers,
                    targetWidth: 24)
                .ConfigureAwait(false);

            var magnitude = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            var steps = Math.Clamp(
                checked((int)Math.Ceiling(magnitude / 70)),
                3,
                18);
            var priorProgress = 0d;
            var sentX = 0d;
            var sentY = 0d;
            for (var index = 1; index <= steps; index++)
            {
                var progress = index == steps
                    ? 1
                    : UnevenSmoothProgress(index, steps, priorProgress);
                var stepX = index == steps
                    ? deltaX - sentX
                    : deltaX * (progress - priorProgress);
                var stepY = index == steps
                    ? deltaY - sentY
                    : deltaY * (progress - priorProgress);
                await DispatchMouseEventAsync(
                        "mouseWheel",
                        originX,
                        originY,
                        BrowserMouseButton.None,
                        buttons,
                        modifiers,
                        clickCount: 0,
                        stepX,
                        stepY)
                    .ConfigureAwait(false);
                KeepCursorVisible();
                sentX += stepX;
                sentY += stepY;
                priorProgress = progress;
                if (index != steps)
                {
                    await PauseAsync(14, 42).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gestureGate.Release();
        }
    }

    public async Task TypeTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        var elements = TextElements(text);
        await _gestureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureCursorAsync().ConfigureAwait(false);
            var burstsRemaining = MaximumTypingBursts;
            for (var index = 0; index < elements.Count; burstsRemaining--)
            {
                var elementsRemaining = elements.Count - index;
                var minimumBurstSize = Math.Max(
                    1,
                    checked((int)Math.Ceiling(
                        elementsRemaining / (double)burstsRemaining)));
                var burstSize = elements.Count <= MaximumTypingBursts
                    ? 1
                    : Math.Min(
                        elementsRemaining,
                        _random.Next(
                            minimumBurstSize,
                            checked(Math.Min(
                                elementsRemaining,
                                checked(minimumBurstSize * 2)) + 1)));
                var burst = string.Concat(elements.Skip(index).Take(burstSize));
                await ExecuteAcknowledgedAsync(
                        "Input.insertText",
                        JsonSerializer.Serialize(
                            new CdpInsertTextParameters(burst),
                            BrowserJsonContext.Default.CdpInsertTextParameters))
                    .ConfigureAwait(false);
                KeepCursorVisible();
                index += burstSize;

                if (index < elements.Count)
                {
                    var baseDelay = elements.Count switch
                    {
                        <= 40 => RandomMilliseconds(28, 82),
                        <= 180 => RandomMilliseconds(15, 48),
                        _ => RandomMilliseconds(4, 18),
                    };
                    if (EndsWithNaturalPause(burst))
                    {
                        baseDelay += RandomMilliseconds(35, 105);
                    }

                    await _delay(TimeSpan.FromMilliseconds(baseDelay))
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gestureGate.Release();
        }
    }

    public async Task EnsureCursorVisibleAsync()
    {
        await _gestureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureCursorAsync().ConfigureAwait(false);
            KeepCursorVisible();
        }
        finally
        {
            _gestureGate.Release();
        }
    }

    public void KeepCursorVisible()
    {
        if (_cursor is { } cursor)
        {
            _cursorActivity?.Invoke(cursor);
        }
    }

    private async Task MoveCoreAsync(
        CefCursorPoint target,
        BrowserMouseButtons buttons,
        BrowserInputModifiers modifiers,
        double targetWidth)
    {
        var start = await EnsureCursorAsync().ConfigureAwait(false);
        var distance = Distance(start, target);
        var duration = MovementDuration(distance, targetWidth);
        if (distance > OvershootThresholdCss)
        {
            var overshoot = Overshoot(target, OvershootRadiusCss);
            await FollowCurveAsync(
                    start,
                    overshoot,
                    buttons,
                    modifiers,
                    spreadOverride: null,
                    ScaleDuration(duration, OvershootTravelFraction))
                .ConfigureAwait(false);
            await FollowCurveAsync(
                    overshoot,
                    target,
                    buttons,
                    modifiers,
                    CorrectionSpreadCss,
                    ScaleDuration(duration, 1 - OvershootTravelFraction))
                .ConfigureAwait(false);
            return;
        }

        await FollowCurveAsync(
                start,
                target,
                buttons,
                modifiers,
                spreadOverride: null,
                duration)
            .ConfigureAwait(false);
    }

    private async Task<CefCursorPoint> EnsureCursorAsync()
    {
        if (_cursor is { } cursor)
        {
            return cursor;
        }

        var viewport = await ReadViewportAsync().ConfigureAwait(false);
        var initial = new CefCursorPoint(
            RandomViewportCoordinate(viewport.WidthCss),
            RandomViewportCoordinate(viewport.HeightCss));
        await DispatchMovedAsync(
                initial,
                BrowserMouseButtons.None,
                BrowserInputModifiers.None)
            .ConfigureAwait(false);
        return initial;
    }

    private double RandomViewportCoordinate(double extent)
    {
        var inset = Math.Min(32, extent * 0.2);
        var maximum = extent - inset;
        return maximum <= inset
            ? extent / 2
            : RandomDouble(inset, maximum);
    }

    private async Task FollowCurveAsync(
        CefCursorPoint start,
        CefCursorPoint finish,
        BrowserMouseButtons buttons,
        BrowserInputModifiers modifiers,
        double? spreadOverride,
        TimeSpan duration)
    {
        var distance = Distance(start, finish);
        if (distance < 0.5)
        {
            await DispatchMovedAsync(finish, buttons, modifiers)
                .ConfigureAwait(false);
            return;
        }

        var spread = spreadOverride ?? Math.Clamp(distance, 2, 200);
        var controls = CreateSameSideControls(start, finish, spread);
        var steps = Math.Clamp(
            checked((int)Math.Ceiling(
                duration.TotalMilliseconds / MovementFrameMilliseconds)) + 1,
            MinimumCurvePoints,
            MaximumCurvePoints);
        var frameDuration = TimeSpan.FromTicks(
            duration.Ticks / Math.Max(steps - 1, 1));

        for (var index = 1; index <= steps; index++)
        {
            var t = index / (double)steps;
            var point = CubicBezier(
                start,
                controls.First,
                controls.Second,
                finish,
                t);
            await DispatchMovedAsync(point, buttons, modifiers)
                .ConfigureAwait(false);
            if (index != steps)
            {
                await _delay(frameDuration).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan MovementDuration(
        double distance,
        double targetWidth)
    {
        var precisionFactor = Math.Clamp(
            Math.Sqrt(12 / Math.Max(targetWidth, 1)),
            0.75,
            1.25);
        var milliseconds = Math.Clamp(
            (MinimumMovementMilliseconds
             + distance * MovementMillisecondsPerCssPixel)
            * precisionFactor,
            MinimumMovementMilliseconds,
            MaximumMovementMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan ScaleDuration(TimeSpan duration, double factor) =>
        TimeSpan.FromTicks(checked((long)Math.Round(duration.Ticks * factor)));

    private async Task DispatchMovedAsync(
        CefCursorPoint point,
        BrowserMouseButtons buttons,
        BrowserInputModifiers modifiers)
    {
        await DispatchMouseEventAsync(
                "mouseMoved",
                point.X,
                point.Y,
                BrowserMouseButton.None,
                buttons,
                modifiers,
                clickCount: 0,
                deltaX: 0,
                deltaY: 0)
            .ConfigureAwait(false);
        _cursor = point;
        _cursorActivity?.Invoke(point);
    }

    private Task DispatchMouseEventAsync(
        string type,
        double x,
        double y,
        BrowserMouseButton button,
        BrowserMouseButtons buttons,
        BrowserInputModifiers modifiers,
        int clickCount,
        double deltaX,
        double deltaY) =>
        ExecuteAcknowledgedAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(
                new CdpMouseEventParameters(
                    type,
                    x,
                    y,
                    MouseButtonName(button),
                    (int)buttons,
                    (int)modifiers,
                    clickCount,
                    deltaX,
                    deltaY,
                    "mouse"),
                BrowserJsonContext.Default.CdpMouseEventParameters));

    private async Task ExecuteAcknowledgedAsync(
        string method,
        string parametersJson)
    {
        using var reply = await ExecuteAsync(method, parametersJson)
            .ConfigureAwait(false);
        _ = RequireResult(reply.RootElement);
    }

    private async Task<JsonDocument> ExecuteAsync(
        string method,
        string? parametersJson)
    {
        var reply = await _transport.ExecuteAsync(
                method,
                parametersJson)
            .ConfigureAwait(false);
        if (StrictUtf8.GetByteCount(reply) > MaximumCdpReplyBytes)
        {
            throw new InvalidOperationException(
                "CEF returned an oversized CDP reply.");
        }

        return JsonDocument.Parse(reply);
    }

    private static JsonElement RequireResult(JsonElement reply)
    {
        if (reply.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "CEF rejected an input command."
                    : "CEF rejected an input command.");
        }

        return reply.TryGetProperty("result", out var result)
            ? result
            : throw new InvalidOperationException(
                "CEF did not acknowledge an input command.");
    }

    private (CefCursorPoint First, CefCursorPoint Second)
        CreateSameSideControls(
            CefCursorPoint start,
            CefCursorPoint finish,
            double spread)
    {
        var dx = finish.X - start.X;
        var dy = finish.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var normalX = length == 0 ? 0 : dy / length;
        var normalY = length == 0 ? 0 : -dx / length;
        var side = _random.Next(0, 2) == 0 ? -1 : 1;
        var firstT = RandomDouble(0.2, 0.45);
        var secondT = RandomDouble(0.55, 0.8);
        var firstOffset = side * RandomDouble(0, spread);
        var secondOffset = side * RandomDouble(0, spread);
        return (
            new CefCursorPoint(
                Math.Max(0, start.X + dx * firstT + normalX * firstOffset),
                Math.Max(0, start.Y + dy * firstT + normalY * firstOffset)),
            new CefCursorPoint(
                Math.Max(0, start.X + dx * secondT + normalX * secondOffset),
                Math.Max(0, start.Y + dy * secondT + normalY * secondOffset)));
    }

    private CefCursorPoint Overshoot(CefCursorPoint target, double radius)
    {
        var angle = RandomDouble(0, Math.PI * 2);
        var magnitude = radius * Math.Sqrt(_random.NextDouble());
        return new CefCursorPoint(
            Math.Max(0, target.X + magnitude * Math.Cos(angle)),
            Math.Max(0, target.Y + magnitude * Math.Sin(angle)));
    }

    private double UnevenSmoothProgress(
        int index,
        int steps,
        double priorProgress)
    {
        var jitteredT = Math.Clamp(
            (index + RandomDouble(-0.22, 0.22)) / steps,
            0,
            1);
        var progress = jitteredT * jitteredT * (3 - 2 * jitteredT);
        return Math.Clamp(progress, priorProgress + 0.0001, 0.9999);
    }

    private Task PauseAsync(int minimumMilliseconds, int maximumMilliseconds) =>
        _delay(TimeSpan.FromMilliseconds(
            RandomMilliseconds(minimumMilliseconds, maximumMilliseconds)));

    private int RandomMilliseconds(int minimum, int maximum) =>
        _random.Next(minimum, checked(maximum + 1));

    private double RandomDouble(double minimum, double maximum) =>
        minimum + _random.NextDouble() * (maximum - minimum);

    private static CefCursorPoint CubicBezier(
        CefCursorPoint start,
        CefCursorPoint first,
        CefCursorPoint second,
        CefCursorPoint finish,
        double t)
    {
        var inverse = 1 - t;
        var startWeight = inverse * inverse * inverse;
        var firstWeight = 3 * inverse * inverse * t;
        var secondWeight = 3 * inverse * t * t;
        var finishWeight = t * t * t;
        return new CefCursorPoint(
            Math.Max(
                0,
                startWeight * start.X
                + firstWeight * first.X
                + secondWeight * second.X
                + finishWeight * finish.X),
            Math.Max(
                0,
                startWeight * start.Y
                + firstWeight * first.Y
                + secondWeight * second.Y
                + finishWeight * finish.Y));
    }

    private static IReadOnlyList<string> TextElements(string value)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return elements;
    }

    private static bool EndsWithNaturalPause(string value) =>
        value.Length != 0
        && value[^1] is '.' or ',' or ';' or ':' or '!' or '?' or '\n';

    private static double Distance(CefCursorPoint first, CefCursorPoint second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void ValidatePoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                "Browser coordinates must be finite and non-negative.");
        }
    }

    private static string MouseButtonName(BrowserMouseButton button) =>
        button switch
        {
            BrowserMouseButton.None => "none",
            BrowserMouseButton.Left => "left",
            BrowserMouseButton.Right => "right",
            BrowserMouseButton.Middle => "middle",
            BrowserMouseButton.Back => "back",
            BrowserMouseButton.Forward => "forward",
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };

    private static BrowserMouseButtons ButtonFlag(BrowserMouseButton button) =>
        button switch
        {
            BrowserMouseButton.None => BrowserMouseButtons.None,
            BrowserMouseButton.Left => BrowserMouseButtons.Left,
            BrowserMouseButton.Right => BrowserMouseButtons.Right,
            BrowserMouseButton.Middle => BrowserMouseButtons.Middle,
            BrowserMouseButton.Back => BrowserMouseButtons.Back,
            BrowserMouseButton.Forward => BrowserMouseButtons.Forward,
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct CefCursorPoint(double X, double Y);
