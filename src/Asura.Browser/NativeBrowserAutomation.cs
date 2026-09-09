using Asura.Application;

namespace Asura.Browser;

internal sealed record NativeBrowserViewport(double WidthCss, double HeightCss)
{
    public NativeBrowserViewport Validate()
    {
        if (!double.IsFinite(WidthCss)
            || !double.IsFinite(HeightCss)
            || WidthCss <= 0
            || HeightCss <= 0
            || WidthCss > BrowserViewportState.MaximumCssExtent
            || HeightCss > BrowserViewportState.MaximumCssExtent)
        {
            throw new InvalidOperationException("CEF returned an invalid CSS viewport.");
        }

        return this;
    }
}

internal enum NativeBrowserAutomationStatus
{
    Acknowledged,
    Rejected,
    OutcomeUnknown,
}

internal sealed record NativeBrowserAutomationResult
{
    private NativeBrowserAutomationResult(
        NativeBrowserAutomationStatus status,
        string? resultJson,
        string? stableCode)
    {
        Status = status;
        ResultJson = resultJson;
        StableCode = stableCode;
    }

    public NativeBrowserAutomationStatus Status { get; }
    public string? ResultJson { get; }
    public string? StableCode { get; }

    public static NativeBrowserAutomationResult Acknowledged(string? resultJson = null) =>
        new(NativeBrowserAutomationStatus.Acknowledged, resultJson, null);

    public static NativeBrowserAutomationResult Rejected(string stableCode) =>
        new(
            NativeBrowserAutomationStatus.Rejected,
            null,
            string.IsNullOrWhiteSpace(stableCode)
                ? throw new ArgumentException("A stable rejection code is required.", nameof(stableCode))
                : stableCode);

    public static NativeBrowserAutomationResult OutcomeUnknown() =>
        new(NativeBrowserAutomationStatus.OutcomeUnknown, null, null);
}

