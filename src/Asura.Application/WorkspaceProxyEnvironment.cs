namespace Asura.Application;

/// <summary>
/// Projects the workspace broker into proxy-aware child processes. Empty bypass
/// values override an inherited NO_PROXY; omitting the keys would retain it.
/// </summary>
public static class WorkspaceProxyEnvironment
{
    public static IReadOnlyDictionary<string, string> Create(
        IReadOnlyDictionary<string, string> environment,
        Uri endpoint,
        WorkspaceNetworkProxyCredentials? credentials,
        Uri? httpEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        // Only use an HTTP endpoint supplied by the broker. An arbitrary SOCKS
        // service cannot be converted to HTTP by changing its URI scheme.
        var proxy = AuthenticatedEndpoint(httpEndpoint ?? endpoint, credentials).AbsoluteUri;
        return new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            ["ALL_PROXY"] = proxy,
            ["all_proxy"] = proxy,
            ["HTTP_PROXY"] = proxy,
            ["http_proxy"] = proxy,
            ["HTTPS_PROXY"] = proxy,
            ["https_proxy"] = proxy,
            ["NO_PROXY"] = string.Empty,
            ["no_proxy"] = string.Empty,
        };
    }

    public static Uri AuthenticatedEndpoint(
        Uri endpoint,
        WorkspaceNetworkProxyCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return credentials is null ? endpoint : new UriBuilder(endpoint)
        {
            UserName = credentials.Username,
            Password = credentials.Password,
        }.Uri;
    }
}
