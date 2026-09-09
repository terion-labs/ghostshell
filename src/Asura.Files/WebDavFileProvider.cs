using System.Net;
using System.Net.Http.Headers;

namespace Asura.Files;

/// <summary>
/// RFC 4918 adapter over a caller-configured <see cref="HttpClient"/>. The caller owns the
/// client and its authentication handler; this provider confines all locations below one URI.
/// </summary>
public sealed partial class WebDavFileProvider : IFileProvider
{
    private const long MaximumReadBytes = 64L * 1024 * 1024;
    private const int MaximumBufferSize = 1024 * 1024;
    private const int MaximumListedEntries = 10_000;
    private const long MaximumPropertyResponseBytes = 8L * 1024 * 1024;
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod MkColMethod = new("MKCOL");
    private static readonly HttpMethod CopyMethod = new("COPY");
    private static readonly HttpMethod MoveMethod = new("MOVE");
    private readonly HttpClient _client;
    private readonly WebDavFileProviderOptions _options;
    private readonly FilePageCursorStore<WebDavPageCursor> _pageCursors = new(maximumEntries: 32);

    public WebDavFileProvider(HttpClient client, WebDavFileProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options;
        ProfileId = options.ProfileId;
        Authority = options.Authority;
        Capabilities = DefaultCapabilities;
    }

    internal static FileProviderCapabilities DefaultCapabilities { get; } =
        new(
            FileProviderCapability.List
            | FileProviderCapability.Stat
            | FileProviderCapability.RangedRead
            | FileProviderCapability.StreamingWrite
            | FileProviderCapability.CreateDirectory
            | FileProviderCapability.Rename
            | FileProviderCapability.Copy
            | FileProviderCapability.Move
            | FileProviderCapability.Delete
            | FileProviderCapability.ServerSideCopy
            | FileProviderCapability.Pagination,
            FileNameComparison.ProviderDefined,
            new FileProviderLimits(
                maximumListPageSize: 1_000,
                maximumReadBytes: MaximumReadBytes,
                maximumBufferSize: MaximumBufferSize));

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public FileProviderCapabilities Capabilities { get; }

    private FileProviderResult<ResolvedWebDavLocation> ResolveLocation(
        FileLocation location,
        bool appendDirectorySlash = false)
    {
        if (location.ProviderProfileId != ProfileId || location.Authority != Authority)
        {
            return Failure<ResolvedWebDavLocation>(
                FileProviderErrorCode.InvalidLocation,
                "The location belongs to another WebDAV profile or authority.");
        }

        var path = location.Address switch
        {
            FileLocationAddress.ContainerRoot => FilePath.Root,
            FileLocationAddress.Hierarchical value => value.Path,
            _ => null,
        };
        if (path is null)
        {
            return Failure<ResolvedWebDavLocation>(
                FileProviderErrorCode.InvalidLocation,
                "WebDAV requires a hierarchical location address.");
        }

        string relative;
        try
        {
            relative = string.Join(
                '/',
                path.Segments.Select(segment => Uri.EscapeDataString(segment.Value)));
        }
        catch (UriFormatException)
        {
            return Failure<ResolvedWebDavLocation>(
                FileProviderErrorCode.InvalidName,
                "The WebDAV path contains text that cannot be encoded as a URI segment.");
        }
        if (appendDirectorySlash && !path.IsRoot)
        {
            relative += "/";
        }

        var uri = path.IsRoot ? _options.BaseUri : new Uri(_options.BaseUri, relative);
        return FileProviderResult<ResolvedWebDavLocation>.Success(
            new ResolvedWebDavLocation(location, path, uri));
    }

    private static FileProviderResult<FileVersion> ParseEtag(string? etag)
    {
        if (!EntityTagHeaderValue.TryParse(etag, out var parsed) || parsed.IsWeak)
        {
            return Failure<FileVersion>(
                FileProviderErrorCode.IoFailure,
                "The WebDAV resource did not provide a strong ETag required for safe mutations.");
        }

        try
        {
            return FileProviderResult<FileVersion>.Success(new FileVersion(parsed.ToString()));
        }
        catch (ArgumentException)
        {
            return Failure<FileVersion>(
                FileProviderErrorCode.IoFailure,
                "The WebDAV resource returned an invalid ETag.");
        }
    }

    private static FileProviderResult<T> Failure<T>(
        FileProviderErrorCode code,
        string message,
        bool retryable = false) =>
        RemoteFileProviderUtilities.Failure<T>(code, message, retryable);

    private async ValueTask<FileProviderResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<FileProviderResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<T>(FileProviderErrorCode.Cancelled, "The WebDAV operation was cancelled.");
        }
        catch (OperationCanceledException exception)
        {
            return Failure<T>(FileProviderErrorCode.IoFailure, exception.Message, retryable: true);
        }
        catch (EndOfStreamException exception)
        {
            return Failure<T>(FileProviderErrorCode.UnexpectedEndOfStream, exception.Message);
        }
        catch (System.Xml.XmlException exception)
        {
            return Failure<T>(FileProviderErrorCode.IoFailure, exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return Failure<T>(FileProviderErrorCode.IoFailure, exception.Message, retryable: true);
        }
        catch (IOException exception)
        {
            return Failure<T>(FileProviderErrorCode.IoFailure, exception.Message, retryable: true);
        }
    }

    private static FileProviderResult<T> HttpFailure<T>(
        HttpResponseMessage response,
        FileMutationPrecondition? precondition = null,
        FileProviderErrorCode methodNotAllowed = FileProviderErrorCode.UnsupportedCapability)
    {
        var message = $"WebDAV returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var code = precondition is FileMutationPrecondition.MustNotExist
                ? FileProviderErrorCode.Conflict
                : FileProviderErrorCode.PreconditionFailed;
            return Failure<T>(code, message);
        }

        var error = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => FileProviderErrorCode.InvalidLocation,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FileProviderErrorCode.AccessDenied,
            HttpStatusCode.NotFound => FileProviderErrorCode.NotFound,
            HttpStatusCode.MethodNotAllowed => methodNotAllowed,
            HttpStatusCode.Conflict => FileProviderErrorCode.Conflict,
            HttpStatusCode.RequestedRangeNotSatisfiable => FileProviderErrorCode.RangeNotSatisfiable,
            (HttpStatusCode)423 => FileProviderErrorCode.SharingViolation,
            (HttpStatusCode)507 => FileProviderErrorCode.QuotaExceeded,
            (HttpStatusCode)207 => FileProviderErrorCode.PartialTransfer,
            _ => FileProviderErrorCode.IoFailure,
        };
        var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode >= HttpStatusCode.InternalServerError;
        return Failure<T>(error, message, retryable);
    }

    private static void AddNoCacheHeaders(HttpRequestMessage request)
    {
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
        };
        request.Headers.Pragma.ParseAdd("no-cache");
    }

    private FileProviderError? ValidateResponseScope(HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !SameOrigin(_options.BaseUri, finalUri))
        {
            return FileProviderError.Create(
                FileProviderErrorCode.OutsideRoot,
                "The WebDAV request was redirected outside its configured origin.");
        }

        var rootPath = _options.BaseUri.AbsolutePath;
        var rootWithoutSlash = rootPath.TrimEnd('/');
        return string.Equals(finalUri.AbsolutePath, rootWithoutSlash
, StringComparison.Ordinal) || finalUri.AbsolutePath.StartsWith(rootPath, StringComparison.Ordinal)
            ? null
            : FileProviderError.Create(
                FileProviderErrorCode.OutsideRoot,
                "The WebDAV request was redirected outside its configured base path.");
    }

    private sealed record ResolvedWebDavLocation(
        FileLocation Location,
        FilePath Path,
        Uri Uri);

    private sealed record WebDavPageCursor(
        string Scope,
        IReadOnlyList<FileEntry> Entries,
        int Offset);
}
