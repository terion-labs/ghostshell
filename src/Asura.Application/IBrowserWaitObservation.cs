namespace Asura.Application;

/// <summary>
/// Read-only semantic observations needed by browser waits. Implementations
/// preserve the opaque reference's exact document binding and never dispatch
/// page input.
/// </summary>
public interface IBrowserWaitObservation
{
    ValueTask BeginNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    ValueTask EndNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    ValueTask<BrowserResult<BrowserElementStateSnapshot>>
        ReadElementStateAsync(
            BrowserElementReference reference,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            BrowserResult<BrowserElementStateSnapshot>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.UnsupportedCapability,
                    "Browser element-state observation is not implemented.")));

    ValueTask<BrowserResult<BrowserNetworkActivitySnapshot>>
        ReadNetworkActivityAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            BrowserResult<BrowserNetworkActivitySnapshot>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.UnsupportedCapability,
                    "Browser network-idle observation is not implemented.")));
}
