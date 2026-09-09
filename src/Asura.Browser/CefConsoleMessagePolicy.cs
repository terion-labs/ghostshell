using Asura.Application;
using Exclr8Cef;

namespace Asura.Browser;

/// <summary>
/// Projects page-controlled console callbacks to closed diagnostic metadata.
/// Message and source values are deliberately never forwarded to a sink.
/// </summary>
internal static class CefConsoleMessagePolicy
{
    public static void Handle(object? sender, ConsoleMessageEventArgs message)
    {
        _ = sender;
        if (TryProject(message, out var severity, out var line))
        {
            SecretSafeDiagnosticProjection.WriteBrowserConsoleTrace(
                severity,
                line);
        }
    }

    public static void Handle(
        ConsoleMessageEventArgs message,
        Action<string> writeDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(writeDiagnostic);
        if (!TryProject(message, out var severity, out var line))
        {
            return;
        }

        writeDiagnostic(SecretSafeDiagnosticProjection.FromBrowserConsole(
            severity,
            line));
    }

    private static bool TryProject(
        ConsoleMessageEventArgs message,
        out BrowserConsoleDiagnosticSeverity severity,
        out int line)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Level < Cef.CefLogSeverity.Warning)
        {
            severity = default;
            line = default;
            return false;
        }

        severity = message.Level switch
        {
            Cef.CefLogSeverity.Warning => BrowserConsoleDiagnosticSeverity.Warning,
            Cef.CefLogSeverity.Error => BrowserConsoleDiagnosticSeverity.Error,
            Cef.CefLogSeverity.Fatal => BrowserConsoleDiagnosticSeverity.Fatal,
            _ => BrowserConsoleDiagnosticSeverity.Unknown,
        };
        line = message.Line;
        return true;
    }
}
