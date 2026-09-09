namespace Asura.Application;

/// <summary>
/// Typed low-level browser automation. Raw CDP is intentionally not exposed.
/// </summary>
public interface IOriginConstrainedBrowserAutomation
{
    ValueTask<BrowserResult<BrowserAutomationReceipt>> DispatchMouseWithinOriginAsync(
        BrowserMouseRequest request,
        BrowserNavigationOrigin allowedOrigin,
        CancellationToken cancellationToken) => Unsupported<BrowserAutomationReceipt>();

    ValueTask<BrowserResult<BrowserAutomationReceipt>> DispatchKeyWithinOriginAsync(
        BrowserKeyRequest request,
        BrowserNavigationOrigin allowedOrigin,
        CancellationToken cancellationToken) => Unsupported<BrowserAutomationReceipt>();

    ValueTask<BrowserResult<BrowserAutomationReceipt>> ScrollWithinOriginAsync(
        BrowserScrollRequest request,
        BrowserNavigationOrigin allowedOrigin,
        CancellationToken cancellationToken) => Unsupported<BrowserAutomationReceipt>();

    ValueTask<BrowserResult<BrowserEvaluationResult>> EvaluateWithinOriginAsync(
        BrowserEvaluateRequest request,
        BrowserNavigationOrigin allowedOrigin,
        CancellationToken cancellationToken) => Unsupported<BrowserEvaluationResult>();

    private static ValueTask<BrowserResult<T>> Unsupported<T>() =>
        ValueTask.FromResult(BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.UnsupportedCapability,
                "Typed low-level browser automation is unavailable.")));
}
