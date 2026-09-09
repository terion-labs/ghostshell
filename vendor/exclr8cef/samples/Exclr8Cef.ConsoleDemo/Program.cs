// Stage 4a console demo: drives the full Exclr8CEF lifecycle from .NET.
//
// Default mode: opens chrome://version in a native Chromium window and
// runs the CEF message loop until the user closes it.
//
// Headless screenshot mode (--screenshot OUT.png --url URL):
//   Spins up an offscreen browser, waits for the page to load, captures
//   a PNG via the DevTools protocol (Page.captureScreenshot), and exits.
//   Useful for automated visual diffing / archiving / vision pipelines.
//
// macOS: must be run inside the Exclr8Cef.ConsoleDemo.app bundle
//   produced by scripts/build-console-demo.sh — CEF's library loader
//   needs the Chromium Embedded Framework framework adjacent to the
//   executable inside a .app.
//
// Windows: the helper subprocess is a sibling .exe next to the main
//   executable; layout is flat (no bundle), produced by the win build
//   script (TBD — see scripts/).

using Exclr8Cef;

var versions = Cef.GetVersions();
Console.WriteLine($"Exclr8Cef {versions.Shim} — running CEF {versions.Cef} (Chromium {versions.Chromium})");

// Helper subprocess path differs per OS:
//   macOS:   <App>.app/Contents/Frameworks/exclr8cef_demo Helper.app/Contents/MacOS/exclr8cef_demo Helper
//   Windows: <dir>/exclr8cef_demo_helper.exe (sibling of the main .exe)
//   Linux:   <dir>/exclr8cef_demo_helper      (same idea, no extension)
var helperPath = OperatingSystem.IsMacOS()
    ? ResolveMacHelperPath()
    : OperatingSystem.IsWindows()
        ? Path.Combine(AppContext.BaseDirectory, "exclr8cef_demo_helper.exe")
        : Path.Combine(AppContext.BaseDirectory, "exclr8cef_demo_helper");

Console.WriteLine($"Helper subprocess: {helperPath}");
Console.WriteLine($"Exists: {File.Exists(helperPath)}");

string? screenshotOut = GetArg(args, "--screenshot");
string? accelPaintOut = GetArg(args, "--accel-paint");
string? screenshotUrl = GetArg(args, "--url");
int screenshotW = int.TryParse(GetArg(args, "--width"), out var w) ? w : 1280;
int screenshotH = int.TryParse(GetArg(args, "--height"), out var h) ? h : 800;
float screenshotDsf = float.TryParse(GetArg(args, "--dsf"), out var d) ? d : 2.0f;

if (screenshotOut is not null)
{
    if (screenshotUrl is null)
    {
        Console.Error.WriteLine("--screenshot requires --url URL");
        return 2;
    }
    return RunScreenshotMode(args, helperPath, screenshotUrl, screenshotOut,
                              screenshotW, screenshotH, screenshotDsf);
}

if (args.Contains("--loadstring-test"))
{
    return RunLoadStringTest(args, helperPath);
}

if (args.Contains("--close-on-load-end-test"))
{
    return RunCloseOnLoadEndTest(args, helperPath);
}

if (args.Contains("--accessibility-test"))
{
    return RunAccessibilityTest(args, helperPath);
}

if (accelPaintOut is not null)
{
    if (screenshotUrl is null)
    {
        Console.Error.WriteLine("--accel-paint requires --url URL");
        return 2;
    }
    if (!OperatingSystem.IsMacOS())
    {
        Console.Error.WriteLine("--accel-paint demo is macOS-only (IOSurface). The AcceleratedPaint event itself is cross-platform — the handle is just a Windows NT handle or Linux dma-buf fd elsewhere, and the consumer code path is different per platform.");
        return 3;
    }
    return RunAccelPaintMode(args, helperPath, screenshotUrl, accelPaintOut,
                              screenshotW, screenshotH, screenshotDsf);
}

Cef.Initialize(args, helperPath);
Cef.CreateBrowser("chrome://version");
Cef.RunMessageLoop();
Cef.Shutdown();

Console.WriteLine("Exclr8Cef shut down cleanly.");
return 0;

static string ResolveMacHelperPath()
{
    var frameworks = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "Frameworks"));
    var candidates = new[]
    {
        Path.Combine(
            frameworks,
            "exclr8cef_demo Helper.app",
            "Contents",
            "MacOS",
            "exclr8cef_demo Helper"),
        Path.Combine(
            frameworks,
            "Asura Helper.app",
            "Contents",
            "MacOS",
            "Asura Helper"),
    };
    return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
}

static string? GetArg(string[] argv, string name)
{
    for (int i = 0; i < argv.Length; ++i)
    {
        if (argv[i] == name && i + 1 < argv.Length) return argv[i + 1];
        if (argv[i].StartsWith(name + "=", StringComparison.Ordinal))
            return argv[i].Substring(name.Length + 1);
    }
    return null;
}

// Temporary diagnostic: exercise LoadString / data: URI navigations and
// log exactly which load events fire for each scenario.
static int RunLoadStringTest(string[] argv, string helperPath)
{
    Cef.InitializeForOsr(argv, helperPath, _ => { });
    var browser = Cef.CreateOffscreenBrowser(800, 600, 1.0f, "about:blank");
    if (browser is null) { Console.Error.WriteLine("create failed"); return 3; }

    int loadEndCount = 0;
    string lastLoadEndUrl = "";
    browser.LoadStart += (_, e) =>
        Console.WriteLine($"  [LoadStart] main={e.IsMainFrame} url={Trunc(e.Url)}");
    browser.LoadEnd += (_, e) =>
    {
        loadEndCount++;
        lastLoadEndUrl = e.Url;
        Console.WriteLine($"  [LoadEnd]   main={e.IsMainFrame} status={e.HttpStatusCode} url={Trunc(e.Url)}");
    };
    browser.LoadError += (_, e) =>
        Console.WriteLine($"  [LoadError] main={e.IsMainFrame} code={e.ErrorCode} '{e.ErrorText}' url={Trunc(e.FailedUrl)}");
    browser.LoadingStateChanged += (_, e) =>
        Console.WriteLine($"  [LoadState] isLoading={e.IsLoading}");

    void Pump(double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { Cef.DoMessageLoopWork(); Thread.Sleep(5); }
    }
    bool WaitLoadEnd(int target, double timeoutS)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutS);
        while (DateTime.UtcNow < end)
        {
            Cef.DoMessageLoopWork(); Thread.Sleep(5);
            if (loadEndCount >= target) return true;
        }
        return false;
    }

    static string Trunc(string s) => s.Length <= 90 ? s : s[..90] + $"…({s.Length} chars)";

    Console.WriteLine("== step 0: initial about:blank");
    bool ok0 = WaitLoadEnd(1, 10);
    Console.WriteLine($"   -> LoadEnd fired: {ok0} (count={loadEndCount})");

    Console.WriteLine("== step 1: LoadString small html");
    browser.LoadString("<html><body><h1>one</h1></body></html>");
    bool ok1 = WaitLoadEnd(2, 10);
    Console.WriteLine($"   -> LoadEnd fired: {ok1} (count={loadEndCount})");

    Console.WriteLine("== step 2: LoadString IDENTICAL html again (same data: URL)");
    browser.LoadString("<html><body><h1>one</h1></body></html>");
    bool ok2 = WaitLoadEnd(3, 10);
    Console.WriteLine($"   -> LoadEnd fired: {ok2} (count={loadEndCount})");

    Console.WriteLine("== step 3: LoadString different small html");
    browser.LoadString("<html><body><h1>two</h1></body></html>");
    bool ok3 = WaitLoadEnd(4, 10);
    Console.WriteLine($"   -> LoadEnd fired: {ok3} (count={loadEndCount})");

    Console.WriteLine("== step 4: LoadString LARGE html (~2.5 MB — over the 2 MB data: URL cap, " +
                      "must load via the internal resource handler)");
    string big = "<html><body><h1>big</h1><p>" +
                 new string('x', 2_500_000) + "</p></body></html>";
    browser.LoadString(big);
    bool ok4 = WaitLoadEnd(5, 10)
               && lastLoadEndUrl.StartsWith("https://loadstring.exclr8cef.internal/");
    Console.WriteLine($"   -> LoadEnd fired via internal handler: {ok4} (count={loadEndCount}, url={Trunc(lastLoadEndUrl)})");

    Console.WriteLine("== step 5: LoadString ~1.4 MB html (data: URL just under 2 MB)");
    string medium = "<html><body><h1>med</h1><p>" +
                    new string('y', 1_400_000) + "</p></body></html>";
    browser.LoadString(medium);
    bool ok5 = WaitLoadEnd(loadEndCount + 1, 10);
    Console.WriteLine($"   -> LoadEnd fired: {ok5} (count={loadEndCount})");

    Pump(0.5);
    browser.Close();
    Pump(1.0);
    Cef.Shutdown();
    Console.WriteLine($"RESULT: initial={ok0} small={ok1} sameAgain={ok2} different={ok3} largeViaHandler={ok4} medium1.4MB={ok5}");
    return 0;
}

// Regression for hosts that remove a browser-backed view as a page finishes.
// The managed callback is synchronous with native CefLoadHandler::OnLoadEnd,
// so Close must be queued until that native callback has unwound.
static int RunCloseOnLoadEndTest(string[] argv, string helperPath)
{
    var tempRoot = Path.GetTempPath();
    if (OperatingSystem.IsMacOS()
        && tempRoot.StartsWith("/var/", StringComparison.Ordinal))
    {
        tempRoot = "/private" + tempRoot;
    }
    var cacheRoot = Path.Combine(
        tempRoot,
        $"exclr8cef-close-on-load-end-{Environment.ProcessId}");
    Directory.CreateDirectory(cacheRoot);
    Cef.SetInitSettings(new Cef.CefSettings
    {
        RootCachePath = cacheRoot,
        CachePath = Path.Combine(cacheRoot, "cache"),
    });
    Cef.InitializeForOsr(argv, helperPath, _ => { });
    var browser = Cef.CreateOffscreenBrowser(320, 180, 1.0f, "about:blank");
    if (browser is null)
    {
        Console.Error.WriteLine("CreateOffscreenBrowser failed");
        Cef.Shutdown();
        return 3;
    }

    var closeRequested = false;
    var closed = false;
    browser.Closed += (_, _) => closed = true;
    browser.LoadEnd += (_, e) =>
    {
        if (!e.IsMainFrame || closeRequested) return;
        closeRequested = true;
        browser.Dispose();
        browser.Dispose();
    };

    // CreateOffscreenBrowser may finish about:blank before callers can attach
    // handlers. Start a distinct navigation only after the callback is wired so
    // this regression deterministically closes from inside LoadEnd.
    browser.LoadString("<html><body>close from load end</body></html>");

    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!closed && DateTime.UtcNow < deadline)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(5);
    }

    Cef.Shutdown();
    try
    {
        Directory.Delete(cacheRoot, recursive: true);
    }
    catch (IOException)
    {
        // The lifecycle result matters more than best-effort test-cache cleanup.
    }
    catch (UnauthorizedAccessException)
    {
        // The lifecycle result matters more than best-effort test-cache cleanup.
    }
    Console.WriteLine(
        $"RESULT: closeRequested={closeRequested} closed={closed}");
    return closeRequested && closed ? 0 : 4;
}

// Native integration probe for the same acknowledged CDP Accessibility
// round-trip used by Asura's browser agent. This deliberately runs below
// Avalonia and the session host so renderer/protocol failures stay observable.
static int RunAccessibilityTest(string[] argv, string helperPath)
{
    var tempRoot = Path.GetTempPath();
    if (OperatingSystem.IsMacOS()
        && tempRoot.StartsWith("/var/", StringComparison.Ordinal))
    {
        tempRoot = "/private" + tempRoot;
    }

    var cacheRoot = Path.Combine(
        tempRoot,
        $"exclr8cef-accessibility-{Environment.ProcessId}");
    Directory.CreateDirectory(cacheRoot);
    Cef.SetInitSettings(new Cef.CefSettings
    {
        RootCachePath = cacheRoot,
        CachePath = Path.Combine(cacheRoot, "cache"),
    });
    Cef.InitializeForOsr(argv, helperPath, _ => { });
    var browser = Cef.CreateOffscreenBrowser(640, 480, 1.0f, "about:blank");
    if (browser is null)
    {
        Console.Error.WriteLine("CreateOffscreenBrowser failed");
        Cef.Shutdown();
        return 3;
    }

    var initialLoaded = false;
    var loaded = false;
    browser.LoadEnd += (_, eventArgs) =>
    {
        if (!eventArgs.IsMainFrame)
        {
            return;
        }

        if (string.Equals(
                eventArgs.Url,
                "about:blank",
                StringComparison.Ordinal))
        {
            initialLoaded = true;
        }
        else
        {
            loaded = true;
        }
    };

    var initialDeadline = DateTime.UtcNow.AddSeconds(10);
    while (!initialLoaded && DateTime.UtcNow < initialDeadline)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(5);
    }
    browser.LoadString(
        "<html><body><button aria-label='Continue' aria-pressed='false' "
        + "onclick=\"this.setAttribute('aria-pressed','true')\">Go</button>"
        + "<label>Email<input type='email'></label>"
        + "<div style='height:1600px'></div></body></html>");

    var loadDeadline = DateTime.UtcNow.AddSeconds(10);
    while (!loaded && DateTime.UtcNow < loadDeadline)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(5);
    }

    Exception? failure = null;
    IReadOnlyList<Exclr8Cef.Cdp.AxNode>? nodes = null;
    var filled = false;
    var clicked = false;
    var scrolled = false;
    if (loaded)
    {
        var readTask = ReadAccessibilityAsync(browser);
        var readDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!readTask.IsCompleted && DateTime.UtcNow < readDeadline)
        {
            Cef.DoMessageLoopWork();
            Thread.Sleep(5);
        }

        if (!readTask.IsCompleted)
        {
            failure = new TimeoutException(
                "Accessibility CDP round-trip did not complete.");
        }
        else
        {
            try
            {
                var result = readTask.GetAwaiter().GetResult();
                nodes = result.Nodes;
                filled = result.Filled;
                clicked = result.Clicked;
                scrolled = result.Scrolled;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
    }

    browser.Close();
    var closeDeadline = DateTime.UtcNow.AddSeconds(2);
    while (!browser.IsClosed && DateTime.UtcNow < closeDeadline)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(5);
    }
    Cef.Shutdown();
    try
    {
        Directory.Delete(cacheRoot, recursive: true);
    }
    catch (IOException)
    {
        // Best-effort test-cache cleanup.
    }
    catch (UnauthorizedAccessException)
    {
        // Best-effort test-cache cleanup.
    }

    var hasButton = nodes?.Any(node =>
        string.Equals(node.Role, "button", StringComparison.OrdinalIgnoreCase)
        && string.Equals(node.Name, "Continue", StringComparison.Ordinal))
        ?? false;
    var hasInput = nodes?.Any(node =>
        string.Equals(node.Role, "textbox", StringComparison.OrdinalIgnoreCase)
        && string.Equals(node.Name, "Email", StringComparison.Ordinal))
        ?? false;
    if (failure is not null)
    {
        Console.Error.WriteLine(failure);
    }

    Console.WriteLine(
        $"RESULT: loaded={loaded} nodes={nodes?.Count ?? 0} "
        + $"button={hasButton} input={hasInput} filled={filled} "
        + $"clicked={clicked} scrolled={scrolled}");
    return loaded && hasButton && hasInput && filled && clicked && scrolled
        && failure is null
        ? 0
        : 4;
}

static async Task<(
    IReadOnlyList<Exclr8Cef.Cdp.AxNode> Nodes,
    bool Filled,
    bool Clicked,
    bool Scrolled)>
    ReadAccessibilityAsync(CefBrowser browser)
{
    await browser.Accessibility.EnableAsync().ConfigureAwait(false);
    await browser.Dom.EnableAsync().ConfigureAwait(false);
    var nodes = await browser.Accessibility
        .GetFullTreeAsync(maxDepth: 64)
        .ConfigureAwait(false);
    var input = nodes.First(node =>
        string.Equals(node.Role, "textbox", StringComparison.OrdinalIgnoreCase)
        && string.Equals(node.Name, "Email", StringComparison.Ordinal));
    var button = nodes.First(node =>
        string.Equals(node.Role, "button", StringComparison.OrdinalIgnoreCase)
        && string.Equals(node.Name, "Continue", StringComparison.Ordinal));
    var inputBackendId = input.BackendDomNodeId
        ?? throw new InvalidOperationException("Textbox has no backend node id.");
    var buttonBackendId = button.BackendDomNodeId
        ?? throw new InvalidOperationException("Button has no backend node id.");

    await browser.Dom.FocusAsync(inputBackendId).ConfigureAwait(false);
    await browser.Input.InsertTextAsync("probe value").ConfigureAwait(false);
    var filledNodes = await browser.Accessibility
        .GetPartialTreeAsync(inputBackendId, fetchRelatives: true)
        .ConfigureAwait(false);
    var filled = filledNodes.Any(node =>
        node.BackendDomNodeId == inputBackendId
        && string.Equals(node.Value, "probe value", StringComparison.Ordinal));

    var quads = await browser.Dom
        .GetContentQuadsAsync(buttonBackendId)
        .ConfigureAwait(false);
    var clip = quads.First().ToBoundingClip();
    var x = clip.X + clip.Width / 2;
    var y = clip.Y + clip.Height / 2;
    await DispatchMouseAsync(browser, "mouseMoved", x, y, "none", buttons: 0)
        .ConfigureAwait(false);
    await DispatchMouseAsync(browser, "mousePressed", x, y, "left", buttons: 1)
        .ConfigureAwait(false);
    await DispatchMouseAsync(browser, "mouseReleased", x, y, "left", buttons: 0)
        .ConfigureAwait(false);
    var clicked = false;
    for (var attempt = 0; attempt < 5 && !clicked; attempt++)
    {
        var describedButton = await browser.Dom
            .DescribeNodeAsync(buttonBackendId)
            .ConfigureAwait(false);
        var attributes = describedButton.GetProperty("attributes")
            .EnumerateArray()
            .Select(attribute => attribute.GetString() ?? string.Empty)
            .ToArray();
        for (var index = 0; index + 1 < attributes.Length; index += 2)
        {
            if (string.Equals(
                    attributes[index],
                    "aria-pressed",
                    StringComparison.Ordinal)
                && string.Equals(
                    attributes[index + 1],
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                clicked = true;
                break;
            }
        }
        if (!clicked)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    await browser.Input
        .SynthesizeScrollGestureAsync(
            x: 320,
            y: 240,
            yDistance: -300,
            gestureSource: "mouse")
        .ConfigureAwait(false);
    return (nodes, filled, clicked, Scrolled: true);
}

static Task DispatchMouseAsync(
    CefBrowser browser,
    string type,
    double x,
    double y,
    string button,
    int buttons) =>
    browser.ExecuteDevToolsMethodAsync(
        "Input.dispatchMouseEvent",
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type,
            x,
            y,
            button,
            buttons,
            clickCount = string.Equals(button, "none", StringComparison.Ordinal)
                ? 0
                : 1,
            pointerType = "mouse",
        }));

static int RunScreenshotMode(string[] argv, string helperPath, string url, string outPath,
                              int width, int height, float dsf)
{
    Console.WriteLine($"Headless screenshot: {url} → {outPath} ({width}×{height} @ {dsf}x)");

    // --stub-html "<body>...</body>" intercepts the main navigation and
    // serves the given HTML as text/html. Lets the demo exercise the
    // ShouldHandleResource → ResolveResourceHandlerRequest pipeline
    // without depending on the public internet.
    string? stubHtml = GetArg(argv, "--stub-html");
    if (stubHtml is not null)
    {
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(stubHtml);
        Cef.ShouldHandleResource = info =>
        {
            // Only claim the top-level navigation; let CSS / icons fail
            // gracefully (the stub body is self-contained anyway).
            if (info.Url != url) return false;
            // Resolve synchronously here on the IO thread — this stub is
            // tiny and copy-free on the wire.
            Cef.ResolveResourceHandlerRequest(
                info.Token, 200, "OK", "text/html; charset=utf-8",
                bodyBytes);
            return true;
        };
    }

    // OSR mode + external pump. The schedule callback is a no-op — we pump
    // synchronously in the main loop below. CEF still fires it when async
    // work needs to be drained sooner, but Sleep(10) inside the loop gives
    // us a tight enough cadence for a one-shot screenshot.
    Cef.InitializeForOsr(argv, helperPath, _ => { });

    var browser = Cef.CreateOffscreenBrowser(width, height, dsf, url);
    if (browser is null)
    {
        Console.Error.WriteLine("CreateOffscreenBrowser failed");
        Cef.Shutdown();
        return 3;
    }

    bool done = false;
    bool failed = false;
    DateTime? captureAt = null;     // grace-period gate, set by LoadEnd
    Task<byte[]>? captureTask = null;
    browser.LoadEnd += (_, e) =>
    {
        if (e.HttpStatusCode >= 400)
            Console.WriteLine($"  warning: HTTP {e.HttpStatusCode} from initial load");
        // Tiny grace period for late-running JS / font swap. CDP gives us
        // no general "page-fully-settled" signal — 250ms covers the common
        // cases; consumers needing more should drive their own wait via JS.
        captureAt ??= DateTime.UtcNow.AddMilliseconds(250);
    };
    browser.LoadError += (_, e) =>
    {
        if (e.FailedUrl == url)
        {
            Console.Error.WriteLine($"Load failed: {e.ErrorText} ({e.ErrorCode})");
            failed = true; done = true;
        }
    };

    // Pump from the main thread. CEF requires that ExecuteDevToolsMethod
    // (and most browser-host APIs) be called from the same thread that
    // initialized CEF — so we kick off CapturePageAsync inline here, then
    // poll the resulting Task without awaiting (which would transfer the
    // continuation onto the threadpool and break that contract).
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (!done && DateTime.UtcNow < deadline)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(10);

        if (captureTask is null && captureAt is DateTime t && DateTime.UtcNow >= t)
        {
            captureTask = browser.CapturePageAsync();
        }

        if (captureTask is { IsCompleted: true } finished)
        {
            try
            {
                var png = finished.GetAwaiter().GetResult();
                File.WriteAllBytes(outPath, png);
                Console.WriteLine($"Wrote {png.Length:N0} bytes to {outPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Capture failed: {ex.Message}");
                failed = true;
            }
            done = true;
        }
    }
    if (!done)
    {
        Console.Error.WriteLine("Timed out after 30s waiting for load + capture");
        failed = true;
    }

    // Drain a few more pump ticks so the browser closes cleanly before Shutdown.
    browser.Close();
    for (int i = 0; i < 100; ++i)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(10);
    }
    Cef.Shutdown();
    return failed ? 4 : 0;
}

// Accelerated-paint mode (macOS only). Creates an OSR browser with
// SharedTexture enabled so CEF delivers paints via the AcceleratedPaint
// event with an IOSurface handle. The host locks the IOSurface, reads
// the BGRA pixels directly out of GPU-shared memory, and writes them as
// PPM (Netpbm; viewable by Preview / ffmpeg / GIMP / ImageMagick).
//
// Real Avalonia consumers would skip the lock-and-read and instead
// import the IOSurface as a Metal texture, then wrap that as an
// SkBackendTexture for Skia. That path is outside this demo's scope —
// the demo only proves the wiring + the handle is real + the pixels are
// what we expect.
static int RunAccelPaintMode(string[] argv, string helperPath, string url, string outPath,
                              int width, int height, float dsf)
{
    Console.WriteLine($"Accelerated paint: {url} → {outPath} ({width}×{height} @ {dsf}x, BGRA via IOSurface)");

    Cef.InitializeForOsr(argv, helperPath, _ => { });

    var browser = Cef.CreateOffscreenBrowserEx(
        width, height, dsf, url, context: null,
        flags: Cef.OffscreenFlags.SharedTexture);
    if (browser is null)
    {
        Console.Error.WriteLine("CreateOffscreenBrowserEx failed");
        Cef.Shutdown();
        return 3;
    }

    // Track when we've written a frame so the main loop can exit.
    // AcceleratedPaint fires on the CEF UI thread = same thread as
    // DoMessageLoopWork — so the file write happens on the main loop
    // tick that delivered the paint. No threading hazards.
    bool wrote = false;
    bool failed = false;
    int frameNum = 0;
    bool loadEnded = false;

    browser.LoadEnd += (_, _) => loadEnded = true;

    browser.AcceleratedPaint += (_, ap) =>
    {
        frameNum++;
        Console.WriteLine($"AcceleratedPaint #{frameNum}: {ap.CodedWidth}×{ap.CodedHeight}, handle=0x{ap.SharedHandle.ToInt64():X}, format={ap.Format}, loadEnded={loadEnded}");
        // Capture the first paint that arrives AFTER LoadEnd — static
        // pages may fire one early "loading" paint and one "done" paint;
        // the latter is the one we want. If the page never fires LoadEnd
        // (rare) the timeout below will still produce a file via the
        // last-seen paint.
        if (wrote || !loadEnded || ap.SharedHandle == IntPtr.Zero) return;
        try
        {
            WriteIOSurfaceAsPpm(ap.SharedHandle, outPath, ap.CodedWidth, ap.CodedHeight, ap.Format);
            Console.WriteLine($"Wrote {outPath} from frame #{frameNum}");
            wrote = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write IOSurface contents: {ex.Message}");
            failed = true;
            wrote = true;
        }
    };

    browser.LoadError += (_, e) =>
    {
        if (e.FailedUrl == url)
        {
            Console.Error.WriteLine($"Load failed: {e.ErrorText} ({e.ErrorCode})");
            failed = true; wrote = true;
        }
    };

    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (!wrote && DateTime.UtcNow < deadline)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(10);
    }
    if (!wrote)
    {
        Console.Error.WriteLine("Timed out waiting for accelerated paint. Check that SharedTexture is supported on this CEF build.");
        failed = true;
    }

    browser.Close();
    for (int i = 0; i < 100; ++i)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(10);
    }
    Cef.Shutdown();
    return failed ? 4 : 0;
}

// Static helper at file scope reachable from the top-level statements.
static void WriteIOSurfaceAsPpm(IntPtr surface, string outPath, int reportedW, int reportedH, Exclr8Cef.Cef.CefColorType format)
    => IOSurfaceInterop.WriteAsPpm(surface, outPath, format);

// macOS IOSurface FFI + PPM writer. IOSurface ships in
// /System/Library/Frameworks/IOSurface.framework. We only use the
// lock-and-read path; for true GPU consumption a host would instead
// import the IOSurface as a Metal texture
// (MTLDevice.MakeTexture(descriptor, iosurface:0)) and bind it as a
// shader resource — no CPU read involved.
internal static class IOSurfaceInterop
{
    private const string Lib = "/System/Library/Frameworks/IOSurface.framework/IOSurface";

    [System.Runtime.InteropServices.DllImport(Lib)]
    private static extern int IOSurfaceLock(IntPtr surface, uint options, out uint seed);
    [System.Runtime.InteropServices.DllImport(Lib)]
    private static extern int IOSurfaceUnlock(IntPtr surface, uint options, out uint seed);
    [System.Runtime.InteropServices.DllImport(Lib)]
    private static extern IntPtr IOSurfaceGetBaseAddress(IntPtr surface);
    [System.Runtime.InteropServices.DllImport(Lib)]
    private static extern UIntPtr IOSurfaceGetWidth(IntPtr surface);
    [System.Runtime.InteropServices.DllImport(Lib)]
    private static extern UIntPtr IOSurfaceGetHeight(IntPtr surface);
    [System.Runtime.InteropServices.DllImport(Lib)]
    private static extern UIntPtr IOSurfaceGetBytesPerRow(IntPtr surface);

    private const uint kIOSurfaceLockReadOnly = 0x1;

    public static unsafe void WriteAsPpm(IntPtr surface, string outPath, Exclr8Cef.Cef.CefColorType format)
    {
        // Trust the IOSurface's own dimensions over the reported
        // coded_size — coded_size can include extra rows/columns for
        // GPU alignment that aren't real content.
        int w = (int)IOSurfaceGetWidth(surface);
        int h = (int)IOSurfaceGetHeight(surface);
        int stride = (int)IOSurfaceGetBytesPerRow(surface);
        bool bgra = format == Exclr8Cef.Cef.CefColorType.Bgra8888;

        if (IOSurfaceLock(surface, kIOSurfaceLockReadOnly, out _) != 0)
            throw new InvalidOperationException("IOSurfaceLock failed");
        try
        {
            byte* baseAddr = (byte*)IOSurfaceGetBaseAddress(surface);
            if (baseAddr == null) throw new InvalidOperationException("IOSurfaceGetBaseAddress returned null");

            // PPM (P6): magic + dims + max + raw RGB bytes. Tools that
            // read PPM: Preview.app, GIMP, ImageMagick, ffmpeg, sips.
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
            fs.Write(header, 0, header.Length);

            var row = new byte[w * 3];
            for (int y = 0; y < h; ++y)
            {
                byte* src = baseAddr + (long)y * stride;
                if (bgra)
                {
                    for (int x = 0; x < w; ++x)
                    {
                        row[x * 3 + 0] = src[x * 4 + 2];  // R
                        row[x * 3 + 1] = src[x * 4 + 1];  // G
                        row[x * 3 + 2] = src[x * 4 + 0];  // B
                    }
                }
                else  // RGBA
                {
                    for (int x = 0; x < w; ++x)
                    {
                        row[x * 3 + 0] = src[x * 4 + 0];
                        row[x * 3 + 1] = src[x * 4 + 1];
                        row[x * 3 + 2] = src[x * 4 + 2];
                    }
                }
                fs.Write(row, 0, row.Length);
            }
        }
        finally
        {
            IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, out _);
        }
    }
}
