using System.Net;

namespace Asura.Browser.Tests;

internal static class BrowserTestDestinationPolicy
{
    private static readonly IPAddress PublicAddress =
        IPAddress.Parse("93.184.216.34");

    public static BrowserDestinationPolicy Public { get; } =
        BrowserDestinationPolicy.CreateLocal(ResolvePublicAsync);

    private static ValueTask<IPAddress[]> ResolvePublicAsync(
        string host,
        CancellationToken cancellationToken)
    {
        _ = host;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPAddress[]>([PublicAddress]);
    }
}
