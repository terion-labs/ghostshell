using System.Security.Cryptography;
using System.Text;

namespace Asura.SoakAcceptance;

internal static class SoakCatalog
{
    public const string Version = "1.0";

    public static IReadOnlyList<SoakScenario> Scenarios { get; } =
    [
        new("reconnect-reattach", "Reconnect and reattach", "cycles", "Disconnect and reconnect active sessions, including reattaching a detached session. Confirm every cycle preserves the real connection state and leaves no duplicate session or attachment.", 0),
        new("startup-crash-restore", "Startup and crash restore", "cycles", "Alternate clean startup/exit cycles with the runner-controlled abrupt exit. After relaunch, confirm recovery never invents completed work, success, approval, or authority.", 1),
        new("many-tabs-panels", "Many tabs and panels", "panels", "Open, exercise, rearrange, and close the required number of mixed terminal, file, browser, database, and agent panels.", 0),
        new("bounded-scrollback", "Bounded scrollback", "lines", "Generate at least the required terminal lines, navigate the retained history, and confirm the configured scrollback remains responsive and bounded.", 0),
        new("provider-failure-noncooperation", "Provider failure and non-cooperation", "failures", "Exercise timeout, error, cancellation, and one non-cooperating provider generation. Confirm stale generations cannot publish results or retain authority.", 0),
        new("cef-renderer-replacement", "CEF renderer replacement", "replacements", "Crash or terminate the browser renderer using the documented developer path and confirm replacement without fabricated navigation success or a retained renderer.", 0),
        new("mcp-failure-cleanup", "MCP failure and cleanup", "failures", "Exercise MCP startup failure, timeout, cancellation, and non-cooperating server cleanup. Confirm the UI reports uncertainty and no captured server remains.", 0),
        new("sleep-wake", "Sleep and wake", "cycles", "Put the reference Mac to sleep and wake it for the required cycles while sessions and panels are active; verify accurate reconnect and recovery state after each wake.", 0),
        new("quick-terminal-cycles", "Quick Terminal focus cycles", "cycles", "Toggle, focus, type into, dismiss, and restore Quick Terminal for the required cycles. Confirm focus returns correctly and no hidden instance accumulates.", 0),
        new("native-view-open-close", "Native view lifecycle", "cycles", "Repeatedly open and close native terminal, browser, preview, database, and agent views. Confirm every view and cancellation source is released.", 0),
    ];

    public static string Sha256 { get; } = ComputeSha256();

    private static string ComputeSha256()
    {
        var canonical = string.Join('\n', Scenarios.Select(s =>
            $"{Version}|{s.Id}|{s.Title}|{s.LoadUnit}|{s.ExpectedAbruptExits}|{s.Instructions}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
