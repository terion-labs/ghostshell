using System.Diagnostics;
using Asura.Application;
using Exclr8Cef;
using Exclr8Cef.WebView;

namespace Asura.Browser;

/// <summary>
/// Runs the real packaged CEF checkbox path without starting the desktop UI.
/// </summary>
internal static class BrowserNativeCheckboxProbe
{
    public const string CommandLineSwitch = "--native-browser-checkbox-probe";

    public static int Run()
    {
        var tempRoot = Path.GetTempPath();
        if (OperatingSystem.IsMacOS()
            && tempRoot.StartsWith("/var/", StringComparison.Ordinal))
        {
            tempRoot = "/private" + tempRoot;
        }

        var cacheRoot = Path.Combine(
            tempRoot,
            $"asura-browser-checkbox-{Environment.ProcessId}");
        CefBrowser? browser = null;
        var initialized = false;
        try
        {
            var helperPath = AvaloniaSetup.LocateMacHelper(
                "Asura Helper");
            if (OperatingSystem.IsMacOS() && helperPath is null)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.native-checkbox-probe.helper-missing",
                    SecretSafeDiagnosticKind.Unexpected);
                return 2;
            }

            Directory.CreateDirectory(cacheRoot);
            BrowserEngineRuntime.ValidateVersions(Cef.GetVersions());
            Cef.SetInitSettings(new Cef.CefSettings
            {
                CachePath = Path.Combine(cacheRoot, "cache"),
                RootCachePath = cacheRoot,
                LogFile = Path.Combine(cacheRoot, "cef.log"),
                LogSeverity = Cef.CefLogSeverity.Warning,
                RemoteDebuggingPort = 0,
            });
            Cef.InitializeForOsr(
                Environment.GetCommandLineArgs(),
                helperPath,
                _ => { });
            initialized = true;

            browser = Cef.CreateOffscreenBrowser(
                640,
                480,
                1,
                "about:blank");
            if (browser is null)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.native-checkbox-probe.create-failed",
                    SecretSafeDiagnosticKind.Unexpected);
                return 3;
            }

            var loaded = false;
            browser.LoadEnd += (_, eventArgs) =>
            {
                if (eventArgs.IsMainFrame
                    && !string.Equals(
                        eventArgs.Url,
                        "about:blank",
                        StringComparison.Ordinal))
                {
                    loaded = true;
                }
            };
            PumpFor(TimeSpan.FromMilliseconds(250));
            browser.LoadString(
                "<html><body><label><input type='checkbox'>Agree</label>"
                + "</body></html>");
            if (!PumpUntil(() => loaded, TimeSpan.FromSeconds(10)))
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.native-checkbox-probe.load-failed",
                    SecretSafeDiagnosticKind.Timeout);
                return 4;
            }

            var transport = new CefDevToolsTransport(browser);
            var adapter = new CefBrowserSemanticAdapter(
                new CefSemanticBrowser(
                    browser,
                    new CefHumanizedInput(transport)));
            var snapshot = Complete(
                adapter.CaptureSnapshotAsync(
                    new BrowserSnapshotQuery(
                        interactiveOnly: true,
                        filter: "Agree")),
                TimeSpan.FromSeconds(10));
            var checkbox = snapshot.Value?.Nodes.SingleOrDefault(node =>
                string.Equals(
                    node.Role,
                    "checkbox",
                    StringComparison.Ordinal));
            if (checkbox?.Handle is null)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.native-checkbox-probe.checkbox-missing",
                    SecretSafeDiagnosticKind.Unexpected);
                return 5;
            }

            var result = Complete(
                adapter.CheckAsync(checkbox.Handle),
                TimeSpan.FromSeconds(10));
            Console.WriteLine(
                "Native checkbox probe: "
                + result.Status.ToString().ToLowerInvariant());
            return result.Status is NativeBrowserCheckStatus.Checked ? 0 : 6;
        }
        catch (Exception error)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "browser.native-checkbox-probe.failed",
                error);
            return 7;
        }
        finally
        {
            if (browser is not null)
            {
                browser.Close(force: true);
                PumpUntil(
                    () => browser.IsClosed,
                    TimeSpan.FromSeconds(2));
            }

            if (initialized)
            {
                Cef.Shutdown();
            }

            try
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup after the isolated native probe.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup after the isolated native probe.
            }
        }
    }

    private static T Complete<T>(Task<T> task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!PumpUntil(() => task.IsCompleted, timeout))
        {
            throw new TimeoutException(
                "The native checkbox operation did not complete.");
        }

        return task.GetAwaiter().GetResult();
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < timeout)
        {
            Cef.DoMessageLoopWork();
            Thread.Sleep(TimeSpan.FromMilliseconds(5));
        }

        return condition();
    }

    private static void PumpFor(TimeSpan duration) =>
        PumpUntil(() => false, duration);
}
