using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Asura.Application;
using Exclr8Cef;

namespace Asura.Browser;

internal interface ICefDevToolsTransport
{
    Task<string> ExecuteAsync(string method, string? parametersJson);
}

internal sealed class CefDevToolsTransport(CefBrowser browser)
    : ICefDevToolsTransport
{
    private readonly CefBrowser _browser = browser
        ?? throw new ArgumentNullException(nameof(browser));

    public Task<string> ExecuteAsync(string method, string? parametersJson) =>
        _browser.ExecuteDevToolsMethodAsync(method, parametersJson);
}

/// <summary>
/// Private typed CDP adapter. No method name, target identifier, execution
/// context, or remote-object handle crosses the Browser assembly boundary.
/// </summary>
internal sealed class CefBrowserAutomationAdapter
{
    private const string IsolatedWorldName = "asura-agent-isolated";
    private const int MaximumCdpReplyBytes = 256 * 1024;
    private const int MaximumReadableArticleCharacters = 1024 * 1024;
    private const int ReadableArticleChunkCharacters = 32 * 1024;
    private const int MaximumExtractedLinkCharacters = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly string PageLinkCollectionStatements = $$"""
        const links = [];
        const seenLinks = new Set();
        let linkCharacters = 0;
        let linksTruncated = false;
        for (const anchor of document.querySelectorAll('a[href]')) {
          const url = String(anchor.href || '');
          if (!/^https?:\/\//i.test(url) || seenLinks.has(url)) {
            continue;
          }

          seenLinks.add(url);
          if (links.length >= {{AgentWebReadResult.MaximumLinkCount}}
              || url.length > {{AgentWebToolRequest.MaximumUrlBytes}}
              || linkCharacters + url.length > {{MaximumExtractedLinkCharacters}}) {
            linksTruncated = true;
            continue;
          }

          links.push(url);
          linkCharacters += url.length;
        }

        """;
    private readonly ICefDevToolsTransport _transport;
    private readonly CefHumanizedInput _humanizedInput;

    public CefBrowserAutomationAdapter(ICefDevToolsTransport transport)
        : this(transport, new CefHumanizedInput(transport))
    {
    }

    public CefBrowserAutomationAdapter(
        ICefDevToolsTransport transport,
        CefHumanizedInput humanizedInput)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _humanizedInput = humanizedInput
            ?? throw new ArgumentNullException(nameof(humanizedInput));
    }

    public async Task<NativeBrowserViewport> ReadViewportAsync()
    {
        return await _humanizedInput.ReadViewportAsync().ConfigureAwait(false);
    }

    public Task<NativeBrowserAutomationResult> DispatchMouseAsync(
        BrowserMouseRequest request) =>
        CaptureOutcomeAsync(() => DispatchMouseCoreAsync(request));

    public Task<NativeBrowserAutomationResult> DispatchKeyAsync(
        BrowserKeyRequest request) =>
        CaptureOutcomeAsync(() => DispatchKeyCoreAsync(request));

    public Task<NativeBrowserAutomationResult> DispatchScrollAsync(
        BrowserScrollRequest request) =>
        CaptureOutcomeAsync(() => DispatchScrollCoreAsync(request));

    public Task<NativeBrowserAutomationResult> EvaluateAsync(
        BrowserEvaluateRequest request) =>
        CaptureOutcomeAsync(() => EvaluateCoreAsync(request));

    public Task<NativeBrowserAutomationResult> ExtractWebSearchDocumentAsync(
        int maximumResults)
    {
        if (maximumResults is < 1 or > AgentWebSearchRequest.MaximumResultCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        return CaptureOutcomeAsync(
            () => ExtractWebSearchDocumentCoreAsync(maximumResults));
    }

    public Task<NativeBrowserAutomationResult> ExtractRenderedDocumentAsync() =>
        CaptureOutcomeAsync(ExtractRenderedDocumentCoreAsync);

    public Task<NativeBrowserAutomationResult> ExtractReadableArticleAsync() =>
        CaptureOutcomeAsync(ExtractReadableArticleCoreAsync);

    private async Task<NativeBrowserAutomationResult> DispatchMouseCoreAsync(
        BrowserMouseRequest request)
    {
        switch (request.Action)
        {
            case BrowserMouseAction.Move:
                await _humanizedInput.MoveAsync(
                        request.XCss,
                        request.YCss,
                        request.Buttons,
                        request.Modifiers)
                    .ConfigureAwait(false);
                break;
            case BrowserMouseAction.Click:
                await _humanizedInput.ClickAsync(
                        request.XCss,
                        request.YCss,
                        request.Button,
                        request.Buttons,
                        request.Modifiers,
                        request.ClickCount)
                    .ConfigureAwait(false);
                break;
            case BrowserMouseAction.Wheel:
                await _humanizedInput.ScrollAsync(
                        request.XCss,
                        request.YCss,
                        request.DeltaX,
                        request.DeltaY,
                        request.Modifiers,
                        request.Buttons)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        return NativeBrowserAutomationResult.Acknowledged();
    }

    private async Task<NativeBrowserAutomationResult> DispatchScrollCoreAsync(
        BrowserScrollRequest request)
    {
        await _humanizedInput.ScrollAsync(
                request.OriginXCss,
                request.OriginYCss,
                request.DeltaX,
                request.DeltaY,
                request.Modifiers)
            .ConfigureAwait(false);
        return NativeBrowserAutomationResult.Acknowledged();
    }

    private async Task<NativeBrowserAutomationResult> DispatchKeyCoreAsync(
        BrowserKeyRequest request)
    {
        await _humanizedInput.EnsureCursorVisibleAsync().ConfigureAwait(false);
        var key = KeyDescriptor.For(request.Key, request.Modifiers);
        await DispatchKeyEventAsync("keyDown", key, request.Modifiers)
            .ConfigureAwait(false);
        await DispatchKeyEventAsync("keyUp", key, request.Modifiers)
            .ConfigureAwait(false);
        _humanizedInput.KeepCursorVisible();

        return NativeBrowserAutomationResult.Acknowledged();
    }

    private async Task<NativeBrowserAutomationResult> EvaluateCoreAsync(
        BrowserEvaluateRequest request)
    {
        var contextId = request.World == BrowserEvaluationWorld.Isolated
            ? await CreateIsolatedWorldAsync().ConfigureAwait(false)
            : null;
        return contextId == 0
            ? NativeBrowserAutomationResult.Rejected("script_context_unavailable")
            : await EvaluateSourceAsync(
                    request.Source,
                    request.AwaitPromise,
                    request.Timeout,
                    throwOnSideEffect: true,
                    contextId)
                .ConfigureAwait(false);
    }

    private async Task<NativeBrowserAutomationResult>
        ExtractWebSearchDocumentCoreAsync(int maximumResults)
    {
        var contextId = await CreateIsolatedWorldAsync().ConfigureAwait(false);
        if (contextId == 0)
        {
            return NativeBrowserAutomationResult.Rejected(
                "script_context_unavailable");
        }

        var source = $$"""
            (() => {
              const resultCandidates = () => {
                const resultRoot = document.querySelector('#rso');
                const candidates = [];
                if (!resultRoot) {
                  return candidates;
                }

                for (const heading of resultRoot.querySelectorAll('h3')) {
                  const anchor = heading.closest('a[href]');
                  const block = heading.closest('[jscontroller][lang]');
                  if (anchor && block && resultRoot.contains(block)) {
                    candidates.push({ anchor, block, heading });
                  }
                }

                return candidates;
              };

              const compact = value => String(value || '').replace(/\s+/g, ' ').trim();
              const pageText = compact(document.body && document.body.innerText);
              const candidates = resultCandidates();
              const results = [];
              let truncated = candidates.length > {{maximumResults}};
              for (const { anchor, block, heading } of candidates) {
                if (results.length >= {{maximumResults}}) {
                  break;
                }

                const clone = block.cloneNode(true);
                const clonedHeading = clone.querySelector('h3');
                if (clonedHeading) {
                  clonedHeading.remove();
                }
                for (const hidden of clone.querySelectorAll('[aria-hidden]')) {
                  const parent = hidden.parentElement;
                  if (parent && parent !== clone) {
                    parent.remove();
                  } else {
                    hidden.remove();
                  }
                }
                for (const element of clone.querySelectorAll(
                    'script, style, noscript, iframe, object, embed, form, input, button, dialog, template, [hidden]')) {
                  element.remove();
                }

                const description = compact(clone.textContent);
                const title = compact(heading.textContent);
                const url = String(anchor.href || '');
                if (!url || !title || !description) {
                  continue;
                }

                truncated ||= title.length > 1024 || description.length > 2048;
                results.push({
                  url: url.slice(0, 2048),
                  title: title.slice(0, 1024),
                  desc: description.slice(0, 2048)
                });
              }
              return {
                title: compact(document.title).slice(0, 1024),
                pageText: pageText.slice(0, 20480),
                results,
                truncated
              };
            })()
            """;
        return await EvaluateSourceAsync(
                source,
                awaitPromise: false,
                TimeSpan.FromSeconds(5),
                throwOnSideEffect: false,
                contextId)
            .ConfigureAwait(false);
    }

    private async Task<NativeBrowserAutomationResult>
        ExtractReadableArticleCoreAsync()
    {
        var contextId = await CreateIsolatedWorldAsync().ConfigureAwait(false);
        if (contextId == 0)
        {
            return NativeBrowserAutomationResult.Rejected(
                "script_context_unavailable");
        }

        var extractSource = MozillaReadabilityScript.Source + """

            ;(() => {
            """ + PageLinkCollectionStatements + """
              const article = new Readability(
                document.cloneNode(true),
                { keepClasses: false, maxElemsToParse: 0 }).parse();
              if (!article || typeof article.content !== 'string'
                  || !article.content.trim()) {
                return null;
              }

              globalThis.__asuraReadableArticleHtml = article.content;
              return {
                title: String(article.title || document.title || '').slice(0, 1024),
                length: article.content.length,
                links,
                linksTruncated
              };
            })()
            """;
        var initialized = await EvaluateSourceAsync(
                extractSource,
                awaitPromise: false,
                TimeSpan.FromSeconds(15),
                throwOnSideEffect: false,
                contextId)
            .ConfigureAwait(false);
        if (initialized.Status is not NativeBrowserAutomationStatus.Acknowledged
            || initialized.ResultJson is null)
        {
            return initialized;
        }

        using var metadata = JsonDocument.Parse(initialized.ResultJson);
        var root = metadata.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("title", out var titleValue)
            || titleValue.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("length", out var lengthValue)
            || !lengthValue.TryGetInt32(out var articleLength)
            || articleLength < 0)
        {
            return NativeBrowserAutomationResult.Rejected(
                "readable_article_metadata_invalid");
        }

        var captureLength = Math.Min(
            articleLength,
            MaximumReadableArticleCharacters);
        var articleHtml = new StringBuilder(captureLength);
        var offset = 0;
        while (offset < captureLength)
        {
            var chunkSource = $$"""
                (() => {
                  const html = globalThis.__asuraReadableArticleHtml;
                  if (typeof html !== 'string') {
                    return null;
                  }

                  let end = Math.min(
                    {{captureLength}},
                    {{offset}} + {{ReadableArticleChunkCharacters}});
                  if (end < {{captureLength}}) {
                    const before = html.charCodeAt(end - 1);
                    const after = html.charCodeAt(end);
                    if (before >= 0xD800 && before <= 0xDBFF
                        && after >= 0xDC00 && after <= 0xDFFF) {
                      end--;
                    }
                  }

                  return {
                    chunk: html.slice({{offset}}, end),
                    nextOffset: end
                  };
                })()
                """;
            var extracted = await EvaluateSourceAsync(
                    chunkSource,
                    awaitPromise: false,
                    TimeSpan.FromSeconds(5),
                    throwOnSideEffect: false,
                    contextId)
                .ConfigureAwait(false);
            if (extracted.Status is not NativeBrowserAutomationStatus.Acknowledged
                || extracted.ResultJson is null)
            {
                return extracted;
            }

            using var chunkDocument = JsonDocument.Parse(extracted.ResultJson);
            var chunkRoot = chunkDocument.RootElement;
            if (chunkRoot.ValueKind != JsonValueKind.Object
                || !chunkRoot.TryGetProperty("chunk", out var chunkValue)
                || chunkValue.ValueKind != JsonValueKind.String
                || !chunkRoot.TryGetProperty("nextOffset", out var nextOffsetValue)
                || !nextOffsetValue.TryGetInt32(out var nextOffset)
                || nextOffset <= offset
                || nextOffset > captureLength)
            {
                return NativeBrowserAutomationResult.Rejected(
                    "readable_article_chunk_invalid");
            }

            var chunk = chunkValue.GetString()!;
            if (chunk.Length != nextOffset - offset)
            {
                return NativeBrowserAutomationResult.Rejected(
                    "readable_article_chunk_invalid");
            }

            articleHtml.Append(chunk);
            offset = nextOffset;
        }

        return NativeBrowserAutomationResult.Acknowledged(
            SerializeExtractedDocument(
                titleValue.GetString() ?? string.Empty,
                articleHtml.ToString(),
                ReadLinks(root),
                articleLength > captureLength
                    || root.GetProperty("linksTruncated").GetBoolean()));
    }

    private async Task<NativeBrowserAutomationResult>
        ExtractRenderedDocumentCoreAsync()
    {
        var contextId = await CreateIsolatedWorldAsync().ConfigureAwait(false);
        if (contextId == 0)
        {
            return NativeBrowserAutomationResult.Rejected(
                "script_context_unavailable");
        }

        var source = """
            (() => {
              const maximum = 98304;
              const html = document.documentElement ? document.documentElement.outerHTML : '';
            """ + PageLinkCollectionStatements + """
              return {
                title: String(document.title || '').slice(0, 1024),
                html: html.slice(0, maximum),
                links,
                truncated: html.length > maximum || linksTruncated
              };
            })()
            """;
        return await EvaluateSourceAsync(
                source,
                awaitPromise: false,
                TimeSpan.FromSeconds(5),
                throwOnSideEffect: false,
                contextId)
            .ConfigureAwait(false);
    }

    private static string SerializeExtractedDocument(
        string title,
        string html,
        IReadOnlyList<string> links,
        bool truncated)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        writer.WriteStartObject();
        writer.WriteString("title", title);
        writer.WriteString("html", html);
        writer.WriteStartArray("links");
        foreach (var link in links)
        {
            writer.WriteStringValue(link);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", truncated);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static IReadOnlyList<string> ReadLinks(JsonElement root)
    {
        var links = root.GetProperty("links");
        if (links.ValueKind is not JsonValueKind.Array
            || links.GetArrayLength() > AgentWebReadResult.MaximumLinkCount)
        {
            throw new JsonException("Readable article links are invalid.");
        }

        return
        [
            .. links.EnumerateArray()
                .Select(link => link.GetString() ?? throw new JsonException(
                    "Readable article link is invalid.")),
        ];
    }

    private async Task<int?> CreateIsolatedWorldAsync()
    {
        using var frameReply = await ExecuteAsync("Page.getFrameTree", null)
            .ConfigureAwait(false);
        var frameId = RequireResult(frameReply.RootElement)
            .GetProperty("frameTree")
            .GetProperty("frame")
            .GetProperty("id")
            .GetString();
        if (string.IsNullOrWhiteSpace(frameId))
        {
            return 0;
        }

        using var worldReply = await ExecuteAsync(
                "Page.createIsolatedWorld",
                JsonSerializer.Serialize(
                    new CdpCreateIsolatedWorldParameters(
                        frameId,
                        IsolatedWorldName,
                        GrantUniveralAccess: false),
                    BrowserJsonContext.Default.CdpCreateIsolatedWorldParameters))
            .ConfigureAwait(false);
        return RequireResult(worldReply.RootElement)
            .GetProperty("executionContextId")
            .GetInt32();
    }

    private async Task<NativeBrowserAutomationResult> EvaluateSourceAsync(
        string source,
        bool awaitPromise,
        TimeSpan timeout,
        bool throwOnSideEffect,
        int? contextId)
    {
        using var evaluationReply = await ExecuteAsync(
                "Runtime.evaluate",
                JsonSerializer.Serialize(
                    new CdpEvaluationParameters(
                        source,
                        awaitPromise,
                        ReturnByValue: true,
                        GeneratePreview: false,
                        UserGesture: false,
                        timeout.TotalMilliseconds,
                        DisableBreaks: true,
                        ReplMode: false,
                        AllowUnsafeEvalBlockedByCSP: false,
                        throwOnSideEffect,
                        contextId),
                    BrowserJsonContext.Default.CdpEvaluationParameters))
            .ConfigureAwait(false);
        var evaluation = RequireResult(evaluationReply.RootElement);
        if (evaluation.TryGetProperty("exceptionDetails", out _))
        {
            return NativeBrowserAutomationResult.Rejected("script_exception");
        }

        var remoteObject = evaluation.GetProperty("result");
        if (remoteObject.TryGetProperty("objectId", out _)
            || remoteObject.TryGetProperty("unserializableValue", out _))
        {
            return NativeBrowserAutomationResult.Rejected(
                "script_result_not_serializable");
        }

        var json = remoteObject.TryGetProperty("value", out var value)
            ? value.GetRawText()
            : "null";
        return NativeBrowserAutomationResult.Acknowledged(json);
    }

    private Task DispatchKeyEventAsync(
        string type,
        KeyDescriptor key,
        BrowserInputModifiers modifiers) =>
        ExecuteAcknowledgedAsync(
            "Input.dispatchKeyEvent",
            JsonSerializer.Serialize(
                new CdpAutomationKeyEventParameters(
                    type,
                    (int)modifiers,
                    key.Key,
                    key.Code,
                    string.Equals(type, "keyDown", StringComparison.Ordinal)
                        && modifiers is BrowserInputModifiers.None
                            ? key.Text
                            : string.Empty,
                    string.Equals(type, "keyDown", StringComparison.Ordinal)
                        && modifiers is BrowserInputModifiers.None
                            ? key.Text
                            : string.Empty,
                    key.VirtualKeyCode,
                    NativeVirtualKeyCode: 0,
                    AutoRepeat: false,
                    IsKeypad: false,
                    modifiers.HasFlag(BrowserInputModifiers.Alt)),
                BrowserJsonContext.Default.CdpAutomationKeyEventParameters));

    private async Task ExecuteAcknowledgedAsync(string method, string parametersJson)
    {
        using var reply = await ExecuteAsync(method, parametersJson).ConfigureAwait(false);
        _ = RequireResult(reply.RootElement);
    }

    private async Task<JsonDocument> ExecuteAsync(string method, string? parametersJson)
    {
        var reply = await _transport.ExecuteAsync(
                method,
                parametersJson)
            .ConfigureAwait(false);
        try
        {
            if (StrictUtf8.GetByteCount(reply) > MaximumCdpReplyBytes)
            {
                throw new InvalidOperationException("CEF returned an oversized CDP reply.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidOperationException("CEF returned invalid Unicode.", exception);
        }

        return JsonDocument.Parse(reply);
    }

    private static JsonElement RequireResult(JsonElement reply)
    {
        if (reply.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "CEF rejected the typed command."
                    : "CEF rejected the typed command.");
        }

        return reply.TryGetProperty("result", out var result)
            ? result
            : throw new InvalidOperationException("CEF did not acknowledge the typed command.");
    }

    private static async Task<NativeBrowserAutomationResult> CaptureOutcomeAsync(
        Func<Task<NativeBrowserAutomationResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NativeBrowserAutomationResult.OutcomeUnknown();
        }
    }

    private sealed record KeyDescriptor(
        string Key,
        string Code,
        int VirtualKeyCode,
        string Text)
    {
        public static KeyDescriptor For(
            BrowserKey value,
            BrowserInputModifiers modifiers)
        {
            var name = value.ToString();
            if (value is >= BrowserKey.A and <= BrowserKey.Z)
            {
                var letter = name[0];
                var shifted = modifiers.HasFlag(BrowserInputModifiers.Shift);
                return new KeyDescriptor(
                    shifted ? name : name.ToLowerInvariant(),
                    "Key" + name,
                    letter,
                    shifted ? name : name.ToLowerInvariant());
            }

            if (value is >= BrowserKey.Digit0 and <= BrowserKey.Digit9)
            {
                var digit = name[^1];
                return new KeyDescriptor(digit.ToString(), name, digit, digit.ToString());
            }

            return value switch
            {
                BrowserKey.Backspace => new("Backspace", "Backspace", 8, ""),
                BrowserKey.Tab => new("Tab", "Tab", 9, "\t"),
                BrowserKey.Enter => new("Enter", "Enter", 13, "\r"),
                BrowserKey.Escape => new("Escape", "Escape", 27, ""),
                BrowserKey.Space => new(" ", "Space", 32, " "),
                BrowserKey.ArrowLeft => new("ArrowLeft", "ArrowLeft", 37, ""),
                BrowserKey.ArrowUp => new("ArrowUp", "ArrowUp", 38, ""),
                BrowserKey.ArrowRight => new("ArrowRight", "ArrowRight", 39, ""),
                BrowserKey.ArrowDown => new("ArrowDown", "ArrowDown", 40, ""),
                BrowserKey.Insert => new("Insert", "Insert", 45, ""),
                BrowserKey.Delete => new("Delete", "Delete", 46, ""),
                BrowserKey.Home => new("Home", "Home", 36, ""),
                BrowserKey.End => new("End", "End", 35, ""),
                BrowserKey.PageUp => new("PageUp", "PageUp", 33, ""),
                BrowserKey.PageDown => new("PageDown", "PageDown", 34, ""),
                BrowserKey.Alt => new("Alt", "AltLeft", 18, ""),
                BrowserKey.Control => new("Control", "ControlLeft", 17, ""),
                BrowserKey.Meta => new("Meta", "MetaLeft", 91, ""),
                BrowserKey.Shift => new("Shift", "ShiftLeft", 16, ""),
                >= BrowserKey.F1 and <= BrowserKey.F12 =>
                    new(name, name, 112 + (value - BrowserKey.F1), ""),
                BrowserKey.Minus => new("-", "Minus", 189, "-"),
                BrowserKey.Equal => new("=", "Equal", 187, "="),
                BrowserKey.BracketLeft => new("[", "BracketLeft", 219, "["),
                BrowserKey.BracketRight => new("]", "BracketRight", 221, "]"),
                BrowserKey.Backslash => new("\\", "Backslash", 220, "\\"),
                BrowserKey.Semicolon => new(";", "Semicolon", 186, ";"),
                BrowserKey.Quote => new("'", "Quote", 222, "'"),
                BrowserKey.Backquote => new("`", "Backquote", 192, "`"),
                BrowserKey.Comma => new(",", "Comma", 188, ","),
                BrowserKey.Period => new(".", "Period", 190, "."),
                BrowserKey.Slash => new("/", "Slash", 191, "/"),
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            };
        }
    }
}
