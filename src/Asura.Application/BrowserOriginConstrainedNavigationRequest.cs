namespace Asura.Application;

/// <summary>
/// Closed renderer-level navigation operations that must remain inside one
/// host-selected origin for their complete top-level redirect chain.
/// </summary>
public abstract record BrowserOriginConstrainedNavigationRequest
{
    private BrowserOriginConstrainedNavigationRequest()
    {
    }

    public sealed record Navigate : BrowserOriginConstrainedNavigationRequest
    {
        public Navigate(BrowserAddress address)
        {
            Address = address
                ?? throw new ArgumentNullException(nameof(address));
        }

        public BrowserAddress Address { get; }
    }

    public sealed record Back
        : BrowserOriginConstrainedNavigationRequest;

    public sealed record Forward
        : BrowserOriginConstrainedNavigationRequest;

    public sealed record Reload
        : BrowserOriginConstrainedNavigationRequest;
}
