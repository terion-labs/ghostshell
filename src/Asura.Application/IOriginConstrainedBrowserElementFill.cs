namespace Asura.Application;

/// <summary>
/// Replaces the value of the exact editable element represented by a
/// document-bound reference while containing any synchronously attributable
/// top-level navigation to one host-selected origin.
/// </summary>
public interface IOriginConstrainedBrowserElementFill
{
    ValueTask<BrowserResult<BrowserFillReceipt>> FillWithinOriginAsync(
        BrowserElementReference reference,
        string text,
        BrowserNavigationOrigin allowedOrigin,
        CancellationToken cancellationToken);
}
