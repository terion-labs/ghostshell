using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Asura.Browser.Tests;

public sealed class BrowserLowLevelAutomationTests
{
    [Fact]
    public async Task GovernedNativeAutomationFailsBeforeDispatchWithoutPeerBinding()
    {
        var native = new RecordingEmbeddedBrowserView
        {
            SupportsPeerBoundTransport = false,
        };
        var surface = Surface(native);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        var origin = BrowserNavigationOrigin.FromAddress(surface.State.Address);

        var mouse = await surface.DispatchMouseWithinOriginAsync(
            new BrowserMouseRequest(
                new SessionId("browser"),
                binding,
                BrowserMouseAction.Move,
                20,
                30),
            origin,
            CancellationToken.None);
        var key = await surface.DispatchKeyWithinOriginAsync(
            new BrowserKeyRequest(
                new SessionId("browser"),
                binding,
                BrowserKeyAction.Press,
                BrowserKey.Enter),
            origin,
            CancellationToken.None);
        var scroll = await surface.ScrollWithinOriginAsync(
            new BrowserScrollRequest(
                new SessionId("browser"),
                binding,
                20,
                30,
                0,
                100),
            origin,
            CancellationToken.None);
        var evaluate = await surface.EvaluateWithinOriginAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"),
                binding,
                "document.title"),
            origin,
            CancellationToken.None);

        Assert.All(
            [mouse.Error, key.Error, scroll.Error, evaluate.Error],
            error => Assert.Equal(BrowserErrorCode.NavigationPolicyDenied, error?.Code));
        Assert.Null(native.LastMouseRequest);
        Assert.Null(native.LastKeyRequest);
        Assert.Null(native.LastScrollRequest);
        Assert.Null(native.LastEvaluateRequest);
    }

    [Fact]
    public async Task AcknowledgedClickAdvancesInputEpochAndReturnsFreshState()
    {
        var native = new RecordingEmbeddedBrowserView();
        var surface = Surface(native);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        var request = new BrowserMouseRequest(
            new SessionId("browser"), binding, BrowserMouseAction.Click,
            20, 30, BrowserMouseButton.Left, clickCount: 1);

        var result = await surface.DispatchMouseWithinOriginAsync(
            request,
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(request, native.LastMouseRequest);
        Assert.Equal(binding.InputEpoch + 1, result.Value!.FreshState.InputEpoch);
        Assert.Equal(result.Value.FreshState, surface.State);
    }

    [Fact]
    public async Task NativeViewportChangeFailsBeforeInputDispatch()
    {
        var native = new RecordingEmbeddedBrowserView();
        var surface = Surface(native);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        native.Viewport = new NativeBrowserViewport(700, 600);

        var result = await surface.DispatchMouseWithinOriginAsync(
            new BrowserMouseRequest(
                new SessionId("browser"), binding, BrowserMouseAction.Move, 20, 30),
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.NavigationStateChanged, result.Error?.Code);
        Assert.Null(native.LastMouseRequest);
        Assert.Equal(700, surface.State.Viewport.WidthCss);
        Assert.Equal(binding.ViewportRevision + 1, surface.State.ViewportRevision);
    }

    [Fact]
    public async Task CancellationAfterDispatchQuarantinesAndDoesNotReportCancelled()
    {
        var pending = new TaskCompletionSource<NativeBrowserAutomationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new RecordingEmbeddedBrowserView { PendingAutomation = pending };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = Surface(native, replacement);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        using var cancellation = new CancellationTokenSource();

        var action = surface.DispatchKeyWithinOriginAsync(
            new BrowserKeyRequest(
                new SessionId("browser"), binding,
                BrowserKeyAction.Press, BrowserKey.Enter),
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            cancellation.Token).AsTask();
        await WaitUntilAsync(() => native.LastKeyRequest is not null);
        cancellation.Cancel();
        var result = await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.InteractionOutcomeUnknown, result.Error?.Code);
        Assert.True(native.IsDisposed);
        Assert.NotEqual(binding.Document.DocumentRevision, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task ScriptNavigationOutsideFrozenOriginIsDeniedAndQuarantined()
    {
        var pending = new TaskCompletionSource<NativeBrowserAutomationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new RecordingEmbeddedBrowserView { PendingAutomation = pending };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = Surface(native, replacement);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        var origin = BrowserNavigationOrigin.FromAddress(surface.State.Address);

        var action = surface.EvaluateWithinOriginAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), binding, "location.href"),
            origin,
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => native.LastEvaluateRequest is not null);
        Assert.True(native.RaiseNavigationStarted(Address("https://other.test/")));
        pending.SetResult(NativeBrowserAutomationResult.Acknowledged("null"));
        var result = await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.NavigationPolicyDenied, result.Error?.Code);
        Assert.True(native.IsDisposed);
    }

    [Fact]
    public async Task EvaluationRejectsSecretBearingResultAfterAcknowledgement()
    {
        var native = new RecordingEmbeddedBrowserView
        {
            EvaluationResult = NativeBrowserAutomationResult.Acknowledged(
                "{\"password\":\"page-controlled\"}"),
        };
        var surface = Surface(native);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);

        var result = await surface.EvaluateWithinOriginAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), binding, "({value: 1})"),
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.ScriptResultRejected, result.Error?.Code);
    }

    [Fact]
    public async Task CdpClickMovesHumanelyThenDispatchesPressAndRelease()
    {
        var transport = new RecordingTransport();
        var moved = new List<CefCursorPoint>();
        var humanizedInput = HumanizedInput(transport, moved.Add);
        var adapter = new CefBrowserAutomationAdapter(
            transport,
            humanizedInput);

        var result = await adapter.DispatchMouseAsync(
            new BrowserMouseRequest(
                new SessionId("browser"), Binding(), BrowserMouseAction.Click,
                20, 30, BrowserMouseButton.Left, clickCount: 1));

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        Assert.Equal("Page.getLayoutMetrics", transport.Calls[0].Method);
        var mouseCalls = transport.Calls
            .Where(call => string.Equals(call.Method, "Input.dispatchMouseEvent", StringComparison.Ordinal))
            .ToArray();
        Assert.True(mouseCalls.Length >= 6);
        Assert.All(
            mouseCalls,
            call => Assert.Equal("Input.dispatchMouseEvent", call.Method));
        Assert.All(
            mouseCalls[..^2],
            call => Assert.Equal("mouseMoved", EventType(call)));
        Assert.Equal("mousePressed", EventType(mouseCalls[^2]));
        Assert.Equal("mouseReleased", EventType(mouseCalls[^1]));
        Assert.Equal(new CefCursorPoint(20, 30), moved[^1]);
        Assert.True(moved.Select(point => point.X).Distinct().Count() > 2);
        Assert.True(moved.Select(point => point.Y).Distinct().Count() > 2);
    }

    [Fact]
    public async Task CursorMovementTimeBudgetScalesWithDistance()
    {
        var shortDelays = new List<TimeSpan>();
        var shortInput = new CefHumanizedInput(
            new RecordingTransport(),
            new Random(23),
            duration =>
            {
                shortDelays.Add(duration);
                return Task.CompletedTask;
            });
        var longDelays = new List<TimeSpan>();
        var longInput = new CefHumanizedInput(
            new RecordingTransport(),
            new Random(23),
            duration =>
            {
                longDelays.Add(duration);
                return Task.CompletedTask;
            });

        await shortInput.MoveAsync(0, 0);
        await longInput.MoveAsync(0, 0);
        shortDelays.Clear();
        longDelays.Clear();
        await shortInput.MoveAsync(30, 40);
        await longInput.MoveAsync(1_000, 0);

        var shortDuration = shortDelays.Aggregate(
            TimeSpan.Zero,
            (sum, next) => sum + next);
        var longDuration = longDelays.Aggregate(
            TimeSpan.Zero,
            (sum, next) => sum + next);
        Assert.InRange(shortDuration.TotalMilliseconds, 32, 38);
        Assert.InRange(longDuration.TotalMilliseconds, 174, 180);
        Assert.True(longDuration > shortDuration * 3);
    }

    [Fact]
    public async Task FirstAgentGestureSeedsCursorInsideViewport()
    {
        var transport = new RecordingTransport();
        var positions = new List<CefCursorPoint>();
        var input = HumanizedInput(transport, positions.Add);

        await input.MoveAsync(700, 500);

        Assert.Equal("Page.getLayoutMetrics", transport.Calls[0].Method);
        Assert.InRange(positions[0].X, 32, 768);
        Assert.InRange(positions[0].Y, 32, 568);
        Assert.NotEqual(new CefCursorPoint(0, 0), positions[0]);
        Assert.Equal(new CefCursorPoint(700, 500), positions[^1]);
    }

    [Fact]
    public void AgentCursorUsesFilledAccentFluentIconWithShadow()
    {
        var overlay = new CefAgentCursorOverlay();
        var workspace = new Border { Child = overlay };
        var workspaceAccent = Color.Parse("#FF35B779");
        workspace.Resources["ShellAccentBrush"] = new SolidColorBrush(workspaceAccent);
        overlay.ShowAt(new CefCursorPoint(40, 60));

        var cursor = Assert.IsType<FluentIcon>(Assert.Single(overlay.Children));
        Assert.Equal(Icon.Cursor, cursor.Icon);
        Assert.Equal(IconVariant.Filled, cursor.IconVariant);
        Assert.Equal(34, cursor.Width);
        Assert.Equal(34, cursor.Height);
        Assert.Equal(31, cursor.FontSize);
        var foreground = Assert.IsType<SolidColorBrush>(cursor.Foreground);
        Assert.Equal(workspaceAccent, foreground.Color);
        var shadow = Assert.IsType<DropShadowEffect>(cursor.Effect);
        Assert.Equal(Colors.Black, shadow.Color);
        Assert.Equal(8, shadow.BlurRadius);
        Assert.Equal(1, shadow.OffsetX);
        Assert.Equal(2, shadow.OffsetY);
        Assert.Equal(0.55, shadow.Opacity);

        var changedAccent = Color.Parse("#FF4B8FE8");
        workspace.Resources["ShellAccentBrush"] = new SolidColorBrush(changedAccent);

        foreground = Assert.IsType<SolidColorBrush>(cursor.Foreground);
        Assert.Equal(changedAccent, foreground.Color);
        Assert.Equal(Colors.Black, shadow.Color);
    }

    [Fact]
    public async Task WheelGestureMovesCursorAndUsesUnevenExactDeltas()
    {
        var transport = new RecordingTransport();
        var adapter = new CefBrowserAutomationAdapter(
            transport,
            HumanizedInput(transport));

        var result = await adapter.DispatchScrollAsync(
            new BrowserScrollRequest(
                new SessionId("browser"),
                Binding(),
                400,
                300,
                35,
                420));

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        var wheelCalls = transport.Calls
            .Where(call => string.Equals(call.Method, "Input.dispatchMouseEvent", StringComparison.Ordinal))
            .Where(call => string.Equals(EventType(call), "mouseWheel", StringComparison.Ordinal))
            .ToArray();
        Assert.InRange(wheelCalls.Length, 3, 18);
        Assert.Contains(
            transport.Calls.Where(
                call => string.Equals(call.Method, "Input.dispatchMouseEvent", StringComparison.Ordinal)),
            call => string.Equals(EventType(call), "mouseMoved", StringComparison.Ordinal));
        Assert.Equal(
            35,
            wheelCalls.Sum(call => EventDelta(call, "deltaX")),
            precision: 8);
        Assert.Equal(
            420,
            wheelCalls.Sum(call => EventDelta(call, "deltaY")),
            precision: 8);
        Assert.True(
            wheelCalls
                .Select(call => Math.Round(EventDelta(call, "deltaY"), 4))
                .Distinct()
                .Count() > 1);
    }

    [Fact]
    public async Task TypingUsesBoundedUnevenBurstsWithoutChangingText()
    {
        var transport = new RecordingTransport();
        var delays = new List<TimeSpan>();
        var cursorActivity = new List<CefCursorPoint>();
        var input = new CefHumanizedInput(
            transport,
            new Random(19),
            duration =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            },
            cursorActivity.Add);
        const string text = "Hello, world! This is typed humanely.";

        await input.TypeTextAsync(text);

        var insertCalls = transport.Calls
            .Where(call => string.Equals(call.Method, "Input.insertText", StringComparison.Ordinal))
            .ToArray();
        Assert.InRange(insertCalls.Length, 2, 96);
        Assert.All(
            insertCalls,
            call => Assert.Equal("Input.insertText", call.Method));
        Assert.Equal(
            text,
            string.Concat(insertCalls.Select(InsertedText)));
        Assert.Equal(insertCalls.Length - 1, delays.Count);
        Assert.True(delays.Distinct().Count() > 1);
        Assert.Equal(insertCalls.Length + 1, cursorActivity.Count);
        Assert.All(
            cursorActivity,
            point => Assert.Equal(cursorActivity[0], point));
    }

    [Fact]
    public async Task IsolatedEvaluationCreatesPrivateWorldAndForbidsSideEffects()
    {
        var transport = new RecordingTransport(
            "{\"result\":{\"frameTree\":{\"frame\":{\"id\":\"main\"}}}}",
            "{\"result\":{\"executionContextId\":42}}",
            "{\"result\":{\"result\":{\"type\":\"number\",\"value\":2}}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.EvaluateAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), Binding(), "1 + 1"));

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        Assert.Equal("2", result.ResultJson);
        Assert.Equal(
            ["Page.getFrameTree", "Page.createIsolatedWorld", "Runtime.evaluate"],
            transport.Calls.Select(call => call.Method), StringComparer.Ordinal);
        using var parameters = JsonDocument.Parse(transport.Calls[2].Parameters!);
        Assert.True(parameters.RootElement.GetProperty("throwOnSideEffect").GetBoolean());
        Assert.Equal(42, parameters.RootElement.GetProperty("contextId").GetInt32());
        Assert.True(parameters.RootElement.GetProperty("returnByValue").GetBoolean());
    }

    [Fact]
    public async Task WebSearchExtractionUsesFixedCodeInAPrivateWorld()
    {
        var transport = new RecordingTransport(
            "{\"result\":{\"frameTree\":{\"frame\":{\"id\":\"main\"}}}}",
            "{\"result\":{\"executionContextId\":42}}",
            "{\"result\":{\"result\":{\"type\":\"object\",\"value\":{\"title\":\"Search\",\"pageText\":\"result\",\"results\":[],\"truncated\":false}}}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.ExtractWebSearchDocumentAsync(4);

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        Assert.Equal(
            ["Page.getFrameTree", "Page.createIsolatedWorld", "Runtime.evaluate"],
            transport.Calls.Select(call => call.Method), StringComparer.Ordinal);
        using var parameters = JsonDocument.Parse(transport.Calls[2].Parameters!);
        var root = parameters.RootElement;
        Assert.False(root.GetProperty("throwOnSideEffect").GetBoolean());
        Assert.False(root.GetProperty("awaitPromise").GetBoolean());
        Assert.Equal(42, root.GetProperty("contextId").GetInt32());
        Assert.True(root.GetProperty("returnByValue").GetBoolean());
        var source = root.GetProperty("expression").GetString()!;
        Assert.Contains(
            "const resultCandidates = () =>",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MutationObserver", source, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "resultCandidates().length >=",
            source,
            StringComparison.Ordinal);
        Assert.Contains("document.querySelector('#rso')", source, StringComparison.Ordinal);
        Assert.Contains("resultRoot.querySelectorAll('h3')", source, StringComparison.Ordinal);
        Assert.Contains("heading.closest('a[href]')", source, StringComparison.Ordinal);
        Assert.Contains("compact(heading.textContent)", source, StringComparison.Ordinal);
        Assert.Contains("title: title.slice(0, 1024)", source, StringComparison.Ordinal);
        Assert.Contains(
            "heading.closest('[jscontroller][lang]')",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "heading.closest('[lang]')",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("anchor.parentElement", source, StringComparison.Ordinal);
        Assert.Contains("results.length >= 4", source, StringComparison.Ordinal);
        Assert.Contains("clone.querySelectorAll('[aria-hidden]')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("querySelectorAll('a[href]')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadableArticleExtractionRunsBeforeBoundedTransport()
    {
        const int chunkSize = 32 * 1024;
        var firstChunk = new string('a', chunkSize);
        const string secondChunk = "b";
        var transport = new RecordingTransport(
            "{\"result\":{\"frameTree\":{\"frame\":{\"id\":\"main\"}}}}",
            "{\"result\":{\"executionContextId\":42}}",
            EvaluationReply(new
            {
                title = "Guide",
                length = chunkSize + 1,
                links = new[]
                {
                    "https://docs.example.test/guide",
                    "https://docs.example.test/reference",
                },
                linksTruncated = false,
            }),
            EvaluationReply(new { chunk = firstChunk, nextOffset = chunkSize }),
            EvaluationReply(new { chunk = secondChunk, nextOffset = chunkSize + 1 }));
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.ExtractReadableArticleAsync();

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        using var document = JsonDocument.Parse(result.ResultJson!);
        Assert.Equal("Guide", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            firstChunk + secondChunk,
            document.RootElement.GetProperty("html").GetString());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            [
                "https://docs.example.test/guide",
                "https://docs.example.test/reference",
            ],
            document.RootElement.GetProperty("links")
                .EnumerateArray()
                .Select(link => link.GetString()),
            StringComparer.Ordinal);
        Assert.Equal(
            [
                "Page.getFrameTree",
                "Page.createIsolatedWorld",
                "Runtime.evaluate",
                "Runtime.evaluate",
                "Runtime.evaluate",
            ],
            transport.Calls.Select(call => call.Method),
            StringComparer.Ordinal);
        var expressions = transport.Calls
            .Skip(2)
            .Select(call => JsonDocument.Parse(call.Parameters!).RootElement
                .GetProperty("expression")
                .GetString()!)
            .ToArray();
        Assert.Contains(
            "document.cloneNode(true)",
            expressions[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "document.querySelectorAll('a[href]')",
            expressions[0],
            StringComparison.Ordinal);
        Assert.Contains("anchor.href", expressions[0], StringComparison.Ordinal);
        Assert.Contains("seenLinks.has(url)", expressions[0], StringComparison.Ordinal);
        Assert.True(
            expressions[0].IndexOf(
                "document.querySelectorAll('a[href]')",
                StringComparison.Ordinal)
            < expressions[0].IndexOf(
                "document.cloneNode(true)",
                StringComparison.Ordinal));
        Assert.Contains(
            "globalThis.__asuraReadableArticleHtml = article.content",
            expressions[0],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "document.documentElement.outerHTML",
            expressions[0],
            StringComparison.Ordinal);
        Assert.Contains("html.slice(0, end)", expressions[1], StringComparison.Ordinal);
        Assert.Contains(
            $"html.slice({chunkSize}, end)",
            expressions[2],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainWorldEvaluationOmitsContextIdInsteadOfSendingNull()
    {
        var transport = new RecordingTransport(
            "{\"result\":{\"result\":{\"type\":\"number\",\"value\":1}}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.EvaluateAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), Binding(), "1",
                BrowserEvaluationWorld.Main));

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        using var parameters = JsonDocument.Parse(transport.Calls[0].Parameters!);
        Assert.False(parameters.RootElement.TryGetProperty("contextId", out _));
    }

    [Fact]
    public async Task OversizedCdpReplyFailsClosedAfterDispatch()
    {
        var transport = new RecordingTransport(
            "{\"result\":{\"cssVisualViewport\":{\"clientWidth\":800,\"clientHeight\":600}}}",
            "{\"result\":{}}",
            "{\"result\":{\"padding\":\"" + new string('x', 300_000) + "\"}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.DispatchKeyAsync(
            new BrowserKeyRequest(
                new SessionId("browser"), Binding(),
                BrowserKeyAction.Press, BrowserKey.Enter));

        Assert.Equal(NativeBrowserAutomationStatus.OutcomeUnknown, result.Status);
    }

    private static BrowserSurface Surface(
        RecordingEmbeddedBrowserView native,
        RecordingEmbeddedBrowserView? replacement = null) =>
        replacement is null
            ? new BrowserSurface(
                native,
                BrowserTestDestinationPolicy.Public,
                InlineBrowserUiDispatcher.Instance,
                capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate)
            : new BrowserSurface(
                native,
                BrowserTestDestinationPolicy.Public,
                InlineBrowserUiDispatcher.Instance,
                () => replacement,
                static _ => { },
                capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);

    private static void Arrange(BrowserSurface surface)
    {
        surface.Measure(new Size(800, 600));
        surface.Arrange(new Rect(0, 0, 800, 600));
        Assert.Equal(new BrowserViewportState(800, 600, 1), surface.State.Viewport);
    }

    private static BrowserAutomationBinding Binding() =>
        new(
            new BrowserDocumentBinding(BrowserAddress.Blank, 0),
            new BrowserViewportState(800, 600, 1),
            1,
            0);

    private static BrowserAddress Address(string value)
    {
        Assert.True(BrowserAddress.TryParse(value, out var address));
        return address;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }

    private static CefHumanizedInput HumanizedInput(
        RecordingTransport transport,
        Action<CefCursorPoint>? cursorActivity = null) =>
        new(
            transport,
            new Random(17),
            static _ => Task.CompletedTask,
            cursorActivity);

    private static string EventType((string Method, string? Parameters) call)
    {
        using var parameters = JsonDocument.Parse(call.Parameters!);
        return parameters.RootElement.GetProperty("type").GetString()!;
    }

    private static double EventDelta(
        (string Method, string? Parameters) call,
        string property)
    {
        using var parameters = JsonDocument.Parse(call.Parameters!);
        return parameters.RootElement.GetProperty(property).GetDouble();
    }

    private static string InsertedText(
        (string Method, string? Parameters) call)
    {
        using var parameters = JsonDocument.Parse(call.Parameters!);
        return parameters.RootElement.GetProperty("text").GetString()!;
    }

    private sealed class RecordingTransport(params string[] replies)
        : ICefDevToolsTransport
    {
        private readonly Queue<string> _replies = new(replies);

        public List<(string Method, string? Parameters)> Calls { get; } = [];

        public Task<string> ExecuteAsync(string method, string? parametersJson)
        {
            Calls.Add((method, parametersJson));
            return Task.FromResult(
                _replies.TryDequeue(out var reply)
                    ? reply
                    : string.Equals(method, "Page.getLayoutMetrics"
, StringComparison.Ordinal) ? "{\"result\":{\"cssVisualViewport\":{\"clientWidth\":800,\"clientHeight\":600}}}"
                    : "{\"result\":{}}");
        }
    }

    private static string EvaluationReply<T>(T value) =>
        JsonSerializer.Serialize(new
        {
            result = new
            {
                result = new
                {
                    type = "object",
                    value,
                },
            },
        });
}
