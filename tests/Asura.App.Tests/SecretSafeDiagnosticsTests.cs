using System.Diagnostics;
using Asura.SecurityCampaign.Tests;

namespace Asura.App.Tests;

public sealed class SecretSafeDiagnosticsTests
{
    [Fact(DisplayName = "secrecy.app-diagnostic-adapter actual stderr and Trace adapters redact shared canaries")]
    [Trait("SecurityCampaignCase", "secrecy.app-diagnostic-adapter")]
    public void AppAdapterWritesOnlyClosedExceptionProjection()
    {
        using var traceOutput = new StringWriter();
        using var standardError = new StringWriter();
        using var listener = new TextWriterTraceListener(traceOutput);
        var originalError = Console.Error;
        Trace.Listeners.Add(listener);
        try
        {
            Console.SetError(standardError);
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "terminal.input.failed",
                new InvalidOperationException(SecurityCampaignCanaries.Joined));
            Trace.Flush();
        }
        finally
        {
            Console.SetError(originalError);
            Trace.Listeners.Remove(listener);
        }

        AssertClosed(traceOutput.ToString());
        AssertClosed(standardError.ToString());
    }

    private static void AssertClosed(string diagnostic)
    {
        Assert.Contains("code=terminal.input.failed", diagnostic, StringComparison.Ordinal);
        Assert.Contains("type=unexpected", diagnostic, StringComparison.Ordinal);
        Assert.All(
            SecurityCampaignCanaries.Values,
            canary => Assert.DoesNotContain(canary, diagnostic, StringComparison.Ordinal));
        Assert.DoesNotContain(
            nameof(InvalidOperationException),
            diagnostic,
            StringComparison.Ordinal);
    }
}
