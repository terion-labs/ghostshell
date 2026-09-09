using System.Text.Json;
using Asura.Application;
using Exclr8Cef;
using Exclr8Cef.Cdp;

namespace Asura.Browser;

/// <summary>
/// Executes semantic browser operations through acknowledged CDP round trips.
/// No page-realm selector, script, or DOM event shortcut is used for input.
/// </summary>
internal sealed class CefSemanticBrowser : ICefSemanticBrowser
{
    private const int MaximumRevealAttempts = 6;
    private const double ViewportMarginCss = 12;
    private readonly CefBrowser _browser;
    private readonly CefHumanizedInput _humanizedInput;
    private bool _domainsEnabled;

    public CefSemanticBrowser(
        CefBrowser browser,
        CefHumanizedInput humanizedInput)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _humanizedInput = humanizedInput
            ?? throw new ArgumentNullException(nameof(humanizedInput));
    }

    public async Task<IReadOnlyList<CefSemanticNode>>
        ReadAccessibilityTreeAsync()
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var reply = await _browser.ExecuteDevToolsMethodAsync(
                "Accessibility.getFullAXTree",
                "{\"depth\":64}")
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(reply);
        var nodes = document.RootElement
            .GetProperty("result")
            .GetProperty("nodes");
        return [.. nodes.EnumerateArray().Select(Project)];
    }

    public async Task<CefSemanticNode?> ReadAccessibilityNodeAsync(
        int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var nodes = await _browser.Accessibility
            .GetPartialTreeAsync(backendNodeId, fetchRelatives: true)
            .ConfigureAwait(false);
        var node = nodes.FirstOrDefault(
            candidate => candidate.BackendDomNodeId == backendNodeId);
        return node is null ? null : Project(node);
    }

    public async Task<CefSemanticPoint?> PrepareClickPointAsync(
        int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        for (var attempt = 0; attempt < MaximumRevealAttempts; attempt++)
        {
            var quads = await _browser.Dom
                .GetContentQuadsAsync(backendNodeId)
                .ConfigureAwait(false);
            ScreenshotClip? usableClip = null;
            foreach (var quad in quads)
            {
                var candidate = quad.ToBoundingClip();
                if (IsUsableClip(candidate))
                {
                    usableClip = candidate;
                    break;
                }
            }

            if (usableClip is not { } clip)
            {
                return null;
            }

            var viewport = await _humanizedInput.ReadViewportAsync()
                .ConfigureAwait(false);
            var point = new CefSemanticPoint(
                clip.X + clip.Width / 2,
                clip.Y + clip.Height / 2,
                clip.Width,
                clip.Height);
            if (IsInsideViewport(point, viewport))
            {
                return point;
            }

            var deltaX = RevealDelta(
                point.X,
                viewport.WidthCss,
                clip.Width);
            var deltaY = RevealDelta(
                point.Y,
                viewport.HeightCss,
                clip.Height);
            await _humanizedInput.ScrollAsync(
                    viewport.WidthCss / 2,
                    viewport.HeightCss / 2,
                    deltaX,
                    deltaY,
                    BrowserInputModifiers.None)
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(35))
                .ConfigureAwait(false);
        }

        return null;
    }

    public async Task<bool> HitTestIncludesAsync(
        CefSemanticPoint point,
        int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var hitNodeId = await _browser.Dom
            .GetNodeForLocationAsync(
                checked((int)Math.Round(point.X)),
                checked((int)Math.Round(point.Y)))
            .ConfigureAwait(false);
        if (hitNodeId is null)
        {
            return false;
        }

        if (hitNodeId.Value == backendNodeId)
        {
            return true;
        }

        var relatives = await _browser.Accessibility
            .GetPartialTreeAsync(hitNodeId.Value, fetchRelatives: true)
            .ConfigureAwait(false);
        return relatives.Any(
            node => node.BackendDomNodeId == backendNodeId);
    }

    public async Task<bool> DispatchClickAsync(
        CefSemanticPoint point,
        int backendNodeId)
    {
        await _humanizedInput.MoveAsync(
                point.X,
                point.Y,
                targetWidth: point.TargetWidth)
            .ConfigureAwait(false);
        if (!await HitTestIncludesAsync(point, backendNodeId)
                .ConfigureAwait(false))
        {
            return false;
        }

        await _humanizedInput.ClickAsync(
                point.X,
                point.Y,
                BrowserMouseButton.Left,
                BrowserMouseButtons.None,
                BrowserInputModifiers.None,
                clickCount: 1,
                targetWidth: point.TargetWidth)
            .ConfigureAwait(false);
        return true;
    }

    public async Task ReplaceFocusedTextAsync(
        int backendNodeId,
        string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        await _humanizedInput.EnsureCursorVisibleAsync().ConfigureAwait(false);
        await _browser.Dom.FocusAsync(backendNodeId).ConfigureAwait(false);
        await DispatchKeyAsync(
                "rawKeyDown",
                "a",
                "KeyA",
                65,
                commands: ["SelectAll"])
            .ConfigureAwait(false);
        await DispatchKeyAsync(
                "keyUp",
                "a",
                "KeyA",
                65)
            .ConfigureAwait(false);
        await DispatchKeyAsync(
                "rawKeyDown",
                "Backspace",
                "Backspace",
                8)
            .ConfigureAwait(false);
        await DispatchKeyAsync(
                "keyUp",
                "Backspace",
                "Backspace",
                8)
            .ConfigureAwait(false);
        if (text.Length != 0)
        {
            // Input.insertText is Chromium's acknowledged, trusted input path;
            // it fires editing/input behavior without assigning page state.
            await _humanizedInput.TypeTextAsync(text).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsVisibleAsync(int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var quads = await _browser.Dom
            .GetContentQuadsAsync(backendNodeId)
            .ConfigureAwait(false);
        foreach (var quad in quads)
        {
            var clip = quad.ToBoundingClip();
            if (clip.Width <= 1
                || clip.Height <= 1
                || !double.IsFinite(clip.X)
                || !double.IsFinite(clip.Y)
                || !double.IsFinite(clip.Width)
                || !double.IsFinite(clip.Height))
            {
                continue;
            }

            var point = new CefSemanticPoint(
                clip.X + clip.Width / 2,
                clip.Y + clip.Height / 2);
            if (await HitTestIncludesAsync(point, backendNodeId)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnsureDomainsEnabledAsync()
    {
        if (_domainsEnabled)
        {
            return;
        }

        await _browser.Accessibility.EnableAsync().ConfigureAwait(false);
        await _browser.Dom.EnableAsync().ConfigureAwait(false);
        _domainsEnabled = true;
    }

    private static bool IsUsableClip(ScreenshotClip clip) =>
        clip.Width > 1
        && clip.Height > 1
        && double.IsFinite(clip.X)
        && double.IsFinite(clip.Y)
        && double.IsFinite(clip.Width)
        && double.IsFinite(clip.Height);

    private static bool IsInsideViewport(
        CefSemanticPoint point,
        NativeBrowserViewport viewport) =>
        point.X >= ViewportMarginCss
        && point.Y >= ViewportMarginCss
        && point.X <= viewport.WidthCss - ViewportMarginCss
        && point.Y <= viewport.HeightCss - ViewportMarginCss;

    private static double RevealDelta(
        double coordinate,
        double viewportExtent,
        double targetExtent)
    {
        if (coordinate >= ViewportMarginCss
            && coordinate <= viewportExtent - ViewportMarginCss)
        {
            return 0;
        }

        var desired = Math.Clamp(
            viewportExtent / 2,
            ViewportMarginCss + targetExtent / 2,
            viewportExtent - ViewportMarginCss - targetExtent / 2);
        return Math.Clamp(coordinate - desired, -800, 800);
    }

    private Task DispatchKeyAsync(
        string type,
        string key,
        string code,
        int windowsVirtualKeyCode,
        string text = "",
        IReadOnlyList<string>? commands = null) =>
        ExecuteAcknowledgedAsync(
            "Input.dispatchKeyEvent",
            SerializeKeyEvent(
                type,
                key,
                code,
                windowsVirtualKeyCode,
                text,
                commands));

    internal static string SerializeKeyEvent(
        string type,
        string key,
        string code,
        int windowsVirtualKeyCode,
        string text = "",
        IReadOnlyList<string>? commands = null) =>
        JsonSerializer.Serialize(
            new CdpSemanticKeyEventParameters(
                type,
                key,
                code,
                text,
                text,
                windowsVirtualKeyCode,
                NativeVirtualKeyCode: 0,
                commands),
            BrowserJsonContext.Default.CdpSemanticKeyEventParameters);

    private async Task ExecuteAcknowledgedAsync(
        string method,
        string parametersJson)
    {
        var reply = await _browser.ExecuteDevToolsMethodAsync(
                method,
                parametersJson)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(reply);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                error.TryGetProperty("message", out var message)
                    ? message.GetString()
                        ?? "CEF rejected an input command."
                    : "CEF rejected an input command.");
        }

        if (!document.RootElement.TryGetProperty("result", out _))
        {
            throw new InvalidOperationException(
                "CEF did not acknowledge an input command.");
        }
    }

    private static CefSemanticNode Project(AxNode node) =>
        new(
            node.Id,
            node.BackendDomNodeId,
            node.Ignored,
            node.Role,
            node.Name,
            node.ParentId,
            node.ChildIds,
            node.Properties,
            node.Value);

    private static CefSemanticNode Project(JsonElement node)
    {
        var childIds = node.TryGetProperty("childIds", out var children)
            ? children.EnumerateArray()
                .Select(static child => child.GetString() ?? string.Empty)
                .Where(static child => child.Length > 0)
                .ToArray()
            : [];
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.TryGetProperty("properties", out var sourceProperties))
        {
            foreach (var property in sourceProperties.EnumerateArray())
            {
                if (property.TryGetProperty("name", out var name)
                    && name.GetString() is { Length: > 0 } propertyName)
                {
                    properties[propertyName] = ReadAccessibilityValue(property);
                }
            }
        }

        return new CefSemanticNode(
            node.GetProperty("nodeId").GetString() ?? string.Empty,
            node.TryGetProperty("backendDOMNodeId", out var backendNodeId)
                ? backendNodeId.GetInt32()
                : null,
            node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean(),
            ReadAccessibilityProperty(node, "role"),
            ReadAccessibilityProperty(node, "name"),
            node.TryGetProperty("parentId", out var parentId)
                ? parentId.GetString()
                : null,
            childIds,
            properties,
            ReadAccessibilityProperty(node, "value"));
    }

    private static string ReadAccessibilityProperty(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value)
            ? ReadAccessibilityValue(value)
            : string.Empty;

    private static string ReadAccessibilityValue(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("value", out var value))
        {
            return string.Empty;
        }

        while (value.ValueKind == JsonValueKind.Object
               && value.TryGetProperty("value", out var nested))
        {
            value = nested;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.Array or JsonValueKind.Object =>
                value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }
}
