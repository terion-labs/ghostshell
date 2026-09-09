using System.Text;

namespace Asura.Application;

public abstract record AgentWebToolResult
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private protected AgentWebToolResult()
    {
    }

    internal static string RequireBoundedText(
        string value,
        int maximumBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        try
        {
            if (value.Contains('\0', StringComparison.Ordinal)
                || StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw new ArgumentException(
                    "Web content is invalid or exceeds its byte limit.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Web content is not valid UTF-8 text.", parameterName, exception);
        }

        return string.Concat(value);
    }

    internal static string RequireFinalUrl(string value) =>
        AgentWebToolRequest.RequireHttpAddress(value, nameof(value)).AbsoluteUri;
}

public sealed record AgentHttpFetchResult : AgentWebToolResult
{
    public const int MaximumContentBytes = 64 * 1_024;
    public const int MaximumMediaTypeBytes = 256;

    public AgentHttpFetchResult(
        string finalUrl,
        int statusCode,
        string mediaType,
        string content)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        FinalUrl = RequireFinalUrl(finalUrl);
        StatusCode = statusCode;
        MediaType = RequireBoundedText(
            mediaType,
            MaximumMediaTypeBytes,
            nameof(mediaType));
        Content = RequireBoundedText(
            content,
            MaximumContentBytes,
            nameof(content));
    }

    public string FinalUrl { get; }

    public int StatusCode { get; }

    public string MediaType { get; }

    public string Content { get; }
}

public sealed record AgentWebReadResult : AgentWebToolResult
{
    public const int MaximumTitleBytes = 1_024;
    public const int MaximumContentBytes = 64 * 1_024;
    public const int MaximumLinkCount = 512;

    public AgentWebReadResult(
        string finalUrl,
        string title,
        AgentWebReadFormat format,
        string content,
        IReadOnlyList<string> links,
        bool truncated)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        FinalUrl = RequireFinalUrl(finalUrl);
        Title = RequireBoundedText(title, MaximumTitleBytes, nameof(title));
        Format = format;
        Content = RequireBoundedText(content, MaximumContentBytes, nameof(content));
        Links = NormalizeLinks(links);
        Truncated = truncated;
    }

    public string FinalUrl { get; }

    public string Title { get; }

    public AgentWebReadFormat Format { get; }

    public string Content { get; }

    public IReadOnlyList<string> Links { get; }

    public bool Truncated { get; }

    private static IReadOnlyList<string> NormalizeLinks(IReadOnlyList<string> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (links.Count > MaximumLinkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(links));
        }

        var uniqueLinks = new List<string>(links.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            var boundedLink = RequireBoundedText(
                link,
                AgentWebToolRequest.MaximumUrlBytes,
                nameof(links));
            if (!Uri.TryCreate(boundedLink, UriKind.Absolute, out var address)
                || !(address.Scheme.Equals(
                        Uri.UriSchemeHttp,
                        StringComparison.OrdinalIgnoreCase)
                    || address.Scheme.Equals(
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase))
                || string.IsNullOrWhiteSpace(address.Host)
                || !string.IsNullOrEmpty(address.UserInfo))
            {
                throw new ArgumentException(
                    "Page links must be bounded, credential-free HTTP(S) URLs.",
                    nameof(links));
            }

            var normalized = address.AbsoluteUri;
            if (seen.Add(normalized))
            {
                uniqueLinks.Add(normalized);
            }
        }

        return uniqueLinks.AsReadOnly();
    }
}

public enum AgentWebToolErrorCode
{
    Unavailable,
    InvalidUrl,
    DestinationDenied,
    DnsFailed,
    RedirectLimit,
    TimedOut,
    BodyTooLarge,
    UnsupportedContentType,
    LoadFailed,
    RenderProcessFailed,
    ExtractionFailed,
    ConverterFailed,
    SearchInterstitial,
    Cancelled,
}

public abstract record AgentWebToolExecutionResult
{
    private AgentWebToolExecutionResult()
    {
    }

    public sealed record Succeeded(AgentWebToolResult Result)
        : AgentWebToolExecutionResult;

    public sealed record Failed(AgentWebToolErrorCode Code)
        : AgentWebToolExecutionResult;
}
