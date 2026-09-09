namespace Asura.Files;

/// <summary>
/// Identifies one WebDAV namespace rooted beneath an HTTP origin. The provider's caller-owned
/// <see cref="HttpClient"/> must use a primary handler with automatic redirects disabled.
/// </summary>
public sealed record WebDavFileProviderOptions
{
    public WebDavFileProviderOptions(
        FileProviderProfileId profileId,
        FileAuthority authority,
        Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The WebDAV base URI must be an absolute HTTP(S) URI.", nameof(baseUri));
        }

        if (!string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException(
                "The WebDAV base URI cannot contain credentials, a query, or a fragment.",
                nameof(baseUri));
        }

        ProfileId = profileId;
        Authority = authority;
        BaseUri = baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri($"{baseUri.AbsoluteUri}/", UriKind.Absolute);
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public Uri BaseUri { get; }
}
