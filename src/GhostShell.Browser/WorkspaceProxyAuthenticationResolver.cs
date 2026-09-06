using GhostShell.Application;

namespace GhostShell.Browser;

internal interface IWorkspaceProxyAuthenticationResolver
{
    BrowserAuthenticationCredentials? Resolve(BrowserAuthenticationChallenge challenge);
}

internal sealed class WorkspaceProxyAuthenticationResolver(
    Uri endpoint,
    WorkspaceNetworkProxyCredentials credentials) :
    IWorkspaceProxyAuthenticationResolver
{
    private readonly Uri _endpoint = endpoint
        ?? throw new ArgumentNullException(nameof(endpoint));
    private readonly WorkspaceNetworkProxyCredentials _credentials = credentials
        ?? throw new ArgumentNullException(nameof(credentials));

    public BrowserAuthenticationCredentials? Resolve(
        BrowserAuthenticationChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        return challenge.IsProxy
            && string.Equals(
                challenge.Host.Trim().TrimEnd('.'),
                _endpoint.Host,
                StringComparison.OrdinalIgnoreCase)
            && challenge.Port == _endpoint.Port
            && string.Equals(challenge.Scheme, "basic", StringComparison.OrdinalIgnoreCase)
                ? new BrowserAuthenticationCredentials(
                    _credentials.Username,
                    _credentials.Password)
                : null;
    }
}
