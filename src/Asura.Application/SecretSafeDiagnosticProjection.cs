using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

namespace Asura.Application;

/// <summary>
/// Projects failures and page-controlled console callbacks to closed diagnostic metadata.
/// Raw messages, stack traces, paths, URLs, sources, and custom type names have no
/// representation in the returned values.
/// </summary>
public static class SecretSafeDiagnosticProjection
{
    private const int MaximumStableCodeLength = 96;
    private const int MaximumReportedLine = 1_000_000;

    public static string FromException(
        string stableCode,
        Exception exception,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Format(stableCode, Classify(exception), correlationId);
    }

    public static string FromEvent(
        string stableCode,
        SecretSafeDiagnosticKind kind,
        string? correlationId = null) =>
        Format(stableCode, KindName(kind), correlationId);

    public static void WriteTrace(string stableCode, Exception exception) =>
        Trace.TraceError(FromException(stableCode, exception));

    public static void WriteStandardError(string stableCode, Exception exception) =>
        Console.Error.WriteLine(FromException(stableCode, exception));

    public static void WriteStandardError(
        string stableCode,
        SecretSafeDiagnosticKind kind) =>
        Console.Error.WriteLine(FromEvent(stableCode, kind));

    public static Task WriteStandardErrorAsync(
        string stableCode,
        SecretSafeDiagnosticKind kind)
    {
        var projection = FromEvent(stableCode, kind);
        return Console.Error.WriteLineAsync(projection);
    }

    public static void WriteTrace(
        string stableCode,
        SecretSafeDiagnosticKind kind) =>
        Trace.TraceError(FromEvent(stableCode, kind));

    public static void WriteTraceAndStandardError(
        string stableCode,
        Exception exception)
    {
        var projection = FromException(stableCode, exception);
        Console.Error.WriteLine(projection);
        Trace.TraceError(projection);
    }

    public static string FromBrowserConsole(
        BrowserConsoleDiagnosticSeverity severity,
        int line)
    {
        var stableSeverity = severity switch
        {
            BrowserConsoleDiagnosticSeverity.Warning => "warning",
            BrowserConsoleDiagnosticSeverity.Error => "error",
            BrowserConsoleDiagnosticSeverity.Fatal => "fatal",
            _ => "unknown",
        };
        var boundedLine = Math.Clamp(line, 0, MaximumReportedLine);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[asura:browser-console] code=browser.console.{stableSeverity} line={boundedLine}");
    }

    public static void WriteBrowserConsoleTrace(
        BrowserConsoleDiagnosticSeverity severity,
        int line) =>
        Trace.TraceWarning(FromBrowserConsole(severity, line));

    private static string Classify(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        UnauthorizedAccessException => "access-denied",
        SocketException => "network",
        IOException => "io",
        _ => "unexpected",
    };

    private static string KindName(SecretSafeDiagnosticKind kind) => kind switch
    {
        SecretSafeDiagnosticKind.Cancelled => "cancelled",
        SecretSafeDiagnosticKind.Timeout => "timeout",
        SecretSafeDiagnosticKind.AccessDenied => "access-denied",
        SecretSafeDiagnosticKind.Network => "network",
        SecretSafeDiagnosticKind.Io => "io",
        _ => "unexpected",
    };

    private static string Format(
        string stableCode,
        string kind,
        string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        if (stableCode.Length > MaximumStableCodeLength
            || !stableCode.All(IsStableCodeCharacter))
        {
            throw new ArgumentException(
                $"Diagnostic codes may contain only lowercase ASCII letters, digits, dots, and hyphens and be at most {MaximumStableCodeLength} characters.",
                nameof(stableCode));
        }

        var correlation = correlationId ?? Guid.NewGuid().ToString("N");
        if (correlation.Length != 32 || !correlation.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Diagnostic correlation identifiers must be 32 hexadecimal characters.",
                nameof(correlationId));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[asura:diagnostic] code={stableCode} correlation={correlation.ToLowerInvariant()} type={kind}");
    }

    private static bool IsStableCodeCharacter(char value) =>
        value is >= 'a' and <= 'z'
        || value is >= '0' and <= '9'
        || value is '.' or '-';
}

public enum BrowserConsoleDiagnosticSeverity
{
    Unknown,
    Warning,
    Error,
    Fatal,
}

public enum SecretSafeDiagnosticKind
{
    Cancelled,
    Timeout,
    AccessDenied,
    Network,
    Io,
    Unexpected,
}
