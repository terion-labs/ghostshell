using Asura.Application;

namespace Asura.App;

/// <summary>
/// Converts exceptions at normal diagnostic boundaries to closed metadata.
/// Exception messages, stack traces, paths, URLs, and custom type names never
/// cross this projection.
/// </summary>
internal static class SecretSafeDiagnostics
{
    public static void WriteTrace(string stableCode, Exception exception) =>
        SecretSafeDiagnosticProjection.WriteTrace(stableCode, exception);

    public static void WriteTraceAndStandardError(
        string stableCode,
        Exception exception)
        => SecretSafeDiagnosticProjection.WriteTraceAndStandardError(
            stableCode,
            exception);

    internal static string Project(
        string stableCode,
        Exception exception,
        string? correlationId = null) =>
        SecretSafeDiagnosticProjection.FromException(
            stableCode,
            exception,
            correlationId);
}
